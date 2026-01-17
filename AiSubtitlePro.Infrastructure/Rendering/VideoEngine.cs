using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiSubtitlePro.Infrastructure.Media;
using AiSubtitlePro.Infrastructure.Native.LibAss;

namespace AiSubtitlePro.Infrastructure.Rendering;

public unsafe class VideoEngine : IDisposable
{
    private readonly Dispatcher? _uiDispatcher;
    private readonly AssRenderer? _assRenderer;
    private readonly FfmpegVideoDecoder _decoder;

    private readonly object _renderLock = new();

    private WriteableBitmap? _writeableBitmap;
    private IntPtr _videoFrameBuffer;
    private IntPtr _compositedBuffer;
    private int _width;
    private int _height;
    private int _stride;

    private int _renderGeneration;
    private bool _isDisposed;

    // Audio-master model: VideoEngine has no internal clock.
    // Rendering is driven by an external master time (audio device clock).

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? MediaEnded;

    public ImageSource? VideoSource => _writeableBitmap;

    public int VideoWidth => _width;
    public int VideoHeight => _height;

    public double FrameRate => _decoder.FrameRate;

    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsPlaying => false;
    public int Volume { get; set; } = 100;
    public bool IsMuted { get; set; }

    public VideoEngine()
    {
        _uiDispatcher = Application.Current?.Dispatcher;
        try
        {
            _assRenderer = new AssRenderer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AssRenderer init failed (subtitles disabled): {ex}");
            _assRenderer = null;
        }
        _decoder = new FfmpegVideoDecoder();
    }

    public void LoadMedia(string path)
    {
        _decoder.Open(path);

        _width = _decoder.Width;
        _height = _decoder.Height;
        _stride = _decoder.Stride;
        Duration = _decoder.Duration;
        Position = TimeSpan.Zero;

        RecreateBuffers();
        _assRenderer?.SetSize(_width, _height);

        // Render first frame for immediate preview
        _decoder.Seek(TimeSpan.Zero);
        _ptsPrev = TimeSpan.Zero;
        _ptsCurr = TimeSpan.Zero;
        if (!DecodeNextIntoCurrent(out var firstPts))
            throw new InvalidOperationException("Failed to decode first video frame.");

        // Store decoded BGRA into our current buffer.
        _ptsCurr = firstPts;
        Buffer.MemoryCopy((void*)_decoder.GetBgraBufferPointer(), (void*)_frameCurr, (long)(_stride * _height), (long)(_stride * _height));

        // Initialize prev frame to current for stable selection.
        _ptsPrev = _ptsCurr;
        Buffer.MemoryCopy((void*)_frameCurr, (void*)_framePrev, (long)(_stride * _height), (long)(_stride * _height));

        RenderAtSync(TimeSpan.Zero);
    }

    public void RenderAtSync(TimeSpan masterTime)
    {
        if (_writeableBitmap == null) return;
        if (_frameCurr == IntPtr.Zero || _framePrev == IntPtr.Zero || _compositedBuffer == IntPtr.Zero) return;
        if (_isDisposed) return;

        if (masterTime < TimeSpan.Zero) masterTime = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && masterTime > Duration) masterTime = Duration;

        EnsureDecodedUpTo(masterTime);

        Position = masterTime;
        PositionChanged?.Invoke(this, Position);

        lock (_renderLock)
        {
            var usePrev = (_ptsCurr > masterTime) && (_ptsPrev != TimeSpan.Zero);
            var src = usePrev ? _framePrev : _frameCurr;

            Buffer.MemoryCopy((void*)src, (void*)_compositedBuffer, (long)(_stride * _height), (long)(_stride * _height));

            if (_assRenderer != null)
            {
                bool changed;
                var imgPtr = _assRenderer.RenderFrame(masterTime, out changed);
                if (imgPtr != IntPtr.Zero)
                    BlendSubtitles(imgPtr);
            }
        }

        // Capture locals so Dispose/Unload can't null/zero them while a UI callback is pending.
        var wb = _writeableBitmap;
        var buffer = _compositedBuffer;
        var width = _width;
        var height = _height;
        var stride = _stride;
        var gen = _renderGeneration;

