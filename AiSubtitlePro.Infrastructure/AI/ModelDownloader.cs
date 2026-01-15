using System.IO;
using System.Net.Http;

namespace AiSubtitlePro.Infrastructure.AI;

/// <summary>
/// Downloads Whisper models from Hugging Face
/// </summary>
public class ModelDownloader : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;
    private bool _disposed;

    // Hugging Face model URLs (ggml format for Whisper.net)
    private static readonly Dictionary<WhisperModelSize, (string Url, long Size)> ModelUrls = new()
    {
        { WhisperModelSize.Tiny, ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin", 75_000_000) },
        { WhisperModelSize.Base, ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", 142_000_000) },
        { WhisperModelSize.Small, ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin", 466_000_000) },
        { WhisperModelSize.Medium, ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin", 1_500_000_000) },
        { WhisperModelSize.Large, ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin", 3_000_000_000) }
    };

    public event EventHandler<DownloadProgress>? ProgressChanged;

    public ModelDownloader(string? modelsDirectory = null)
    {
        _modelsDirectory = modelsDirectory ?? WhisperEngine.DefaultModelDirectory;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromHours(1); // Large models can take a while
        
        Directory.CreateDirectory(_modelsDirectory);
    }

    /// <summary>
    /// Checks if a model is already downloaded
    /// </summary>
    public bool IsModelDownloaded(WhisperModelSize modelSize)
    {
        var modelPath = GetModelPath(modelSize);
        return File.Exists(modelPath);
    }

    /// <summary>
    /// Gets the local path for a model
    /// </summary>
    public string GetModelPath(WhisperModelSize modelSize)
    {
        var fileName = modelSize switch
        {
            WhisperModelSize.Tiny => "ggml-tiny.bin",
            WhisperModelSize.Base => "ggml-base.bin",
            WhisperModelSize.Small => "ggml-small.bin",
            WhisperModelSize.Medium => "ggml-medium.bin",
            WhisperModelSize.Large => "ggml-large-v3.bin",
            _ => "ggml-base.bin"
        };
        return Path.Combine(_modelsDirectory, fileName);
    }

    /// <summary>
    /// Gets the expected download size for a model
    /// </summary>
    public static long GetModelSize(WhisperModelSize modelSize)
    {
        return ModelUrls.TryGetValue(modelSize, out var info) ? info.Size : 0;
    }

    /// <summary>
    /// Downloads a Whisper model
    /// </summary>
    public async Task DownloadModelAsync(WhisperModelSize modelSize, CancellationToken cancellationToken = default)
    {
        if (!ModelUrls.TryGetValue(modelSize, out var modelInfo))
        {
            throw new ArgumentException($"Unknown model size: {modelSize}");
        }

        var modelPath = GetModelPath(modelSize);
        var tempPath = modelPath + ".downloading";

        try
        {
            ReportProgress(0, $"Connecting to download server...", 0, modelInfo.Size);

            using var response = await _httpClient.GetAsync(modelInfo.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? modelInfo.Size;
            var bytesDownloaded = 0L;

            ReportProgress(0, $"Downloading {modelSize} model ({FormatBytes(totalBytes)})...", 0, totalBytes);

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            
            // Use block to ensure file stream is disposed before File.Move
            {
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[81920]; // 80KB buffer
                int bytesRead;
                var lastReportTime = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    bytesDownloaded += bytesRead;

                    // Report progress every 500ms to avoid UI flooding
                    if ((DateTime.UtcNow - lastReportTime).TotalMilliseconds > 500)
                    {
                        var percent = (int)((double)bytesDownloaded / totalBytes * 100);
                        ReportProgress(percent, $"Downloading... {FormatBytes(bytesDownloaded)} / {FormatBytes(totalBytes)}", bytesDownloaded, totalBytes);
                        lastReportTime = DateTime.UtcNow;
                    }
                }
            }

            ReportProgress(100, "Download complete, verifying...", totalBytes, totalBytes);

            // Move temp file to final location
            if (File.Exists(modelPath))
            {
                File.Delete(modelPath);
            }
            File.Move(tempPath, modelPath);

            ReportProgress(100, $"{modelSize} model downloaded successfully!", totalBytes, totalBytes);
        }
        catch (OperationCanceledException)
        {
            // Cleanup temp file on cancel
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }
        catch (Exception ex)
        {
            // Cleanup temp file on error
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw new Exception($"Failed to download model: {ex.Message}", ex);
        }
    }

    private void ReportProgress(int percent, string status, long bytesDownloaded, long totalBytes)
    {
        ProgressChanged?.Invoke(this, new DownloadProgress
        {
            Percent = percent,
            Status = status,
            BytesDownloaded = bytesDownloaded,
            TotalBytes = totalBytes
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient?.Dispose();
    }
}

public class DownloadProgress
{
    public int Percent { get; init; }
    public string Status { get; init; } = string.Empty;
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
}
