using System.Diagnostics;
using System.IO;
using AiSubtitlePro.Core.Models;
using Whisper.net;

namespace AiSubtitlePro.Infrastructure.AI;

/// <summary>
/// Whisper model size options
/// </summary>
public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium,
    Large
}

/// <summary>
/// Transcription result for a segment
/// </summary>
public class TranscriptionSegment
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public string Language { get; set; } = string.Empty;
}

/// <summary>
/// Progress information for transcription
/// </summary>
public class TranscriptionProgress
{
    public int ProgressPercent { get; set; }
    public string Status { get; set; } = string.Empty;
    public TimeSpan ProcessedDuration { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public TranscriptionSegment? CurrentSegment { get; set; }
}

/// <summary>
/// Offline speech recognition using Whisper.net (whisper.cpp binding)
/// </summary>
public class WhisperEngine : IDisposable
{
    private readonly string _modelDirectory;
    private bool _isLoaded;
    private WhisperModelSize _currentModel;
    private CancellationTokenSource? _cts;

    public event EventHandler<TranscriptionProgress>? ProgressChanged;

    /// <summary>
    /// Directory where Whisper models are stored
    /// </summary>
    public static string DefaultModelDirectory => 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "AiSubtitlePro", "Models", "Whisper");

    public WhisperEngine(string? modelDirectory = null)
    {
        _modelDirectory = modelDirectory ?? DefaultModelDirectory;
        Directory.CreateDirectory(_modelDirectory);
    }

    /// <summary>
    /// Checks if a specific model is available locally
    /// </summary>
    public bool IsModelAvailable(WhisperModelSize model)
    {
        var modelPath = GetModelPath(model);
        return File.Exists(modelPath);
    }

    /// <summary>
    /// Gets the file path for a specific model
    /// </summary>
    public string GetModelPath(WhisperModelSize model)
    {
        var fileName = model switch
        {
            WhisperModelSize.Tiny => "ggml-tiny.bin",
            WhisperModelSize.Base => "ggml-base.bin",
            WhisperModelSize.Small => "ggml-small.bin",
            WhisperModelSize.Medium => "ggml-medium.bin",
            WhisperModelSize.Large => "ggml-large-v3.bin",
            _ => "ggml-base.bin"
        };
        return Path.Combine(_modelDirectory, fileName);
    }

    /// <summary>
    /// Gets available models
    /// </summary>
    public IEnumerable<WhisperModelSize> GetAvailableModels()
    {
        foreach (WhisperModelSize model in Enum.GetValues<WhisperModelSize>())
        {
            if (IsModelAvailable(model))
                yield return model;
        }
    }

    /// <summary>
    /// Loads a Whisper model
    /// </summary>
    public async Task LoadModelAsync(WhisperModelSize model, CancellationToken cancellationToken = default)
    {
        if (!IsModelAvailable(model))
            throw new FileNotFoundException($"Whisper model not found: {model}. Please download the model first.");

        // TODO: Integrate actual Whisper.net model loading
        // This is where we would call:
        // var processor = WhisperFactory.FromPath(GetModelPath(model));
        
        await Task.Delay(100, cancellationToken); // Placeholder
        _currentModel = model;
        _isLoaded = true;
    }

