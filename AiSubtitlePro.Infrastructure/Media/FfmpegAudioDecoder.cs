using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace AiSubtitlePro.Infrastructure.Media;

public unsafe sealed class FfmpegAudioDecoder : IDisposable
{
    private readonly object _sync = new();

    private AVFormatContext* _formatCtx;
    private AVCodecContext* _codecCtx;
    private AVStream* _audioStream;
    private SwrContext* _swr;

    private int _audioStreamIndex = -1;

    private AVPacket* _packet;
    private AVFrame* _decodedFrame;

    private byte* _resampleBuffer;
    private int _resampleBufferSize;

    private bool _isOpen;

    public TimeSpan Duration { get; private set; }
    public AVRational TimeBase { get; private set; }

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    // Output format: 16-bit signed interleaved PCM
    public const int BytesPerSample = 2;

    private static bool _nativeLoaded;

    public FfmpegAudioDecoder(string? ffmpegBinariesPath = null)
    {
        var root = ffmpegBinariesPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Native", "win-x64");
        }

        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            SetDllDirectory(root);
            ffmpeg.RootPath = root;
            EnsureNativeLibrariesLoaded(root);
        }

        try { ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR); } catch { }
        try { ffmpeg.avformat_network_init(); } catch { }
    }

    private static void EnsureNativeLibrariesLoaded(string root)
    {
        if (_nativeLoaded) return;

        var dlls = new[]
        {
            "avutil-*.dll",
            "swresample-*.dll",
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

            NativeLibrary.Load(match);
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

            ThrowIfError(ffmpeg.avformat_find_stream_info(_formatCtx, null));

            _audioStreamIndex = ffmpeg.av_find_best_stream(_formatCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (_audioStreamIndex < 0)
                throw new InvalidOperationException("No audio stream found");

            _audioStream = _formatCtx->streams[_audioStreamIndex];
            TimeBase = _audioStream->time_base;

            var codecpar = _audioStream->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException($"Decoder not found for codec_id={codecpar->codec_id}");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null)
                throw new OutOfMemoryException("avcodec_alloc_context3 failed");

            ThrowIfError(ffmpeg.avcodec_parameters_to_context(_codecCtx, codecpar));
            ThrowIfError(ffmpeg.avcodec_open2(_codecCtx, codec, null));

            // Duration
            if (_formatCtx->duration > 0)
                Duration = TimeSpan.FromSeconds(_formatCtx->duration / (double)ffmpeg.AV_TIME_BASE);
            else
                Duration = TimeSpan.Zero;

            SampleRate = _codecCtx->sample_rate;
            Channels = _codecCtx->ch_layout.nb_channels;
            if (Channels <= 0)
                Channels = codecpar->ch_layout.nb_channels;

            if (SampleRate <= 0 || Channels <= 0)
                throw new InvalidOperationException("Invalid audio format");

            _packet = ffmpeg.av_packet_alloc();
            _decodedFrame = ffmpeg.av_frame_alloc();
            if (_packet == null || _decodedFrame == null)
                throw new OutOfMemoryException("Failed to allocate ffmpeg structs");

            // Resampler to: s16 interleaved, same sample rate, same channel count.
            var outChLayout = _codecCtx->ch_layout;
            var outSampleFmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
            var outSampleRate = SampleRate;

            SwrContext* swr = null;
            ThrowIfError(ffmpeg.swr_alloc_set_opts2(
                &swr,
                &outChLayout,
                outSampleFmt,
                outSampleRate,
                &_codecCtx->ch_layout,
                _codecCtx->sample_fmt,
                _codecCtx->sample_rate,
                0,
                null));
            _swr = swr;
            ThrowIfError(ffmpeg.swr_init(_swr));

            _isOpen = true;
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (!_isOpen) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;

            long ts = ffmpeg.av_rescale_q(
                (long)position.TotalMilliseconds,
                new AVRational { num = 1, den = 1000 },
                TimeBase);

            ThrowIfError(ffmpeg.av_seek_frame(_formatCtx, _audioStreamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD));
            ffmpeg.avcodec_flush_buffers(_codecCtx);
        }
    }

    public int DecodePcm16Interleaved(Span<byte> destination, out TimeSpan pts)
    {
        pts = TimeSpan.Zero;

        lock (_sync)
        {
            if (!_isOpen) return 0;

            while (true)
            {
                int read = ffmpeg.av_read_frame(_formatCtx, _packet);
                if (read < 0)
                {
                    if (read == ffmpeg.AVERROR_EOF)
                        return 0;

                    throw new InvalidOperationException($"FFmpeg error: av_read_frame failed: {GetErrorString(read)} ({read})");
                }

                try
                {
                    if (_packet->stream_index != _audioStreamIndex)
                        continue;

                    ThrowIfError(ffmpeg.avcodec_send_packet(_codecCtx, _packet));

                    while (true)
                    {
                        int recv = ffmpeg.avcodec_receive_frame(_codecCtx, _decodedFrame);
                        if (recv == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recv == ffmpeg.AVERROR_EOF)
                            break;

                        ThrowIfError(recv);

                        long bestTs = _decodedFrame->best_effort_timestamp;
                        if (bestTs != ffmpeg.AV_NOPTS_VALUE)
                            pts = TimeSpan.FromSeconds(bestTs * (TimeBase.num / (double)TimeBase.den));

                        // Allocate resample buffer for worst-case samples
                        int outSamples = ffmpeg.swr_get_out_samples(_swr, _decodedFrame->nb_samples);
                        int requiredBytes = outSamples * Channels * BytesPerSample;
                        EnsureResampleBuffer(requiredBytes);

                        // FFmpeg.AutoGen expects byte** for output pointers.
                        var outData = stackalloc byte*[1];
                        outData[0] = _resampleBuffer;

                        int converted = ffmpeg.swr_convert(
                            _swr,
                            outData,
                            outSamples,
                            _decodedFrame->extended_data,
                            _decodedFrame->nb_samples);

                        if (converted < 0)
                            ThrowIfError(converted);

                        int outBytes = converted * Channels * BytesPerSample;
                        if (outBytes <= 0)
                            return 0;

                        if (outBytes > destination.Length)
                            outBytes = destination.Length;

                        new Span<byte>(_resampleBuffer, outBytes).CopyTo(destination);
                        return outBytes;
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }
        }
    }

    private void EnsureResampleBuffer(int requiredBytes)
    {
        if (_resampleBuffer != null && _resampleBufferSize >= requiredBytes)
            return;

        if (_resampleBuffer != null)
        {
            ffmpeg.av_free(_resampleBuffer);
            _resampleBuffer = null;
            _resampleBufferSize = 0;
        }

        _resampleBuffer = (byte*)ffmpeg.av_malloc((ulong)requiredBytes);
        if (_resampleBuffer == null)
            throw new OutOfMemoryException("av_malloc failed for resample buffer");

        _resampleBufferSize = requiredBytes;
    }

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

        if (_swr != null)
        {
            var s = _swr;
            ffmpeg.swr_free(&s);
            _swr = null;
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

        if (_resampleBuffer != null)
        {
            ffmpeg.av_free(_resampleBuffer);
            _resampleBuffer = null;
            _resampleBufferSize = 0;
        }

        _audioStreamIndex = -1;
        _audioStream = null;
        Duration = TimeSpan.Zero;
        SampleRate = 0;
        Channels = 0;
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
