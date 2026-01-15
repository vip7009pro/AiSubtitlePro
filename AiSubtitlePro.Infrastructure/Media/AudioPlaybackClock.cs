using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AiSubtitlePro.Infrastructure.Media;

public sealed class AudioPlaybackClock : IDisposable
{
    private readonly object _sync = new();

    private readonly FfmpegAudioDecoder _decoder;
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private Task? _decodeTask;
    private CancellationTokenSource? _cts;

    private WaveFormat? _format;

    // We treat device position as the master clock.
    private long _deviceStartBytes;
    private TimeSpan _clockStartTime;

    private volatile bool _isPlaying;

    public bool IsPlaying => _isPlaying;

    public TimeSpan Duration { get; private set; }

    public AudioPlaybackClock()
    {
        _decoder = new FfmpegAudioDecoder();
    }

    public void Load(string mediaPath)
    {
        Stop();

        _decoder.Open(mediaPath);
        Duration = _decoder.Duration;

        _format = WaveFormat.CreateCustomFormat(
            WaveFormatEncoding.Pcm,
            _decoder.SampleRate,
            _decoder.Channels,
            _decoder.SampleRate * _decoder.Channels * FfmpegAudioDecoder.BytesPerSample,
            _decoder.Channels * FfmpegAudioDecoder.BytesPerSample,
            16);

        _buffer = new BufferedWaveProvider(_format)
        {
            DiscardOnBufferOverflow = true,
            BufferLength = _format.AverageBytesPerSecond * 2
        };

        // Shared mode is fine for editor preview.
        _out = new WasapiOut(AudioClientShareMode.Shared, false, 50);
        _out.Init(_buffer);

        _deviceStartBytes = 0;
        _clockStartTime = TimeSpan.Zero;
    }

    public void Play()
    {
        lock (_sync)
        {
            if (_out == null || _buffer == null)
                return;

            if (_isPlaying)
                return;

            _isPlaying = true;

            _cts = new CancellationTokenSource();
            _decodeTask = Task.Run(() => DecodeLoop(_cts.Token));

            // Reset base for master clock.
            _deviceStartBytes = _out.GetPosition();

            _out.Play();
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (!_isPlaying)
                return;

            _isPlaying = false;
            try { _out?.Pause(); } catch { }
            _cts?.Cancel();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _isPlaying = false;
            try { _out?.Stop(); } catch { }
            _cts?.Cancel();
            _cts = null;
            _decodeTask = null;

            _clockStartTime = TimeSpan.Zero;
            _deviceStartBytes = 0;

            if (_buffer != null)
                _buffer.ClearBuffer();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && position > Duration) position = Duration;

            var wasPlaying = _isPlaying;

            // Stop device to avoid playing old buffered audio.
            try { _out?.Stop(); } catch { }
            _cts?.Cancel();

            _decoder.Seek(position);

            if (_buffer != null)
                _buffer.ClearBuffer();

            // Rebase master clock.
            _clockStartTime = position;
            _deviceStartBytes = _out?.GetPosition() ?? 0;

            if (wasPlaying)
            {
                _cts = new CancellationTokenSource();
                _decodeTask = Task.Run(() => DecodeLoop(_cts.Token));
                try { _out?.Play(); } catch { }
                _isPlaying = true;
            }
            else
            {
                _isPlaying = false;
            }
        }
    }

    public void SetVolume(float volume01)
    {
        lock (_sync)
        {
            if (_out == null) return;
            // WasapiOut exposes Volume.
            try { _out.Volume = Math.Clamp(volume01, 0f, 1f); } catch { }
        }
    }

    public TimeSpan GetAudioTime()
    {
        var o = _out;
        var f = _format;
        if (o == null || f == null) return _clockStartTime;

        try
        {
            var bytes = o.GetPosition();
            var deltaBytes = Math.Max(0, bytes - _deviceStartBytes);
            var deltaSeconds = deltaBytes / (double)f.AverageBytesPerSecond;
            var t = _clockStartTime + TimeSpan.FromSeconds(deltaSeconds);

            if (Duration > TimeSpan.Zero && t > Duration) t = Duration;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;

            return t;
        }
        catch
        {
            return _clockStartTime;
        }
    }

    private void DecodeLoop(CancellationToken token)
    {
        // Keep buffer primed but don't overfill.
        var temp = new byte[64 * 1024];

        while (!token.IsCancellationRequested)
        {
            BufferedWaveProvider? buf;
            WaveFormat? fmt;
            lock (_sync)
            {
                buf = _buffer;
                fmt = _format;
            }

            if (buf == null || fmt == null)
                return;

            // Keep ~500ms ahead.
            var bufferedSeconds = buf.BufferedDuration.TotalSeconds;
            if (bufferedSeconds > 0.5)
            {
                Thread.Sleep(10);
                continue;
            }

            var written = _decoder.DecodePcm16Interleaved(temp, out _);
            if (written <= 0)
            {
                Thread.Sleep(10);
                continue;
            }

            buf.AddSamples(temp, 0, written);
        }
    }

    public void Dispose()
    {
        Stop();
        try { _out?.Dispose(); } catch { }
        _out = null;
        _buffer = null;
        _decoder.Dispose();
    }
}
