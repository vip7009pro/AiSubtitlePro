using System;
using System.Diagnostics;
using System.IO;

namespace AiSubtitlePro.Infrastructure.Media;

/// <summary>
/// Progress information for media operations
/// </summary>
public class MediaProgress
{
    public int ProgressPercent { get; set; }
    public string Status { get; set; } = string.Empty;
    public TimeSpan ProcessedDuration { get; set; }
    public TimeSpan TotalDuration { get; set; }
}

/// <summary>
/// Service for FFmpeg operations: audio extraction, hard-sub rendering, etc.
/// </summary>
public class FFmpegService : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private Process? _currentProcess;
    private CancellationTokenSource? _cts;

    public event EventHandler<MediaProgress>? ProgressChanged;

    /// <summary>
    /// Default FFmpeg path - looks in PATH or app directory
    /// </summary>
    public static string DefaultFFmpegPath
    {
        get
        {
            // Check if ffmpeg is in PATH
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
            foreach (var dir in pathDirs)
            {
                var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(ffmpeg))
                    return ffmpeg;
            }

            // Check app directory
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var appFfmpeg = Path.Combine(appDir, "ffmpeg.exe");
            if (File.Exists(appFfmpeg))
                return appFfmpeg;

            return "ffmpeg"; // Assume in PATH
        }
    }

    private static string? FindExeOnPath(string exeName)
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
            }
        }
        return null;
    }

    public FFmpegService(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? DefaultFFmpegPath;
        var ffprobeOnPath = FindExeOnPath("ffprobe.exe");
        if (!string.IsNullOrWhiteSpace(ffprobeOnPath))
        {
            _ffprobePath = ffprobeOnPath;
        }
        else
        {
            _ffprobePath = Path.Combine(Path.GetDirectoryName(_ffmpegPath) ?? "", "ffprobe.exe");
        }
    }

    /// <summary>
    /// Checks if FFmpeg is available
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets duration of media file
    /// </summary>
    public async Task<TimeSpan> GetDurationAsync(string mediaPath)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (double.TryParse(output.Trim(), out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch
        {
            // Ignore
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Extracts audio from video file for Whisper transcription
    /// </summary>
    /// <param name="videoPath">Input video path</param>
    /// <param name="outputPath">Output WAV path (16kHz mono for Whisper)</param>
    public async Task ExtractAudioAsync(
        string videoPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Extract audio to 16kHz mono WAV (optimal for Whisper)
        var arguments = $"-hide_banner -nostdin -loglevel error -threads 0 -i \"{videoPath}\" -vn -sn -dn -acodec pcm_s16le -ar 16000 -ac 1 -y \"{outputPath}\"";

        await RunFFmpegAsync(arguments, videoPath, _cts.Token);
    }

    public async Task ExportMp3Async(
        string videoPath,
        string outputPath,
        TimeSpan? start = null,
        TimeSpan? duration = null,
        int bitrateKbps = 192,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        string ToFfmpegTime(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";

        var ss = start.HasValue && start.Value > TimeSpan.Zero
            ? $"-ss {ToFfmpegTime(start.Value)} "
            : string.Empty;

        var tt = duration.HasValue && duration.Value > TimeSpan.Zero
            ? $"-t {ToFfmpegTime(duration.Value)} "
            : string.Empty;

        // MP3 export: audio only, VBR off, good compatibility.
        var arguments = $"-hide_banner -nostdin -loglevel error {ss}{tt}-i \"{videoPath}\" -vn -sn -dn -acodec libmp3lame -b:a {bitrateKbps}k -y \"{outputPath}\"";
        await RunFFmpegAsync(arguments, videoPath, _cts.Token);
    }

    public async Task ExtractAudioAsync(
        string videoPath,
        string outputPath,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        string ToFfmpegTime(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        var startStr = ToFfmpegTime(start);
        var durStr = ToFfmpegTime(duration);

        // Extract & convert: 16kHz, mono, PCM s16le (Whisper-friendly)
        var arguments = $"-hide_banner -nostdin -loglevel error -threads 0 -ss {startStr} -t {durStr} -i \"{videoPath}\" -vn -sn -dn -acodec pcm_s16le -ar 16000 -ac 1 -y \"{outputPath}\"";
        await RunFFmpegAsync(arguments, videoPath, _cts.Token);
    }

    /// <summary>
    /// Renders hard-subtitles onto video
    /// </summary>
    /// <param name="videoPath">Input video path</param>
    /// <param name="subtitlePath">ASS subtitle file path</param>
    /// <param name="outputPath">Output video path</param>
    public async Task RenderHardSubAsync(
        string videoPath,
        string subtitlePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        await RenderHardSubAsync(videoPath, subtitlePath, outputPath, preferGpuEncoding: true, cancellationToken);
    }

    public async Task RenderHardSubAsync(
        string videoPath,
        string subtitlePath,
        string outputPath,
        TimeSpan start,
        TimeSpan duration,
        bool preferGpuEncoding,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Escape special characters in path for ffmpeg filter
        var escapedSubPath = subtitlePath
            .Replace("\\", "/")
            .Replace(":", "\\:");

        string ToFfmpegTime(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";

        var startStr = ToFfmpegTime(start);
        var durStr = ToFfmpegTime(duration);

        var videoEncoders = preferGpuEncoding
            ? new[] { "h264_nvenc", "h264_qsv", "h264_amf", "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx264" }
            : new[] { "libx264" };

        Exception? lastError = null;
        foreach (var vcodec in videoEncoders)
        {
            try
            {
                var arguments = $"-ss {startStr} -t {durStr} -i \"{videoPath}\" -vf \"ass='{escapedSubPath}'\" -c:v {vcodec} -c:a copy -y \"{outputPath}\"";
                await RunFFmpegAsync(arguments, videoPath, _cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new Exception("FFmpeg hard-sub export failed");
    }

    public async Task RenderHardSubVerticalBlurAsync(
        string videoPath,
        string subtitlePath,
        string outputPath,
        int width,
        int height,
        int blurSigma,
        bool preferGpuEncoding,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var escapedSubPath = subtitlePath
            .Replace("\\", "/")
            .Replace(":", "\\:");

        // Build a 9:16 canvas and fill unused area by blurring the same video.
        // Then scale original video to fit (preserve aspect), burn subtitles on foreground,
        // and overlay centered on the blurred background.
        // Use filter_complex + mapping to avoid FFmpeg "Invalid argument" on some builds.
        var fc =
            $"[0:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},gblur=sigma={blurSigma}[bg];" +
            $"[0:v]scale={width}:{height}:force_original_aspect_ratio=decrease,ass='{escapedSubPath}'[fg];" +
            $"[bg][fg]overlay=(W-w)/2:(H-h)/2[v]";

        var videoEncoders = preferGpuEncoding
            ? new[] { "h264_nvenc", "h264_qsv", "h264_amf", "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx264" }
            : new[] { "libx264" };

        Exception? lastError = null;
        foreach (var vcodec in videoEncoders)
        {
            try
            {
                var arguments = $"-i \"{videoPath}\" -filter_complex \"{fc}\" -map \"[v]\" -map 0:a? -c:v {vcodec} -c:a copy -y \"{outputPath}\"";
                await RunFFmpegAsync(arguments, videoPath, _cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new Exception("FFmpeg vertical hard-sub export failed");
    }

    public async Task RenderHardSubVerticalBlurAsync(
        string videoPath,
        string subtitlePath,
        string outputPath,
        TimeSpan start,
        TimeSpan duration,
        int width,
        int height,
        int blurSigma,
        bool preferGpuEncoding,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var escapedSubPath = subtitlePath
            .Replace("\\", "/")
            .Replace(":", "\\:");

        string ToFfmpegTime(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        var startStr = ToFfmpegTime(start);
        var durStr = ToFfmpegTime(duration);

        var fc =
            $"[0:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},gblur=sigma={blurSigma}[bg];" +
            $"[0:v]scale={width}:{height}:force_original_aspect_ratio=decrease,ass='{escapedSubPath}'[fg];" +
            $"[bg][fg]overlay=(W-w)/2:(H-h)/2[v]";

        var videoEncoders = preferGpuEncoding
            ? new[] { "h264_nvenc", "h264_qsv", "h264_amf", "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx264" }
            : new[] { "libx264" };

        Exception? lastError = null;
        foreach (var vcodec in videoEncoders)
        {
            try
            {
                var arguments = $"-ss {startStr} -t {durStr} -i \"{videoPath}\" -filter_complex \"{fc}\" -map \"[v]\" -map 0:a? -c:v {vcodec} -c:a copy -y \"{outputPath}\"";
                await RunFFmpegAsync(arguments, videoPath, _cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new Exception("FFmpeg vertical hard-sub export failed");
    }

    public async Task GenerateVerticalPreviewJpgAsync(
        string videoPath,
        string subtitlePath,
        string outputJpgPath,
        TimeSpan time,
        int blurSigma,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var escapedSubPath = subtitlePath
            .Replace("\\", "/")
            .Replace(":", "\\:");

        string ToFfmpegTime(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        var timeStr = ToFfmpegTime(time);

        var fc =
            $"[0:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},gblur=sigma={blurSigma}[bg];" +
            $"[0:v]scale={width}:{height}:force_original_aspect_ratio=decrease,ass='{escapedSubPath}'[fg];" +
            $"[bg][fg]overlay=(W-w)/2:(H-h)/2[v]";

        var arguments = $"-ss {timeStr} -i \"{videoPath}\" -filter_complex \"{fc}\" -map \"[v]\" -vframes 1 -q:v 2 -y \"{outputJpgPath}\"";
        await RunFFmpegAsync(arguments, videoPath, _cts.Token);
    }

    public async Task RenderHardSubAsync(
        string videoPath,
        string subtitlePath,
        string outputPath,
        bool preferGpuEncoding,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Escape special characters in path for ffmpeg filter
        var escapedSubPath = subtitlePath
            .Replace("\\", "/")
            .Replace(":", "\\:");

        // Note: libass filter runs on CPU, but encoding can be GPU-accelerated.
        // We try common hardware encoders (NVIDIA/Intel/AMD) then fall back to CPU.
        var videoEncoders = preferGpuEncoding
            ? new[] { "h264_nvenc", "h264_qsv", "h264_amf", "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx264" }
            : new[] { "libx264" };

        Exception? lastError = null;
        foreach (var vcodec in videoEncoders)
        {
            try
            {
                var arguments = $"-i \"{videoPath}\" -vf \"ass='{escapedSubPath}'\" -c:v {vcodec} -c:a copy -y \"{outputPath}\"";
                await RunFFmpegAsync(arguments, videoPath, _cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // Try next encoder
            }
        }

        throw lastError ?? new Exception("FFmpeg hard-sub export failed");
    }

    /// <summary>
    /// Creates a video with embedded soft subtitles
    /// </summary>
    public async Task EmbedSoftSubAsync(
        string videoPath,
        string subtitlePath,
        string outputPath,
        string language = "vi",
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var arguments = $"-i \"{videoPath}\" -i \"{subtitlePath}\" " +
                       $"-c:v copy -c:a copy -c:s ass " +
                       $"-metadata:s:s:0 language={language} " +
                       $"-y \"{outputPath}\"";

        await RunFFmpegAsync(arguments, videoPath, _cts.Token);
    }

    /// <summary>
    /// Generates thumbnail at specific time
    /// </summary>
    public async Task GenerateThumbnailAsync(
        string videoPath,
        string outputPath,
        TimeSpan time,
        int width = 320,
        CancellationToken cancellationToken = default)
    {
        var timeStr = $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        var arguments = $"-ss {timeStr} -i \"{videoPath}\" -vframes 1 -vf scale={width}:-1 -y \"{outputPath}\"";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(cancellationToken);
    }

    public async Task CutVideoStreamCopyAsync(
        string videoPath,
        string outputPath,
        TimeSpan start,
        TimeSpan? end = null,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (start < TimeSpan.Zero) start = TimeSpan.Zero;

        string ToFfmpegTime(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        var startStr = ToFfmpegTime(start);

        string arguments;
        if (end.HasValue && end.Value > TimeSpan.Zero && end.Value > start)
        {
            var dur = end.Value - start;
            var durStr = ToFfmpegTime(dur);
            arguments = $"-hide_banner -nostdin -loglevel error -ss {startStr} -t {durStr} -i \"{videoPath}\" -map 0 -c copy -y \"{outputPath}\"";
        }
        else
        {
            arguments = $"-hide_banner -nostdin -loglevel error -ss {startStr} -i \"{videoPath}\" -map 0 -c copy -y \"{outputPath}\"";
        }

        await RunFFmpegAsync(arguments, videoPath, _cts.Token);
    }

    private async Task RunFFmpegAsync(string arguments, string inputPath, CancellationToken cancellationToken)
    {
        var totalDuration = await GetDurationAsync(inputPath);

        // Use FFmpeg's structured progress protocol for reliable progress updates.
        // This avoids depending on stderr human-readable lines (which vary by build/settings).
        var effectiveArguments = $"-progress pipe:2 -nostats {arguments}";

        _currentProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = effectiveArguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _currentProcess.Start();

        // Parse progress from stderr (FFmpeg -progress protocol reports key=value lines)
        var progressTask = Task.Run(async () =>
        {
            using var reader = _currentProcess.StandardError;
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Preferred: out_time_ms= (microseconds)
                if (line.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = line.Substring("out_time_ms=".Length).Trim();
                    if (long.TryParse(raw, out var us))
                    {
                        var processed = TimeSpan.FromMilliseconds(us / 1000.0);
                        var percent = totalDuration.TotalSeconds > 0
                            ? (int)(processed.TotalSeconds * 100 / totalDuration.TotalSeconds)
                            : 0;
                        percent = Math.Clamp(percent, 0, 100);
                        var status = totalDuration.TotalSeconds > 0
                            ? "Processing..."
                            : $"Processing... {processed:hh\\:mm\\:ss}";
                        ReportProgress(percent, status, processed, totalDuration);
                    }
                    continue;
                }

                // Fallback: out_time=HH:MM:SS.micro
                if (line.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = line.Substring("out_time=".Length).Trim();
                    if (TimeSpan.TryParse(raw, out var processed))
                    {
                        var percent = totalDuration.TotalSeconds > 0
                            ? (int)(processed.TotalSeconds * 100 / totalDuration.TotalSeconds)
                            : 0;
                        percent = Math.Clamp(percent, 0, 100);
                        var status = totalDuration.TotalSeconds > 0
                            ? "Processing..."
                            : $"Processing... {processed:hh\\:mm\\:ss}";
                        ReportProgress(percent, status, processed, totalDuration);
                    }
                    continue;
                }

                // progress=end means FFmpeg finished (part of -progress protocol)
                if (line.Equals("progress=end", StringComparison.OrdinalIgnoreCase))
                {
                    ReportProgress(100, "Complete", totalDuration, totalDuration);
                    continue;
                }

                // Legacy fallback for builds where -progress isn't honored as expected
                if (line.Contains("time=", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
                    if (match.Success)
                    {
                        var processed = new TimeSpan(0,
                            int.Parse(match.Groups[1].Value),
                            int.Parse(match.Groups[2].Value),
                            int.Parse(match.Groups[3].Value),
                            int.Parse(match.Groups[4].Value) * 10);

                        var percent = totalDuration.TotalSeconds > 0
                            ? (int)(processed.TotalSeconds * 100 / totalDuration.TotalSeconds)
                            : 0;

                        percent = Math.Clamp(percent, 0, 100);
                        ReportProgress(percent, "Processing...", processed, totalDuration);
                    }
                }
            }
        }, cancellationToken);

        try
        {
            await _currentProcess.WaitForExitAsync(cancellationToken);
            await progressTask;

            if (_currentProcess.ExitCode != 0)
            {
                throw new Exception($"FFmpeg exited with code {_currentProcess.ExitCode}");
            }

            ReportProgress(100, "Complete", totalDuration, totalDuration);
        }
        finally
        {
            _currentProcess = null;
        }
    }

    /// <summary>
    /// Cancels current operation
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
        try
        {
            _currentProcess?.Kill();
        }
        catch { }
    }

    private void ReportProgress(int percent, string status, TimeSpan processed, TimeSpan total)
    {
        ProgressChanged?.Invoke(this, new MediaProgress
        {
            ProgressPercent = percent,
            Status = status,
            ProcessedDuration = processed,
            TotalDuration = total
        });
    }

    public void Dispose()
    {
        Cancel();
        _cts?.Dispose();
    }
}
