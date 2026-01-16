using AiSubtitlePro.Core.Models;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiSubtitlePro.Infrastructure.AI;

/// <summary>
/// Translation mode affecting output style
/// </summary>
public enum TranslationMode
{
    /// <summary>Literal, word-for-word translation</summary>
    Literal,
    /// <summary>Natural, fluent translation</summary>
    Natural,
    /// <summary>Colloquial, conversational style for movies/anime</summary>
    Colloquial
}

/// <summary>
/// Options for subtitle translation
/// </summary>
public class TranslationOptions
{
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "vi";
    public TranslationMode Mode { get; set; } = TranslationMode.Natural;
    public bool CreateBilingual { get; set; }
    public bool PreserveAssTags { get; set; } = true;
    public int BatchSize { get; set; } = 10; // Number of lines to translate together for context
}

/// <summary>
/// Progress information for translation
/// </summary>
public class TranslationProgress
{
    public int CompletedLines { get; set; }
    public int TotalLines { get; set; }
    public int ProgressPercent => TotalLines > 0 ? (CompletedLines * 100 / TotalLines) : 0;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Translation service using LibreTranslate API
/// </summary>
public class TranslationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private CancellationTokenSource? _cts;

    public event EventHandler<TranslationProgress>? ProgressChanged;

    /// <summary>
    /// Common language codes
    /// </summary>
    public static readonly Dictionary<string, string> LanguageCodes = new()
    {
        ["auto"] = "Auto-detect",
        ["en"] = "English",
        ["ja"] = "Japanese",
        ["ko"] = "Korean",
        ["zh"] = "Chinese",
        ["vi"] = "Vietnamese",
        ["th"] = "Thai",
        ["id"] = "Indonesian",
        ["fr"] = "French",
        ["de"] = "German",
        ["es"] = "Spanish",
        ["pt"] = "Portuguese",
        ["ru"] = "Russian",
        ["ar"] = "Arabic"
    };

    public TranslationService(string apiUrl = "https://libretranslate.com")
    {
        _apiUrl = apiUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    /// <summary>
    /// Tests API connectivity
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_apiUrl}/languages");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Translates a subtitle document with context-aware batch processing
    /// </summary>
    public async Task<SubtitleDocument> TranslateDocumentAsync(
        SubtitleDocument source,
        TranslationOptions options,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var result = source.Clone();

        try
        {
            var lines = result.Lines.ToList();
            var totalLines = lines.Count;
            var completedLines = 0;

            ReportProgress(0, totalLines, "Preparing translation...");

            // Process in batches for context-aware translation
            for (int i = 0; i < lines.Count; i += options.BatchSize)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var batch = lines.Skip(i).Take(options.BatchSize).ToList();
                
                // Build context-aware text block
                var textsToTranslate = batch.Select(l => ExtractTranslatableText(l.Text, options.PreserveAssTags)).ToList();
                
                ReportProgress(completedLines, totalLines, $"Translating lines {i + 1}-{Math.Min(i + options.BatchSize, totalLines)}...");

                // Translate the batch
                var translatedTexts = await TranslateBatchAsync(
                    textsToTranslate,
                    options.SourceLanguage,
                    options.TargetLanguage,
                    _cts.Token);

                // Apply translations
                for (int j = 0; j < batch.Count && j < translatedTexts.Count; j++)
                {
                    var originalLine = batch[j];
                    var translatedText = translatedTexts[j];

                    if (options.PreserveAssTags)
                    {
                        translatedText = RestoreAssTags(originalLine.Text, translatedText);
                    }

                    if (options.CreateBilingual)
                    {
                        // Keep original text and add translation
                        originalLine.Text = $"{originalLine.Text}\\N{translatedText}";
                    }
                    else
                    {
                        originalLine.Text = translatedText;
                    }

                    completedLines++;
                }

                ReportProgress(completedLines, totalLines, "Translating...");

                // Small delay to respect API rate limits
                await Task.Delay(100, _cts.Token);
            }

            ReportProgress(totalLines, totalLines, "Translation complete");
        }
        finally
        {
            _cts = null;
        }

        result.IsDirty = true;
        return result;
    }

    /// <summary>
    /// Translates a batch of texts
    /// </summary>
    private async Task<List<string>> TranslateBatchAsync(
        List<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                results.Add(text);
                continue;
            }

            try
            {
                var request = new
                {
                    q = text,
                    source = sourceLanguage == "auto" ? "auto" : sourceLanguage,
                    target = targetLanguage,
                    format = "text"
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(request),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(
                    $"{_apiUrl}/translate",
                    content,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LibreTranslate error {(int)response.StatusCode}: {body}");
                }

                var result = await response.Content.ReadFromJsonAsync<TranslateResponse>(cancellationToken: cancellationToken);
                results.Add(result?.TranslatedText ?? text);
            }
            catch (Exception ex)
            {
                // Fail fast so the UI can show the real reason (rate limit / auth / network / API error)
                throw new Exception($"Translation failed: {ex.Message}", ex);
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts text to translate (removes ASS tags if configured)
    /// </summary>
    private static string ExtractTranslatableText(string text, bool preserveTags)
    {
        if (!preserveTags)
            return text;

        // Remove ASS tags for translation, they'll be restored later
        return System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]*\}", "");
    }

    public static string ExtractTranslatableTextPublic(string text, bool preserveTags)
    {
        return ExtractTranslatableText(text, preserveTags);
    }

    /// <summary>
    /// Restores ASS tags from original text to translated text
    /// </summary>
    private static string RestoreAssTags(string original, string translated)
    {
        // Find leading tags
        var leadingTags = System.Text.RegularExpressions.Regex.Match(original, @"^(\{[^}]*\})+");
        if (leadingTags.Success)
        {
            translated = leadingTags.Value + translated;
        }

        return translated;
    }

    public static string RestoreAssTagsPublic(string original, string translated)
    {
        return RestoreAssTags(original, translated);
    }

    private void ReportProgress(int completed, int total, string status)
    {
        ProgressChanged?.Invoke(this, new TranslationProgress
        {
            CompletedLines = completed,
            TotalLines = total,
            Status = status
        });
    }

    /// <summary>
    /// Cancels ongoing translation
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient.Dispose();
    }

    private class TranslateResponse
    {
        [JsonPropertyName("translatedText")]
        public string? TranslatedText { get; set; }
    }
}