        _uiDispatcher?.Invoke(() =>
        {
            try
            {
                if (_isDisposed) return;
                if (gen != _renderGeneration) return;
                if (!ReferenceEquals(wb, _writeableBitmap)) return;
                if (buffer != _compositedBuffer) return;
                if (wb == null) return;
                if (buffer == IntPtr.Zero) return;
                if (width <= 0 || height <= 0 || stride <= 0) return;

                wb.Lock();
                wb.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride * height, stride);
                wb.Unlock();
            }
            catch
            {
            }
        });
    }

    public void SetSubtitleContent(string content)
    {
        if (_assRenderer == null) return;

        lock (_renderLock)
        {
            _assRenderer.SetContent(content);
        }

        // Audio-master model: re-render at current Position immediately (cheap; reuses cached frame).
        RenderAt(Position);
    }

    // Frame selection state (double buffer)
    private IntPtr _framePrev;
    private IntPtr _frameCurr;
    private TimeSpan _ptsPrev;
    private TimeSpan _ptsCurr;

    public void SeekTo(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration) position = Duration;

        _decoder.Seek(position);
        Position = position;

        // Flush our selection buffers and decode one frame so RenderAt has something to show.
        _ptsPrev = TimeSpan.Zero;
        _ptsCurr = TimeSpan.Zero;
        if (DecodeNextIntoCurrent(out var firstPts))
        {
            _ptsCurr = firstPts;
            Buffer.MemoryCopy((void*)_decoder.GetBgraBufferPointer(), (void*)_frameCurr, (long)(_stride * _height), (long)(_stride * _height));
            _ptsPrev = _ptsCurr;
            Buffer.MemoryCopy((void*)_frameCurr, (void*)_framePrev, (long)(_stride * _height), (long)(_stride * _height));
        }

        RenderAt(position);
    }

    /// <summary>
    /// Audio-master rendering entry point.
    /// Select the video frame with the closest PTS <= masterTime and render subtitles at masterTime.
    /// Drops frames if needed to keep up.
    /// </summary>
    public void RenderAt(TimeSpan masterTime)
    {
        if (_writeableBitmap == null) return;
        if (_frameCurr == IntPtr.Zero || _framePrev == IntPtr.Zero || _compositedBuffer == IntPtr.Zero) return;
        if (_isDisposed) return;

        if (masterTime < TimeSpan.Zero) masterTime = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && masterTime > Duration) masterTime = Duration;

        // Drive decode forward to masterTime.
        EnsureDecodedUpTo(masterTime);

        Position = masterTime;
        PositionChanged?.Invoke(this, Position);

        lock (_renderLock)
        {
            var usePrev = (_ptsCurr > masterTime) && (_ptsPrev != TimeSpan.Zero);
            var src = usePrev ? _framePrev : _frameCurr;

            Buffer.MemoryCopy((void*)src, (void*)_compositedBuffer, (long)(_stride * _height), (long)(_stride * _height));

            if (_assRenderer != null)
            {
                bool changed;
                var imgPtr = _assRenderer.RenderFrame(masterTime, out changed);
                if (imgPtr != IntPtr.Zero)
                    BlendSubtitles(imgPtr);
            }
        }

        // Capture locals so Dispose/Unload can't null/zero them while a UI callback is pending.
        var wb = _writeableBitmap;
        var buffer = _compositedBuffer;
        var width = _width;
        var height = _height;
        var stride = _stride;
        var gen = _renderGeneration;

        _uiDispatcher?.BeginInvoke(() =>
        {
            try
            {
                if (_isDisposed) return;
                if (gen != _renderGeneration) return;
                if (!ReferenceEquals(wb, _writeableBitmap)) return;
                if (buffer != _compositedBuffer) return;
                if (wb == null) return;
                if (buffer == IntPtr.Zero) return;
                if (width <= 0 || height <= 0 || stride <= 0) return;

                wb.Lock();
                wb.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride * height, stride);
                wb.Unlock();
            }
            catch
            {
            }
        });
    }

    private void EnsureDecodedUpTo(TimeSpan masterTime)
    {
        // Decode forward until current PTS >= masterTime (then we can choose prev or curr).
        // If decode runs ahead, we will pick prev.
        while (_ptsCurr < masterTime)
        {
            if (!DecodeNextIntoCurrent(out var pts))
            {
                MediaEnded?.Invoke(this, EventArgs.Empty);
                return;
            }

            // shift current to prev (swap buffers)
            var tmp = _framePrev;
            _framePrev = _frameCurr;
            _frameCurr = tmp;
            _ptsPrev = _ptsCurr;
            _ptsCurr = pts;

            // Copy decoded BGRA into new current buffer
            Buffer.MemoryCopy((void*)_decoder.GetBgraBufferPointer(), (void*)_frameCurr, (long)(_stride * _height), (long)(_stride * _height));
        }
    }

    private bool DecodeNextIntoCurrent(out TimeSpan pts)
    {
        pts = TimeSpan.Zero;
        try
        {
            return _decoder.TryDecodeNextFrame(out pts);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DecodeNextIntoCurrent error: {ex}");
            return false;
        }
    }

    private void RecreateBuffers()
    {
        _renderGeneration++;

        if (_compositedBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_compositedBuffer);
            _compositedBuffer = IntPtr.Zero;
        }

        if (_videoFrameBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_videoFrameBuffer);
            _videoFrameBuffer = IntPtr.Zero;
        }

        if (_framePrev != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_framePrev);
            _framePrev = IntPtr.Zero;
        }

        if (_frameCurr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameCurr);
            _frameCurr = IntPtr.Zero;
        }

        // Keep the old field for backward compatibility, but use our own double buffers.
        _videoFrameBuffer = Marshal.AllocHGlobal(_stride * _height);
        _framePrev = Marshal.AllocHGlobal(_stride * _height);
        _frameCurr = Marshal.AllocHGlobal(_stride * _height);
        _compositedBuffer = Marshal.AllocHGlobal(_stride * _height);

        _uiDispatcher?.Invoke(() =>
        {
            _writeableBitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        });
    }

    private void BlendSubtitles(IntPtr imgPtr)
    {
        // Iterate linked list of ASS_Image
        while (imgPtr != IntPtr.Zero)
        {
            var img = Marshal.PtrToStructure<LibAss.ASS_Image>(imgPtr);
            if (img.w > 0 && img.h > 0)
            {
                BlendImage(img);
            }
            imgPtr = img.next;
        }
    }

    private void BlendImage(LibAss.ASS_Image img)
    {
        // Simple Alpha Blending
        // Dst = Video Buffer (Bgra32)
        // Src = ASS Bitmap (Grayscale mask + Color)
        
        // Extract color components from uint (AABBGGRR - LibAss usually outputs this, but we need to check endianness)
        // LibAss color is Big Endian RGBA? Or local?
        // Actually LibAss docs say: "color is RGBA"
        
        // Decompose color
        uint color = img.color;
        byte r = (byte)((color >> 24) & 0xFF);
        byte g = (byte)((color >> 16) & 0xFF);
        byte b = (byte)((color >> 8) & 0xFF);
        byte a = (byte)(color & 0xFF); // Setup alpha (255 - transparency) -> wait, ASS uses 0=opaque, 255=transparent?
        // Ass spec: Alpha is 0 (opaque) to 255 (transparent).
        
        // Wait, normally we want Alpha 0-255 where 255 is opaque. 
        // Let's assume standard compositing: we need to invert ASS alpha.
        byte alphaStart = (byte)(255 - a);

        byte* src = (byte*)img.bitmap;
        // Dst is BGRA (Windows bitmap)
        byte* dstBase = (byte*)_compositedBuffer;
        
        int dstStride = _stride;
        int srcStride = img.stride;

        for (int y = 0; y < img.h; y++)
        {
            int dstY = img.dst_y + y;
            if (dstY < 0 || dstY >= _height) continue;

            byte* dstRow = dstBase + (dstY * dstStride);
            byte* srcRow = src + (y * srcStride);

            for (int x = 0; x < img.w; x++)
            {
                int dstX = img.dst_x + x;
                if (dstX < 0 || dstX >= _width) continue;

                // Source Alpha from bitmap (0-255 coverage)
                byte coverage = srcRow[x];
                
                // Final Alpha = (Coverage * FontAlpha) / 255
                int finalAlpha = (coverage * alphaStart) / 255;
                
                if (finalAlpha > 0)
                {
                    byte* pDst = dstRow + (dstX * 4);
                    // BGRA
                    byte oldB = pDst[0];
                    byte oldG = pDst[1];
                    byte oldR = pDst[2];
                    
                    // Standard Alpha Blend
                    // Out = (Src * A + Dst * (255 - A)) / 255
                    int invA = 255 - finalAlpha;
                    
                    pDst[0] = (byte)((b * finalAlpha + oldB * invA) / 255);
                    pDst[1] = (byte)((g * finalAlpha + oldG * invA) / 255);
                    pDst[2] = (byte)((r * finalAlpha + oldR * invA) / 255);
                    // pDst[3] remains 255 (Opaque video)
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _renderGeneration++;

        _assRenderer?.Dispose();
        _decoder.Dispose();

        _writeableBitmap = null;
        _width = 0;
        _height = 0;
        _stride = 0;

        if (_compositedBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_compositedBuffer);
            _compositedBuffer = IntPtr.Zero;
        }

        if (_videoFrameBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_videoFrameBuffer);
            _videoFrameBuffer = IntPtr.Zero;
        }

        if (_framePrev != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_framePrev);
            _framePrev = IntPtr.Zero;
        }

        if (_frameCurr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameCurr);
            _frameCurr = IntPtr.Zero;
        }
    }
}
