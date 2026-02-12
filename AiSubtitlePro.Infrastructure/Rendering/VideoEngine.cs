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

    private readonly D3DImageRenderer? _d3dImageRenderer;
    private bool _useGpu;

    private readonly object _renderLock = new();

    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private int _decodeSeq;
    private volatile int _decodeSeqRequested;
    private long _decodeTargetTicks;

    private static readonly TimeSpan PlaybackDecodeAhead = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PlaybackDecodeLowWater = TimeSpan.FromMilliseconds(80);

    private CancellationTokenSource? _playDecodeCts;
    private Task? _playDecodeTask;

    private WriteableBitmap? _writeableBitmap;
    private IntPtr _videoFrameBuffer;
    private IntPtr _compositedBuffer;
    private int _width;
    private int _height;
    private int _stride;

    private int _renderGeneration;

    private int _uiUploadQueued;
    private IntPtr _uiUploadBuffer;
    private int _uiUploadWidth;
    private int _uiUploadHeight;
    private int _uiUploadStride;
    private int _uiUploadGen;
    private bool _uiUploadUseGpu;
    private bool _isDisposed;

    private bool _endOfStream;
    private int _mediaEndedRaised;

    private readonly object _frameCacheLock = new();
    private readonly Dictionary<long, byte[]> _frameCache = new();
    private readonly LinkedList<long> _frameCacheLru = new();
    private const int FrameCacheMax = 90;

    // Audio-master model: VideoEngine has no internal clock.
    // Rendering is driven by an external master time (audio device clock).

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? MediaEnded;

    public ImageSource? VideoSource => _useGpu ? _d3dImageRenderer?.ImageSource : _writeableBitmap;

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

        try
        {
            _d3dImageRenderer = new D3DImageRenderer();
            _useGpu = true;
        }
        catch
        {
            _d3dImageRenderer = null;
            _useGpu = false;
        }
    }

    public void LoadMedia(string path)
    {
        // Prevent background decode from touching decoder while we reinitialize.
        _decodeGate.Wait();
        try
        {
        _endOfStream = false;
        _mediaEndedRaised = 0;

        _decoder.Open(path);

        _width = _decoder.Width;
        _height = _decoder.Height;
        _stride = _decoder.Stride;
        Duration = _decoder.Duration;
        Position = TimeSpan.Zero;

        RecreateBuffers();
        _assRenderer?.SetSize(_width, _height);

        if (_useGpu && _d3dImageRenderer != null)
        {
            try
            {
                _uiDispatcher?.Invoke(() => _d3dImageRenderer.Initialize(_width, _height));
            }
            catch
            {
                _useGpu = false;
            }
        }

        lock (_frameCacheLock)
        {
            _frameCache.Clear();
            _frameCacheLru.Clear();
        }

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
        finally
        {
            _decodeGate.Release();
        }
    }

    public void StartPlaybackDecodeLoop(Func<TimeSpan> getMasterTime)
    {
        if (_isDisposed) return;
        if (_playDecodeTask != null) return;

        _playDecodeCts = new CancellationTokenSource();
        var token = _playDecodeCts.Token;

        _playDecodeTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_endOfStream)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    if (!_decodeGate.Wait(0))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    try
                    {
                        var master = getMasterTime();
                        if (master < TimeSpan.Zero) master = TimeSpan.Zero;
                        if (Duration > TimeSpan.Zero && master > Duration) master = Duration;

                        var target = master + PlaybackDecodeAhead;
                        if (Duration > TimeSpan.Zero && target > Duration) target = Duration;

                        // If we are already sufficiently ahead, back off.
                        var ahead = _ptsCurr - master;
                        if (ahead >= PlaybackDecodeLowWater)
                        {
                            Thread.Sleep(2);
                            continue;
                        }

                        EnsureDecodedUpTo(target);

                        if (_endOfStream)
                        {
                            // We reached the end; no need to keep spinning.
                            Thread.Sleep(10);
                        }
                    }
                    finally
                    {
                        _decodeGate.Release();
                    }
                }
                catch
                {
                    Thread.Sleep(5);
                }
            }
        }, token);
    }

    public void StopPlaybackDecodeLoop()
    {
        try { _playDecodeCts?.Cancel(); } catch { }
        _playDecodeCts = null;
        _playDecodeTask = null;
    }

    private void RequestDecodeUpTo(TimeSpan masterTime)
    {
        if (_isDisposed) return;

        if (masterTime < TimeSpan.Zero) masterTime = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && masterTime > Duration) masterTime = Duration;

        // Coalesce: always keep only the latest target.
        _decodeTargetTicks = masterTime.Ticks;
        _decodeSeqRequested = Interlocked.Increment(ref _decodeSeq);

        _ = Task.Run(() =>
        {
            var mySeq = _decodeSeqRequested;

            if (!_decodeGate.Wait(0))
                return;

            try
            {
                // Decode to the most recent request. If newer arrives while decoding, loop once more.
                while (true)
                {
                    var target = new TimeSpan(Interlocked.Read(ref _decodeTargetTicks));
                    EnsureDecodedUpTo(target);

                    // If no newer request since we started this iteration, we're done.
                    if (mySeq == _decodeSeqRequested)
                        break;

                    mySeq = _decodeSeqRequested;
                }
            }
            catch
            {
            }
            finally
            {
                _decodeGate.Release();
            }
        });
    }

    public void RenderAtSync(TimeSpan masterTime)
    {
        if (!_useGpu && _writeableBitmap == null) return;
        if (_frameCurr == IntPtr.Zero || _framePrev == IntPtr.Zero || _compositedBuffer == IntPtr.Zero) return;
        if (_isDisposed) return;

        masterTime = ClampToDecodeableTime(masterTime);

        // Do not block the UI thread for decode. Ask background worker to decode up to this time.
        RequestDecodeUpTo(masterTime);

        Position = masterTime;
        _uiDispatcher?.BeginInvoke(() =>
        {
            try { PositionChanged?.Invoke(this, Position); } catch { }
        });

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

        // Upload using the same coalesced UI path as RenderAt to avoid blocking.
        QueueUiUpload(_compositedBuffer, _width, _height, _stride, _renderGeneration, _useGpu);
    }

    public void SetSubtitleContent(string content)
    {
        if (_assRenderer == null) return;

        lock (_renderLock)
        {
            _assRenderer.SetContent(content);
        }
    }

    // Frame selection state (double buffer)
    private IntPtr _framePrev;
    private IntPtr _frameCurr;
    private TimeSpan _ptsPrev;
    private TimeSpan _ptsCurr;

    public void SeekTo(TimeSpan position)
    {
        StopPlaybackDecodeLoop();

        // Prevent background decode from touching decoder while we perform seek/decode.
        _decodeGate.Wait();
        try
        {
        _endOfStream = false;
        _mediaEndedRaised = 0;

        position = ClampToDecodeableTime(position);

        Position = position;

        // Try satisfy from cache first (scrubbing back/forth).
        var cacheKey = ToFrameCacheKey(position);
        if (TryGetCachedFrame(cacheKey, out var cached))
        {
            _ptsPrev = position;
            _ptsCurr = position;
            Marshal.Copy(cached, 0, _frameCurr, cached.Length);
            Buffer.MemoryCopy((void*)_frameCurr, (void*)_framePrev, (long)(_stride * _height), (long)(_stride * _height));
            RenderAt(position);
            return;
        }

        // Seek to nearest keyframe and decode forward to requested time (Aegisub/FFMS2-like).
        _ptsPrev = TimeSpan.Zero;
        _ptsCurr = TimeSpan.Zero;
        if (_decoder.SeekAndDecodeTo(position, out var pts))
        {
            _ptsCurr = pts;
            Buffer.MemoryCopy((void*)_decoder.GetBgraBufferPointer(), (void*)_frameCurr, (long)(_stride * _height), (long)(_stride * _height));
            _ptsPrev = _ptsCurr;
            Buffer.MemoryCopy((void*)_frameCurr, (void*)_framePrev, (long)(_stride * _height), (long)(_stride * _height));

            TryCacheCurrentFrame(cacheKey);
        }
        else
        {
            _decoder.Seek(position);
        }

        RenderAt(position);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private TimeSpan ClampToDecodeableTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && t > Duration) t = Duration;

        // Many decoders cannot reliably seek/decode exactly at Duration.
        // Clamp to the last full frame to avoid end-of-stream edge cases.
        if (Duration > TimeSpan.Zero)
        {
            var fps = FrameRate;
            if (fps > 0 && !double.IsNaN(fps) && !double.IsInfinity(fps))
            {
                var frame = TimeSpan.FromSeconds(1.0 / fps);
                var last = Duration - frame;
                if (last > TimeSpan.Zero && t >= Duration)
                    t = last;
                if (t > last && last > TimeSpan.Zero)
                    t = last;
            }
        }

        return t;
    }

    private long ToFrameCacheKey(TimeSpan t)
    {
        // Frame-index based cache key for better reuse across varying FPS.
        var fps = FrameRate;
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)) fps = 30;
        return (long)Math.Round(t.TotalSeconds * fps);
    }

    private bool TryGetCachedFrame(long key, out byte[] data)
    {
        lock (_frameCacheLock)
        {
            if (_frameCache.TryGetValue(key, out data!))
            {
                _frameCacheLru.Remove(key);
                _frameCacheLru.AddLast(key);
                return true;
            }
        }

        data = Array.Empty<byte>();
        return false;
    }

    private void TryCacheCurrentFrame(long key)
    {
        try
        {
            var bytes = new byte[_stride * _height];
            Marshal.Copy(_frameCurr, bytes, 0, bytes.Length);

            lock (_frameCacheLock)
            {
                if (_frameCache.ContainsKey(key))
                {
                    _frameCacheLru.Remove(key);
                    _frameCacheLru.AddLast(key);
                    return;
                }

                _frameCache[key] = bytes;
                _frameCacheLru.AddLast(key);
                while (_frameCacheLru.Count > FrameCacheMax)
                {
                    var oldest = _frameCacheLru.First!.Value;
                    _frameCacheLru.RemoveFirst();
                    _frameCache.Remove(oldest);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Audio-master rendering entry point.
    /// Select the video frame with the closest PTS <= masterTime and render subtitles at masterTime.
    /// Drops frames if needed to keep up.
    /// </summary>
    public void RenderAt(TimeSpan masterTime)
    {
        if (!_useGpu && _writeableBitmap == null) return;
        if (_frameCurr == IntPtr.Zero || _framePrev == IntPtr.Zero || _compositedBuffer == IntPtr.Zero) return;
        if (_isDisposed) return;

        masterTime = ClampToDecodeableTime(masterTime);

        // Decode slightly ahead to keep playback smooth.
        // Render still uses masterTime (audio clock), decode runs ahead and frames are dropped as needed.
        RequestDecodeUpTo(masterTime + PlaybackDecodeAhead);

        Position = masterTime;
        _uiDispatcher?.BeginInvoke(() =>
        {
            try { PositionChanged?.Invoke(this, Position); } catch { }
        });

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
        var buffer = _compositedBuffer;
        var width = _width;
        var height = _height;
        var stride = _stride;
        var gen = _renderGeneration;
        var useGpu = _useGpu;
        var gpu = _d3dImageRenderer;
        var wb = _writeableBitmap;

        QueueUiUpload(buffer, width, height, stride, gen, useGpu);
    }

    private void QueueUiUpload(IntPtr buffer, int width, int height, int stride, int gen, bool useGpu)
    {
        if (_uiDispatcher == null) return;

        // Keep only latest upload parameters.
        _uiUploadBuffer = buffer;
        _uiUploadWidth = width;
        _uiUploadHeight = height;
        _uiUploadStride = stride;
        _uiUploadGen = gen;
        _uiUploadUseGpu = useGpu;

        if (Interlocked.Exchange(ref _uiUploadQueued, 1) != 0)
            return;

        _uiDispatcher.BeginInvoke(() =>
        {
            try
            {
                Interlocked.Exchange(ref _uiUploadQueued, 0);

                if (_isDisposed) return;
                if (_uiUploadGen != _renderGeneration) return;
                if (_uiUploadBuffer == IntPtr.Zero) return;
                if (_uiUploadWidth <= 0 || _uiUploadHeight <= 0 || _uiUploadStride <= 0) return;

                var gpu = _d3dImageRenderer;
                if (_uiUploadUseGpu && gpu != null)
                {
                    gpu.UpdateFromBgra32Buffer(_uiUploadBuffer, _uiUploadStride);
                    return;
                }

                var wb = _writeableBitmap;
                if (wb == null) return;

                wb.Lock();
                wb.WritePixels(new Int32Rect(0, 0, _uiUploadWidth, _uiUploadHeight), _uiUploadBuffer,
                    _uiUploadStride * _uiUploadHeight, _uiUploadStride);
                wb.Unlock();
            }
            catch
            {
            }
        });
    }

    private void EnsureDecodedUpTo(TimeSpan masterTime)
    {
        if (_isDisposed) return;
        if (_endOfStream) return;

        bool raiseEnded = false;

        lock (_renderLock)
        {
            // Decode forward until current PTS >= masterTime (then we can choose prev or curr).
            // If decode runs ahead, we will pick prev.
            while (_ptsCurr < masterTime)
            {
                if (!DecodeNextIntoCurrent(out var pts))
                {
                    _endOfStream = true;
                    _ptsPrev = Duration;
                    _ptsCurr = Duration;

                    raiseEnded = true;
                    break;
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

        // Never raise events while holding locks (can deadlock UI thread).
        if (raiseEnded)
        {
            if (Interlocked.Exchange(ref _mediaEndedRaised, 1) == 0)
                MediaEnded?.Invoke(this, EventArgs.Empty);
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
            // Create CPU fallback buffer.
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

        StopPlaybackDecodeLoop();

        _assRenderer?.Dispose();
        try { _d3dImageRenderer?.Dispose(); } catch { }
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
