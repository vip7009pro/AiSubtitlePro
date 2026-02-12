using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;

namespace AiSubtitlePro.Infrastructure.Rendering;

public sealed class D3DImageRenderer : IDisposable
{
    private readonly object _sync = new();

    private readonly D3DImage _image;
    private readonly Dispatcher _imageDispatcher;

    private IntPtr _surface9Ptr;

    private ID3D11Device? _d3d11;
    private ID3D11DeviceContext? _d3d11Ctx;
    private ID3D11Texture2D? _sharedTex11;
    private IDXGIKeyedMutex? _keyedMutex;
    private bool _useD3d11Shared;

    public static bool EnableD3D11SharedInterop { get; set; }

    private static string LogPath => Path.Combine(Path.GetTempPath(), "AiSubtitlePro.VideoPreview.log");

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
        Debug.WriteLine(message);
    }

    private IDirect3D9Ex? _d3d9;
    private IDirect3DDevice9Ex? _d3d9Device;
    private IDirect3DTexture9? _sharedTex9;
    private IDirect3DSurface9? _surface9;

    private IDirect3DSurface9? _readbackSysmemSurface;
    private IDirect3DSurface9? _uploadSysmemSurface;
    private int _interopBlackChecks;
    private int _updateCounter;

    private volatile bool _frontBufferAvailable;
    private int _presentQueued;

    private int _width;
    private int _height;
    private int _stride;

    public ImageSource ImageSource => _image;

    public bool IsInitialized => _surface9 != null;

    public D3DImageRenderer()
    {
        _image = new D3DImage();
        _imageDispatcher = _image.Dispatcher;
        _frontBufferAvailable = false;
        _image.IsFrontBufferAvailableChanged += (_, __) =>
        {
            lock (_sync)
            {
                RunOnImageDispatcher(() => { _frontBufferAvailable = _image.IsFrontBufferAvailable; });

                if (_surface9Ptr == IntPtr.Zero)
                    return;

                if (!_frontBufferAvailable)
                    return;

                RunOnImageDispatcher(() =>
                {
                    try
                    {
                        _image.Lock();
                        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9Ptr);
                        _image.Unlock();
                    }
                    catch
                    {
                    }
                });
            }
        };
    }

    private void RunOnImageDispatcher(Action action)
    {
        try
        {
            if (_imageDispatcher.CheckAccess())
            {
                action();
                return;
            }

            _imageDispatcher.Invoke(action);
        }
        catch
        {
        }
    }

    private void QueuePresent()
    {
        // Avoid blocking decode/render threads on UI. Coalesce: only queue one pending present.
        if (Interlocked.Exchange(ref _presentQueued, 1) != 0)
            return;

        try
        {
            _imageDispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!_frontBufferAvailable) return;
                    if (_width <= 0 || _height <= 0) return;

                    _image.Lock();
                    _image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                    _image.Unlock();
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _presentQueued, 0);
                }
            }, DispatcherPriority.Render);
        }
        catch
        {
            Interlocked.Exchange(ref _presentQueued, 0);
        }
    }

    public void Initialize(int width, int height)
    {
        lock (_sync)
        {
            DisposeInternal();

            _width = width;
            _height = height;
            _stride = width * 4;

            // D3D9Ex device + shared surface (required by WPF D3DImage)
            _d3d9 = D3D9.Direct3DCreate9Ex();
            var pp = new Vortice.Direct3D9.PresentParameters
            {
                Windowed = true,
                SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
                DeviceWindowHandle = GetDesktopWindow(),
                PresentationInterval = Vortice.Direct3D9.PresentInterval.Immediate
            };

            _d3d9Device = _d3d9.CreateDeviceEx(
                0,
                DeviceType.Hardware,
                GetDesktopWindow(),
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                pp);

            _useD3d11Shared = false;
            _sharedTex11 = null;
            _keyedMutex = null;
            _interopBlackChecks = 0;
            _updateCounter = 0;

            if (EnableD3D11SharedInterop)
            {
                try
                {
                    Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                        null,
                        DriverType.Hardware,
                        DeviceCreationFlags.BgraSupport,
                        null,
                        out _d3d11,
                        out _d3d11Ctx);

                    var desc = new Texture2DDescription
                    {
                        Width = width,
                        Height = height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.Shared
                    };

                    _sharedTex11 = _d3d11.CreateTexture2D(desc);

                    _keyedMutex = _sharedTex11.QueryInterfaceOrNull<IDXGIKeyedMutex>();

                    using var dxgiRes = _sharedTex11.QueryInterface<IDXGIResource>();
                    var sharedHandle = dxgiRes.SharedHandle;

                    _sharedTex9 = _d3d9Device.CreateTexture(
                        width,
                        height,
                        1,
                        Vortice.Direct3D9.Usage.RenderTarget,
                        Vortice.Direct3D9.Format.A8R8G8B8,
                        Pool.Default,
                        ref sharedHandle);

                    _useD3d11Shared = _sharedTex9 != null;

                    if (_useD3d11Shared)
                    {
                        try
                        {
                            _readbackSysmemSurface = _d3d9Device.CreateOffscreenPlainSurface(
                                Math.Min(width, 64),
                                Math.Min(height, 64),
                                Vortice.Direct3D9.Format.A8R8G8B8,
                                Pool.SystemMemory);
                        }
                        catch
                        {
                            _readbackSysmemSurface = null;
                        }
                    }

                    Log($"D3DImageRenderer: D3D11 shared interop enabled={_useD3d11Shared} handle=0x{sharedHandle.ToInt64():X}");
                }
                catch (Exception ex)
                {
                    Log($"D3DImageRenderer: D3D11 shared interop init failed; falling back to D3D9 LockRect. {ex}");
                    try { _sharedTex11?.Dispose(); } catch { }
                    _sharedTex11 = null;
                    try { _keyedMutex?.Dispose(); } catch { }
                    _keyedMutex = null;
                    try { _d3d11Ctx?.Dispose(); } catch { }
                    _d3d11Ctx = null;
                    try { _d3d11?.Dispose(); } catch { }
                    _d3d11 = null;
                    try { _sharedTex9?.Dispose(); } catch { }
                    _sharedTex9 = null;
                    _useD3d11Shared = false;
                }
            }

            if (_sharedTex9 == null)
            {
                // For D3DImage, the backbuffer must be a render-target surface.
                // We upload CPU pixels into a lockable sysmem surface, then UpdateSurface into this render-target.
                _sharedTex9 = _d3d9Device.CreateTexture(
                    width,
                    height,
                    1,
                    Vortice.Direct3D9.Usage.RenderTarget,
                    Vortice.Direct3D9.Format.A8R8G8B8,
                    Pool.Default);

                _useD3d11Shared = false;
                try { _readbackSysmemSurface?.Dispose(); } catch { }
                _readbackSysmemSurface = null;

                try { _uploadSysmemSurface?.Dispose(); } catch { }
                _uploadSysmemSurface = null;

                try
                {
                    _uploadSysmemSurface = _d3d9Device.CreateOffscreenPlainSurface(
                        width,
                        height,
                        Vortice.Direct3D9.Format.A8R8G8B8,
                        Pool.SystemMemory);
                }
                catch
                {
                    _uploadSysmemSurface = null;
                }
            }

            _surface9 = _sharedTex9.GetSurfaceLevel(0);

            _surface9Ptr = _surface9.NativePointer;

            RunOnImageDispatcher(() => { _frontBufferAvailable = _image.IsFrontBufferAvailable; });

            RunOnImageDispatcher(() =>
            {
                _image.Lock();
                _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9Ptr);
                _image.Unlock();
            });
        }
    }

    private bool DetectBlackFrame()
    {
        try
        {
            if (!_useD3d11Shared) return false;
            if (_d3d9Device == null || _surface9 == null || _readbackSysmemSurface == null) return false;

            // Copy a small region for diagnostics (fast enough at low frequency).
            _d3d9Device.GetRenderTargetData(_surface9, _readbackSysmemSurface);

            var locked = _readbackSysmemSurface.LockRect(LockFlags.ReadOnly);
            try
            {
                unsafe
                {
                    // Sample a few pixels from the first row.
                    var p = (byte*)locked.DataPointer;
                    var pitch = locked.Pitch;
                    if (p == null || pitch <= 0) return false;

                    int nonZero = 0;
                    int samples = 0;
                    int width = Math.Min(_width, 64);
                    for (int x = 0; x < width; x += 8)
                    {
                        byte b = p[x * 4 + 0];
                        byte g = p[x * 4 + 1];
                        byte r = p[x * 4 + 2];
                        byte a = p[x * 4 + 3];
                        samples++;
                        if (b != 0 || g != 0 || r != 0 || a != 0)
                            nonZero++;
                    }

                    return nonZero == 0 && samples > 0;
                }
            }
            finally
            {
                _readbackSysmemSurface.UnlockRect();
            }
        }
        catch
        {
            return false;
        }
    }

    private void FallbackToD3d9LockRect()
    {
        try
        {
            if (_d3d9Device == null) return;

            try { _surface9?.Dispose(); } catch { }
            _surface9 = null;

            try { _sharedTex9?.Dispose(); } catch { }
            _sharedTex9 = null;

            try { _readbackSysmemSurface?.Dispose(); } catch { }
            _readbackSysmemSurface = null;

            try { _sharedTex11?.Dispose(); } catch { }
            _sharedTex11 = null;

            try { _keyedMutex?.Dispose(); } catch { }
            _keyedMutex = null;

            try { _d3d11Ctx?.Dispose(); } catch { }
            _d3d11Ctx = null;

            try { _d3d11?.Dispose(); } catch { }
            _d3d11 = null;

            _useD3d11Shared = false;

            _sharedTex9 = _d3d9Device.CreateTexture(
                _width,
                _height,
                1,
                Vortice.Direct3D9.Usage.Dynamic,
                Vortice.Direct3D9.Format.A8R8G8B8,
                Pool.Default);

            _surface9 = _sharedTex9.GetSurfaceLevel(0);
            _surface9Ptr = _surface9.NativePointer;

            if (_image.IsFrontBufferAvailable)
            {
                RunOnImageDispatcher(() =>
                {
                    _image.Lock();
                    _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9Ptr);
                    _image.Unlock();
                });
            }
        }
        catch (Exception ex)
        {
            Log($"D3DImageRenderer: fallback failed. {ex}");
        }
    }

    public void UpdateFromBgra32Buffer(IntPtr srcBgra32, int strideBytes)
    {
        lock (_sync)
        {
            // Do not read D3DImage properties from a background thread.
            if (!_frontBufferAvailable)
                return;

            try
            {
                if (_d3d9Device != null)
                {
                    _d3d9Device.TestCooperativeLevel();
                }
            }
            catch
            {
                return;
            }

            if (_sharedTex9 == null || _surface9 == null)
                return;

            if (_width <= 0 || _height <= 0)
                return;

            if (strideBytes <= 0)
                strideBytes = _stride;

            if (_useD3d11Shared)
            {
                if (_sharedTex11 == null || _d3d11Ctx == null)
                    return;

                try
                {
                    _keyedMutex?.AcquireSync(0, 5);
                }
                catch
                {
                }

                unsafe
                {
                    var byteCount = strideBytes * _height;
                    var span = new ReadOnlySpan<byte>((void*)srcBgra32, byteCount);
                    try { _d3d11Ctx.UpdateSubresource(span, _sharedTex11, 0, strideBytes); } catch { return; }
                }

                try { _d3d11Ctx.Flush(); } catch { }

                try
                {
                    _keyedMutex?.ReleaseSync(0);
                }
                catch
                {
                }

                _updateCounter++;
                // Readback-based detection is expensive and can throw on some drivers.
                // Only probe briefly after init to decide whether to auto-fallback.
                if (_updateCounter <= 120 && (_updateCounter % 60) == 0)
                {
                    if (DetectBlackFrame())
                    {
                        _interopBlackChecks++;
                        if (_interopBlackChecks >= 3)
                        {
                            Log("D3DImageRenderer: detected black output on D3D11 interop; auto-fallback to D3D9 LockRect.");
                            FallbackToD3d9LockRect();
                        }
                    }
                    else
                    {
                        _interopBlackChecks = 0;
                    }
                }
            }
            else
            {
                if (_surface9 == null || _uploadSysmemSurface == null || _d3d9Device == null)
                    return;

                // Stage upload into lockable sysmem surface, then blit to render-target surface.
                try
                {
                    var locked = _uploadSysmemSurface.LockRect(LockFlags.None);
                    try
                    {
                        unsafe
                        {
                            byte* srcRow = (byte*)srcBgra32;
                            byte* dstRow = (byte*)locked.DataPointer;

                            var copyBytes = Math.Min(strideBytes, locked.Pitch);
                            for (int y = 0; y < _height; y++)
                            {
                                Buffer.MemoryCopy(srcRow, dstRow, locked.Pitch, copyBytes);
                                srcRow += strideBytes;
                                dstRow += locked.Pitch;
                            }
                        }
                    }
                    finally
                    {
                        try { _uploadSysmemSurface.UnlockRect(); } catch { }
                    }
                }
                catch
                {
                    return;
                }

                try
                {
                    _d3d9Device.UpdateSurface(
                        _uploadSysmemSurface,
                        new Vortice.Direct3D9.Rect(0, 0, _width, _height),
                        _surface9,
                        new Vortice.Mathematics.Int2(0, 0));
                }
                catch
                {
                    return;
                }
            }

            // Notify WPF that the backbuffer changed (async + coalesced).
            QueuePresent();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            DisposeInternal();
        }
    }

    private void DisposeInternal()
    {
        try
        {
            RunOnImageDispatcher(() =>
            {
                _image.Lock();
                _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                _image.Unlock();
            });
        }
        catch
        {
        }

        _surface9Ptr = IntPtr.Zero;

        try { _readbackSysmemSurface?.Dispose(); } catch { }
        _readbackSysmemSurface = null;

        try { _uploadSysmemSurface?.Dispose(); } catch { }
        _uploadSysmemSurface = null;

        try { _surface9?.Dispose(); } catch { }
        _surface9 = null;

        try { _sharedTex9?.Dispose(); } catch { }
        _sharedTex9 = null;

        try { _d3d9Device?.Dispose(); } catch { }
        _d3d9Device = null;

        try { _d3d9?.Dispose(); } catch { }
        _d3d9 = null;

        try { _sharedTex11?.Dispose(); } catch { }
        _sharedTex11 = null;

        try { _keyedMutex?.Dispose(); } catch { }
        _keyedMutex = null;

        try { _d3d11Ctx?.Dispose(); } catch { }
        _d3d11Ctx = null;

        try { _d3d11?.Dispose(); } catch { }
        _d3d11 = null;

        _width = 0;
        _height = 0;
        _stride = 0;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
