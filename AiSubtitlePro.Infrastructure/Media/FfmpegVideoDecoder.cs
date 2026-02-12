using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FFmpeg.AutoGen;

namespace AiSubtitlePro.Infrastructure.Media;

public unsafe sealed class FfmpegVideoDecoder : IDisposable
{
    private readonly object _sync = new();

    private AVFormatContext* _formatCtx;
    private AVCodecContext* _codecCtx;
    private AVStream* _videoStream;
    private SwsContext* _sws;

    private int _videoStreamIndex = -1;

    private AVPacket* _packet;
    private AVFrame* _decodedFrame;
    private AVFrame* _bgraFrame;

    private byte* _bgraBuffer;
    private int _bgraBufferSize;

    private bool _isOpen;

    private long[] _keyframeIndexTs = Array.Empty<long>();
    private string? _sourcePath;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }

    public TimeSpan Duration { get; private set; }

    public AVRational TimeBase { get; private set; }

    public double FrameRate { get; private set; }

    private static bool _nativeLoaded;

    public FfmpegVideoDecoder(string? ffmpegBinariesPath = null)
    {
        // FFmpeg.AutoGen uses dynamically loaded bindings by default.
        // We must point it to the directory that contains avcodec/avformat/avutil/swscale dlls.
        var root = ffmpegBinariesPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Native", "win-x64");
        }

        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            // Help both the Windows loader and FFmpeg.AutoGen's dynamic loader.
            SetDllDirectory(root);
            ffmpeg.RootPath = root;

            EnsureNativeLibrariesLoaded(root);
        }

        try
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        }
        catch (NotSupportedException)
        {
            // If bindings failed to load, this call throws. We'll surface the real error later when opening media.
        }

        try
        {
            ffmpeg.avformat_network_init();
        }
        catch (NotSupportedException)
        {
            // Same as above.
        }
    }

    private static void EnsureNativeLibrariesLoaded(string root)
    {
        if (_nativeLoaded) return;

        // Load order matters a bit due to dependencies.
        // Prefer full-path loads to avoid picking up random DLLs from PATH.
        var dlls = new[]
        {
            "avutil-*.dll",
            "swresample-*.dll",
            "swscale-*.dll",
            "avcodec-*.dll",
            "avformat-*.dll",
        };

        foreach (var pattern in dlls)
        {
            var match = Directory
                .EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (match == null)
                continue;

            try
            {
                NativeLibrary.Load(match);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load native FFmpeg DLL '{Path.GetFileName(match)}' from '{root}'. This usually means missing VC++ runtime or missing dependent DLLs. Original error: {ex.Message}", ex);
            }
        }

        _nativeLoaded = true;
    }

    public void Open(string path)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is empty", nameof(path));

            CloseInternal();

            AVFormatContext* fmt = null;
            ThrowIfError(ffmpeg.avformat_open_input(&fmt, path, null, null));
            _formatCtx = fmt;

            _sourcePath = path;

            ThrowIfError(ffmpeg.avformat_find_stream_info(_formatCtx, null));

            _videoStreamIndex = ffmpeg.av_find_best_stream(_formatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (_videoStreamIndex < 0)
                throw new InvalidOperationException("No video stream found");

            _videoStream = _formatCtx->streams[_videoStreamIndex];
            TimeBase = _videoStream->time_base;

            // Build keyframe timestamps by scanning packets in a separate format context.
            // This is slower than container indexes but works with FFmpeg.AutoGen bindings.
            _keyframeIndexTs = LoadOrBuildKeyframeIndex(path);

            var codecpar = _videoStream->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException($"Decoder not found for codec_id={codecpar->codec_id}");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null)
                throw new OutOfMemoryException("avcodec_alloc_context3 failed");

            ThrowIfError(ffmpeg.avcodec_parameters_to_context(_codecCtx, codecpar));
            ThrowIfError(ffmpeg.avcodec_open2(_codecCtx, codec, null));

            Width = _codecCtx->width;
            Height = _codecCtx->height;
            Stride = Width * 4;

            // Duration
            if (_formatCtx->duration > 0)
            {
                Duration = TimeSpan.FromSeconds(_formatCtx->duration / (double)ffmpeg.AV_TIME_BASE);
            }
            else if (_videoStream->duration > 0 && _videoStream->time_base.den > 0)
            {
                // Some containers don't fill format duration. Fall back to stream duration.
                Duration = TimeSpan.FromSeconds(_videoStream->duration * (_videoStream->time_base.num / (double)_videoStream->time_base.den));
            }
            else
            {
                Duration = TimeSpan.Zero;
            }

            // Frame rate (best effort)
            var fr = _videoStream->avg_frame_rate;
            if (fr.num > 0 && fr.den > 0)
                FrameRate = fr.num / (double)fr.den;
            else
                FrameRate = 0;

            _packet = ffmpeg.av_packet_alloc();
            _decodedFrame = ffmpeg.av_frame_alloc();
            _bgraFrame = ffmpeg.av_frame_alloc();
            if (_packet == null || _decodedFrame == null || _bgraFrame == null)
                throw new OutOfMemoryException("Failed to allocate ffmpeg structs");

            // Prepare BGRA output frame
            _bgraBufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, Width, Height, 1);
            _bgraBuffer = (byte*)ffmpeg.av_malloc((ulong)_bgraBufferSize);
            if (_bgraBuffer == null)
                throw new OutOfMemoryException("av_malloc failed for BGRA buffer");

            // FFmpeg.AutoGen maps av_image_fill_arrays to 4-plane arrays.
            // BGRA is packed (single plane), so we fill plane 0 and copy into the AVFrame arrays.
            byte_ptrArray4 dstData = default;
            int_array4 dstLinesize = default;
            ThrowIfError(ffmpeg.av_image_fill_arrays(ref dstData, ref dstLinesize, _bgraBuffer, AVPixelFormat.AV_PIX_FMT_BGRA, Width, Height, 1));
            _bgraFrame->data[0] = dstData[0];
            _bgraFrame->linesize[0] = dstLinesize[0];

            _sws = ffmpeg.sws_getContext(
                Width,
                Height,
                _codecCtx->pix_fmt,
                Width,
                Height,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR,
                null,
                null,
                null);

            if (_sws == null)
                throw new InvalidOperationException("sws_getContext failed");

            _isOpen = true;
        }
    }

    private static long[] LoadOrBuildKeyframeIndex(string path)
    {
        var cacheKey = TryComputeIndexCacheKey(path);
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            var cachePath = GetIndexCachePath(cacheKey);
            var loaded = TryLoadIndex(cachePath);
            if (loaded != null)
                return loaded;

            var built = BuildKeyframeIndexByPacketScan(path);
            TrySaveIndex(cachePath, built);
            return built;
        }

        return BuildKeyframeIndexByPacketScan(path);
    }

    private static string GetIndexCachePath(string cacheKey)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"keyframes_{cacheKey}.bin");
    }

    private static string? TryComputeIndexCacheKey(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;

            var payload = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            var bytes = Encoding.UTF8.GetBytes(payload);
            var hash = SHA1.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static long[]? TryLoadIndex(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;

            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            var count = br.ReadInt32();
            if (count <= 0 || count > 5_000_000)
                return null;

            var arr = new long[count];
            for (var i = 0; i < count; i++)
                arr[i] = br.ReadInt64();
            return arr;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySaveIndex(string cachePath, long[] index)
    {
        try
        {
            if (index == null || index.Length == 0)
                return;

            using var fs = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);
            bw.Write(index.Length);
            for (var i = 0; i < index.Length; i++)
                bw.Write(index[i]);
        }
        catch
        {
        }
    }

    private static long[] BuildKeyframeIndexByPacketScan(string path)
    {
        try
        {
            AVFormatContext* fmt = null;
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0 || fmt == null)
                return Array.Empty<long>();

            try
            {
                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    return Array.Empty<long>();

                var videoStreamIndex = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (videoStreamIndex < 0)
                    return Array.Empty<long>();

                var packet = ffmpeg.av_packet_alloc();
                if (packet == null)
                    return Array.Empty<long>();

                try
                {
                    var set = new System.Collections.Generic.HashSet<long>();
                    while (true)
                    {
                        var read = ffmpeg.av_read_frame(fmt, packet);
                        if (read < 0)
                            break;

                        try
                        {
                            if (packet->stream_index != videoStreamIndex)
                                continue;

                            // packet flags: AV_PKT_FLAG_KEY indicates keyframe.
                            if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0)
                                continue;

                            long ts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                            if (ts == ffmpeg.AV_NOPTS_VALUE)
                                continue;

                            set.Add(ts);
                        }
                        finally
                        {
                            ffmpeg.av_packet_unref(packet);
                        }
                    }

                    if (set.Count == 0)
                        return Array.Empty<long>();

                    var arr = set.ToArray();
                    Array.Sort(arr);
                    return arr;
                }
                finally
                {
                    var p = packet;
                    ffmpeg.av_packet_free(&p);
                }
            }
            finally
            {
                var f = fmt;
                ffmpeg.avformat_close_input(&f);
            }
        }
        catch
        {
            return Array.Empty<long>();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (!_isOpen)
                return;

            if (position < TimeSpan.Zero) position = TimeSpan.Zero;

            // Use seconds-based conversion to preserve precision (avoid rounding to milliseconds).
            var tb = TimeBase.num / (double)TimeBase.den;
            if (tb <= 0) tb = 1.0 / 1000.0;
            long ts = (long)Math.Round(position.TotalSeconds / tb);

            ThrowIfError(ffmpeg.av_seek_frame(_formatCtx, _videoStreamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD));
            ffmpeg.avcodec_flush_buffers(_codecCtx);
        }
    }

    public bool SeekAndDecodeTo(TimeSpan target, out TimeSpan decodedPts)
    {
        decodedPts = TimeSpan.Zero;
        lock (_sync)
        {
            if (!_isOpen)
                return false;

            if (target < TimeSpan.Zero) target = TimeSpan.Zero;

            var tb = TimeBase.num / (double)TimeBase.den;
            if (tb <= 0) tb = 1.0 / 1000.0;
            var targetTs = (long)Math.Round(target.TotalSeconds / tb);

            // Pick nearest keyframe <= target when index is available.
            long seekTs = targetTs;
            if (_keyframeIndexTs.Length > 0)
            {
                var idx = Array.BinarySearch(_keyframeIndexTs, targetTs);
                if (idx < 0) idx = ~idx - 1;
                if (idx < 0) idx = 0;
                seekTs = _keyframeIndexTs[idx];
            }

            ThrowIfError(ffmpeg.av_seek_frame(_formatCtx, _videoStreamIndex, seekTs, ffmpeg.AVSEEK_FLAG_BACKWARD));
            ffmpeg.avcodec_flush_buffers(_codecCtx);

            // Decode forward until we reach a frame at/after target.
            while (true)
            {
                if (!TryDecodeNextFrame_NoLock(out var pts))
                    return false;

                decodedPts = pts;
                if (pts >= target)
                    return true;
            }
        }
    }

    public bool TryDecodeNextFrame(out TimeSpan pts)
    {
        pts = TimeSpan.Zero;
        lock (_sync)
        {
            if (!_isOpen)
                return false;

            return TryDecodeNextFrame_NoLock(out pts);
        }
    }

    private bool TryDecodeNextFrame_NoLock(out TimeSpan pts)
    {
        pts = TimeSpan.Zero;

        while (true)
        {
            int read = ffmpeg.av_read_frame(_formatCtx, _packet);
            if (read < 0)
            {
                if (read == ffmpeg.AVERROR_EOF)
                    return false;

                throw new InvalidOperationException($"FFmpeg error: av_read_frame failed: {GetErrorString(read)} ({read})");
            }

            try
            {
                if (_packet->stream_index != _videoStreamIndex)
                    continue;

                ThrowIfError(ffmpeg.avcodec_send_packet(_codecCtx, _packet));

                while (true)
                {
                    int recv = ffmpeg.avcodec_receive_frame(_codecCtx, _decodedFrame);
                    if (recv == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recv == ffmpeg.AVERROR_EOF)
                        break;

                    ThrowIfError(recv);

                    // Convert to BGRA
                    ffmpeg.sws_scale(
                        _sws,
                        _decodedFrame->data,
                        _decodedFrame->linesize,
                        0,
                        Height,
                        _bgraFrame->data,
                        _bgraFrame->linesize);

                    long bestTs = _decodedFrame->best_effort_timestamp;
                    if (bestTs != ffmpeg.AV_NOPTS_VALUE)
                    {
                        pts = TimeSpan.FromSeconds(bestTs * (TimeBase.num / (double)TimeBase.den));
                    }

                    return true;
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    public IntPtr GetBgraBufferPointer() => (IntPtr)_bgraBuffer;

    public void Close()
    {
        lock (_sync)
        {
            CloseInternal();
        }
    }

    private void CloseInternal()
    {
        _isOpen = false;
        _keyframeIndexTs = Array.Empty<long>();
        _sourcePath = null;

        if (_packet != null)
        {
            var p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }

        if (_decodedFrame != null)
        {
            var f = _decodedFrame;
            ffmpeg.av_frame_free(&f);
            _decodedFrame = null;
        }

        if (_bgraFrame != null)
        {
            var f = _bgraFrame;
            ffmpeg.av_frame_free(&f);
            _bgraFrame = null;
        }

        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        if (_codecCtx != null)
        {
            var c = _codecCtx;
            ffmpeg.avcodec_free_context(&c);
            _codecCtx = null;
        }

        if (_formatCtx != null)
        {
            var f = _formatCtx;
            ffmpeg.avformat_close_input(&f);
            _formatCtx = null;
        }

        if (_bgraBuffer != null)
        {
            ffmpeg.av_free(_bgraBuffer);
            _bgraBuffer = null;
            _bgraBufferSize = 0;
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private static void ThrowIfError(int err)
    {
        if (err >= 0) return;
        throw new InvalidOperationException($"FFmpeg error: {GetErrorString(err)} ({err})");
    }

    private static string GetErrorString(int error)
    {
        var bufferSize = 1024;
        var buffer = stackalloc byte[bufferSize];
        ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? "Unknown";
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}
