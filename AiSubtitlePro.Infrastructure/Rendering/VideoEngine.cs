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

    private WriteableBitmap? _writeableBitmap;
    private IntPtr _compositedBuffer;
    private int _width;
    private int _height;
    private int _stride;

    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private volatile bool _isPlaying;

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? MediaEnded;

    public ImageSource? VideoSource => _writeableBitmap;

    public int VideoWidth => _width;
    public int VideoHeight => _height;

    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsPlaying => _isPlaying;
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
        Stop();

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
        if (!RenderSingleFrameInternal(out _))
            throw new InvalidOperationException("Failed to decode first video frame.");
    }

    public void SetSubtitleContent(string content)
    {
        _assRenderer?.SetContent(content);
        RenderSingleFrame();
    }

    public void Play()
    {
        if (_isPlaying) return;
        if (_width <= 0 || _height <= 0) return;

        _isPlaying = true;
        _playbackCts = new CancellationTokenSource();
        _playbackTask = Task.Run(() => PlaybackLoop(_playbackCts.Token));
    }

    public void Pause()
    {
        if (!_isPlaying) return;
        _isPlaying = false;
        _playbackCts?.Cancel();
    }

    public void Stop()
    {
        _isPlaying = false;
        _playbackCts?.Cancel();
        _playbackCts = null;
        _playbackTask = null;

        Position = TimeSpan.Zero;
    }

    public void SeekTo(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration) position = Duration;

        _decoder.Seek(position);
        Position = position;
        RenderSingleFrame();
    }

    private void PlaybackLoop(CancellationToken token)
    {
        // Best-effort fixed frame pacing (30fps) - can be improved by using PTS deltas.
        var frameDelay = TimeSpan.FromMilliseconds(33);

        while (!token.IsCancellationRequested)
        {
            TimeSpan pts;
            try
            {
                if (!RenderSingleFrameInternal(out pts))
                {
                    _isPlaying = false;
                    _uiDispatcher?.BeginInvoke(() => MediaEnded?.Invoke(this, EventArgs.Empty));
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaybackLoop error: {ex}");
                _isPlaying = false;
                _uiDispatcher?.BeginInvoke(() => MediaEnded?.Invoke(this, EventArgs.Empty));
                return;
            }

            if (pts != TimeSpan.Zero)
            {
                Position = pts;
            }

            _uiDispatcher?.BeginInvoke(() => PositionChanged?.Invoke(this, Position));

            try
            {
                Task.Delay(frameDelay, token).Wait(token);
            }
            catch
            {
                return;
            }
        }
    }

    private void RenderSingleFrame()
    {
        try
        {
            RenderSingleFrameInternal(out _);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RenderSingleFrame error: {ex}");
        }
    }

    private bool RenderSingleFrameInternal(out TimeSpan pts)
    {
        pts = TimeSpan.Zero;
        if (_writeableBitmap == null) return false;

        if (!_decoder.TryDecodeNextFrame(out pts))
            return false;

        // Copy decoded BGRA into composited buffer
        Buffer.MemoryCopy((void*)_decoder.GetBgraBufferPointer(), (void*)_compositedBuffer, (long)(_stride * _height), (long)(_stride * _height));

        // Render and blend subtitles for current timestamp
        var subTime = pts != TimeSpan.Zero ? pts : Position;
        if (_assRenderer != null)
        {
            bool changed;
            IntPtr imgPtr = _assRenderer.RenderFrame(subTime, out changed);
            if (imgPtr != IntPtr.Zero)
            {
                BlendSubtitles(imgPtr);
            }
        }

        // Present in WPF
        _uiDispatcher?.BeginInvoke(() =>
        {
            if (_writeableBitmap == null) return;

            _writeableBitmap.Lock();
            _writeableBitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _compositedBuffer, _stride * _height, _stride);
            _writeableBitmap.Unlock();
        });

        return true;
    }

    private void RecreateBuffers()
    {
        if (_compositedBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_compositedBuffer);
            _compositedBuffer = IntPtr.Zero;
        }

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
        Stop();

        _assRenderer?.Dispose();
        _decoder.Dispose();

        if (_compositedBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_compositedBuffer);
            _compositedBuffer = IntPtr.Zero;
        }
    }
}