    /// <summary>
    /// Transcribes audio/video file to subtitle document
    /// </summary>
    /// <param name="mediaFilePath">Path to audio or video file</param>
    /// <param name="language">Language code (e.g., "en", "ja", "vi") or "auto" for detection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Subtitle document with transcribed lines</returns>
    public async Task<SubtitleDocument> TranscribeAsync(
        string mediaFilePath,
        string language = "auto",
        CancellationToken cancellationToken = default)
    {
        if (!_isLoaded || _currentModel == default)
            throw new InvalidOperationException("No model loaded. Call LoadModelAsync first.");

        if (!File.Exists(mediaFilePath))
            throw new FileNotFoundException("Media file not found.", mediaFilePath);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var document = SubtitleDocument.CreateNew();

        string? tempWavPath = null;

        try
        {
            // 1. Extract/Convert Audio if necessary
            // Whisper needs 16kHz mono PCM WAV
            ReportProgress(0, "Preparing audio...", TimeSpan.Zero, TimeSpan.Zero);
            
            var tempDir = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
            Directory.CreateDirectory(tempDir);
            tempWavPath = Path.Combine(tempDir, Guid.NewGuid() + ".wav");

            // Use FFmpeg to convert
            // We need to instantiate FFmpegService here or pass it in. 
            // For now, we'll create a temporary one since it's lightweight.
            using (var ffmpeg = new Media.FFmpegService())
            {
                await ffmpeg.ExtractAudioAsync(mediaFilePath, tempWavPath, _cts.Token);
                
                // Get duration for progress tracking
                var duration = await ffmpeg.GetDurationAsync(tempWavPath);
                
                ReportProgress(10, "Loading Whisper model...", TimeSpan.Zero, duration);

                LogLoadedWhisperDll();

                using var factory = WhisperFactory.FromPath(GetModelPath(_currentModel));
                var builder = factory.CreateBuilder()
                    .WithLanguage(language == "auto" ? "auto" : language);

                using var processor = builder.Build();

                using var fileStream = File.OpenRead(tempWavPath);

                ReportProgress(15, "Transcribing...", TimeSpan.Zero, duration);

                await foreach (var segment in processor.ProcessAsync(fileStream, _cts.Token))
                {
                    var text = segment.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var subLine = new SubtitleLine
                    {
                        Start = segment.Start,
                        End = segment.End,
                        Text = text
                    };
                    document.AddLine(subLine);

                    ReportProgress(
                        15 + (int)(segment.End.TotalSeconds / duration.TotalSeconds * 85),
                        "Transcribing...",
                        segment.End,
                        duration,
                        new TranscriptionSegment
                        {
                            Start = segment.Start,
                            End = segment.End,
                            Text = text,
                            Confidence = segment.Probability,
                            Language = language
                        });
                }
            }

            ReportProgress(100, "Transcription complete", TimeSpan.Zero, TimeSpan.Zero);
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (Exception)
        {
            LogLoadedWhisperDll();
            throw;
        }
        finally
        {
            _cts = null;
            if (tempWavPath != null && File.Exists(tempWavPath))
            {
                try { File.Delete(tempWavPath); } catch { }
            }
        }

        return document;
    }

    public async Task<SubtitleDocument> TranscribeAsync(
        string mediaFilePath,
        TimeSpan startAbs,
        TimeSpan duration,
        string language = "auto",
        CancellationToken cancellationToken = default)
    {
        if (!_isLoaded || _currentModel == default)
            throw new InvalidOperationException("No model loaded. Call LoadModelAsync first.");

        if (!File.Exists(mediaFilePath))
            throw new FileNotFoundException("Media file not found.", mediaFilePath);

        if (startAbs < TimeSpan.Zero) startAbs = TimeSpan.Zero;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var document = SubtitleDocument.CreateNew();

        string? tempWavPath = null;

        try
        {
            ReportProgress(0, "Preparing audio...", TimeSpan.Zero, TimeSpan.Zero);

            var tempDir = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
            Directory.CreateDirectory(tempDir);
            tempWavPath = Path.Combine(tempDir, Guid.NewGuid() + ".wav");

            using (var ffmpeg = new Media.FFmpegService())
            {
                await ffmpeg.ExtractAudioAsync(mediaFilePath, tempWavPath, startAbs, duration, _cts.Token);

                var total = duration;
                if (total <= TimeSpan.Zero)
                    total = await ffmpeg.GetDurationAsync(tempWavPath);

                ReportProgress(10, "Loading Whisper model...", TimeSpan.Zero, total);

                LogLoadedWhisperDll();
                using var factory = WhisperFactory.FromPath(GetModelPath(_currentModel));
                var builder = factory.CreateBuilder()
                    .WithLanguage(language == "auto" ? "auto" : language);

                using var processor = builder.Build();
                using var fileStream = File.OpenRead(tempWavPath);

                ReportProgress(15, "Transcribing...", TimeSpan.Zero, total);

                await foreach (var segment in processor.ProcessAsync(fileStream, _cts.Token))
                {
                    var text = segment.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    // Segment is already relative to extracted WAV => 0-based within cut.
                    var subLine = new SubtitleLine
                    {
                        Start = segment.Start,
                        End = segment.End,
                        Text = text
                    };
                    document.AddLine(subLine);

                    var processed = segment.End;
                    var progress = (total.TotalSeconds > 0)
                        ? 15 + (int)(processed.TotalSeconds / total.TotalSeconds * 85)
                        : 15;

                    ReportProgress(
                        progress,
                        "Transcribing...",
                        processed,
                        total,
                        new TranscriptionSegment
                        {
                            Start = segment.Start,
                            End = segment.End,
                            Text = text,
                            Confidence = segment.Probability,
                            Language = language
                        });
                }
            }

            ReportProgress(100, "Transcription complete", TimeSpan.Zero, TimeSpan.Zero);
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (Exception)
        {
            LogLoadedWhisperDll();
            throw;
        }
        finally
        {
            _cts = null;
            if (tempWavPath != null && File.Exists(tempWavPath))
            {
                try { File.Delete(tempWavPath); } catch { }
            }
        }

        return document;
    }

    /// <summary>
    /// Cancels ongoing transcription
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    private void ReportProgress(int percent, string status, TimeSpan processed, TimeSpan total, TranscriptionSegment? segment = null)
    {
        ProgressChanged?.Invoke(this, new TranscriptionProgress
        {
            ProgressPercent = percent,
            Status = status,
            ProcessedDuration = processed,
            TotalDuration = total,
            CurrentSegment = segment
        });
    }

    private static void LogLoadedWhisperDll()
    {
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(m.ModuleName, "whisper.dll", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"Loaded whisper.dll from: {m.FileName}");
                    break;
                }
            }
        }
        catch
        {
        }
    }

    public static string DetectRuntimeUsed()
    {
        try
        {
            string? whisperPath = null;
            var hasCudaDeps = false;

            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                var name = m.ModuleName ?? string.Empty;
                if (string.Equals(name, "whisper.dll", StringComparison.OrdinalIgnoreCase))
                    whisperPath = m.FileName;

                if (name.StartsWith("cublas", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("cudart", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("cuda", StringComparison.OrdinalIgnoreCase))
                    hasCudaDeps = true;
            }

            if (!string.IsNullOrWhiteSpace(whisperPath))
            {
                var p = whisperPath.Replace('/', '\\');
                if (p.Contains("\\cuda\\", StringComparison.OrdinalIgnoreCase) || hasCudaDeps)
                    return "GPU(CUDA)";
                if (p.Contains("\\vulkan\\", StringComparison.OrdinalIgnoreCase))
                    return "GPU(Vulkan)";
            }

            if (hasCudaDeps)
                return "GPU(CUDA)";
        }
        catch
        {
        }

        return "CPU";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        // TODO: Dispose Whisper processor
    }
}
