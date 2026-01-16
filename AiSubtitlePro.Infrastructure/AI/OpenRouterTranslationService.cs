using AiSubtitlePro.Core.Models;
using System.Text.Json;

namespace AiSubtitlePro.Infrastructure.AI;

public sealed class OpenRouterTranslationService : IDisposable
{
    private readonly OpenRouterClient _client;
    private CancellationTokenSource? _cts;

    public event EventHandler<TranslationProgress>? ProgressChanged;
    public event EventHandler<PromptDebugInfo>? PromptPrepared;
    public event EventHandler<RawResponseDebugInfo>? RawResponseReceived;

    public sealed class PromptDebugInfo
    {
        public string Model { get; set; } = string.Empty;
        public string SystemPrompt { get; set; } = string.Empty;
        public string UserPrompt { get; set; } = string.Empty;
        public int BatchStartLine { get; set; }
        public int BatchEndLine { get; set; }
    }

    public sealed class RawResponseDebugInfo
    {
        public string Model { get; set; } = string.Empty;
        public int BatchStartLine { get; set; }
        public int BatchEndLine { get; set; }
        public string RawContent { get; set; } = string.Empty;
    }

    public OpenRouterTranslationService(OpenRouterClient? client = null)
    {
        _client = client ?? new OpenRouterClient();
    }

    private sealed class TranslateSchema
    {
        public string schema { get; set; } = "AiSubtitlePro.Translation.v1";
        public List<Item> items { get; set; } = new();

        public sealed class Item
        {
            public int index { get; set; }
            public string text { get; set; } = string.Empty;
        }
    }

    public static int ApplyTranslationJsonToDocument(
        SubtitleDocument document,
        TranslationOptions options,
        string json)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (options == null) throw new ArgumentNullException(nameof(options));

        var extracted = ExtractJson(json);
        if (string.IsNullOrWhiteSpace(extracted))
            throw new Exception("Pasted output is empty (no JSON).");

        TranslateSchema? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TranslateSchema>(extracted);
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to parse pasted JSON.", ex);
        }

        if (parsed == null || !string.Equals(parsed.schema, "AiSubtitlePro.Translation.v1", StringComparison.Ordinal))
            throw new Exception("Invalid translation schema in pasted JSON.");

        var map = parsed.items.ToDictionary(x => x.index, x => x.text);
        var applied = 0;

        foreach (var line in document.Lines)
        {
            if (!map.TryGetValue(line.Index, out var translatedText))
                continue;

            if (options.PreserveAssTags)
                translatedText = TranslationService.RestoreAssTagsPublic(line.Text, translatedText);

            if (options.CreateBilingual)
                line.Text = $"{line.Text}\\N{translatedText}";
            else
                line.Text = translatedText;

            applied++;
        }

        document.IsDirty = true;
        return applied;
    }

    public async Task<SubtitleDocument> TranslateDocumentAsync(
        SubtitleDocument source,
        TranslationOptions options,
        string apiKey,
        string model,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var result = source.Clone();

        var lines = result.Lines.ToList();
        var totalLines = lines.Count;
        var completedLines = 0;
        var failedBatches = 0;

        ReportProgress(0, totalLines, "Preparing translation...");

        var batches = new Queue<(int StartIndex, List<SubtitleLine> Lines)>();
        for (int i = 0; i < lines.Count; i += options.BatchSize)
            batches.Enqueue((i, lines.Skip(i).Take(options.BatchSize).ToList()));

        var attempts = new Dictionary<int, int>();
        var maxAttemptsPerBatch = 6;

        while (batches.Count > 0)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var (startIndex, batch) = batches.Dequeue();
            var batchStart = startIndex + 1;
            var batchEnd = Math.Min(startIndex + options.BatchSize, totalLines);

            attempts.TryGetValue(startIndex, out var tryCount);
            tryCount++;
            attempts[startIndex] = tryCount;

            ReportProgress(completedLines, totalLines, $"Translating lines {batchStart}-{batchEnd}... (remaining batches: {batches.Count + 1})");

            try
            {
                var payloadItems = new List<object>();
                for (var j = 0; j < batch.Count; j++)
                {
                    var line = batch[j];
                    var text = TranslationService.ExtractTranslatableTextPublic(line.Text, options.PreserveAssTags);
                    payloadItems.Add(new { index = line.Index, text });
                }

                var system = BuildSystemPrompt(options);
                var user = BuildUserPrompt(options, payloadItems);

                PromptPrepared?.Invoke(this, new PromptDebugInfo
                {
                    Model = model,
                    SystemPrompt = system,
                    UserPrompt = user,
                    BatchStartLine = batchStart,
                    BatchEndLine = batchEnd
                });

                var content = await _client.ChatCompletionAsync(apiKey, model, system, user, _cts.Token);

                RawResponseReceived?.Invoke(this, new RawResponseDebugInfo
                {
                    Model = model,
                    BatchStartLine = batchStart,
                    BatchEndLine = batchEnd,
                    RawContent = content
                });

                var json = ExtractJson(content);
                if (string.IsNullOrWhiteSpace(json))
                    throw new Exception($"Model returned empty response (no JSON). Model='{model}'.");

                TranslateSchema? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<TranslateSchema>(json);
                }
                catch (Exception ex)
                {
                    var preview = content;
                    if (preview.Length > 800) preview = preview[..800] + "...";
                    throw new Exception($"Failed to parse JSON from model output. Model='{model}'. Output preview: {preview}", ex);
                }

                if (parsed == null || !string.Equals(parsed.schema, "AiSubtitlePro.Translation.v1", StringComparison.Ordinal))
                    throw new Exception("Invalid translation schema response");

                var map = parsed.items.ToDictionary(x => x.index, x => x.text);

                foreach (var line in batch)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    if (!map.TryGetValue(line.Index, out var translatedText))
                        throw new Exception($"Missing translated item for index {line.Index}");

                    if (options.PreserveAssTags)
                        translatedText = TranslationService.RestoreAssTagsPublic(line.Text, translatedText);

                    if (options.CreateBilingual)
                        line.Text = $"{line.Text}\\N{translatedText}";
                    else
                        line.Text = translatedText;

                    completedLines++;
                }

                ReportProgress(completedLines, totalLines, $"Translated lines {batchStart}-{batchEnd}.");
            }
            catch (OpenRouterHttpException ex) when (ex.StatusCode == 429)
            {
                if (tryCount >= maxAttemptsPerBatch)
                {
                    failedBatches++;
                    ReportProgress(completedLines, totalLines, $"Rate limited. Skipping batch {batchStart}-{batchEnd} after {tryCount} attempts. Failed batches: {failedBatches}");
                    continue;
                }

                var delay = ComputeRateLimitDelay(ex.Headers, tryCount);
                ReportProgress(completedLines, totalLines, $"Rate limited (429). Waiting {delay.TotalSeconds:F0}s then retrying batch {batchStart}-{batchEnd}... (attempt {tryCount}/{maxAttemptsPerBatch})");
                await Task.Delay(delay, _cts.Token);
                batches.Enqueue((startIndex, batch));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                if (tryCount >= maxAttemptsPerBatch)
                {
                    failedBatches++;
                    ReportProgress(completedLines, totalLines, $"Error. Skipping batch {batchStart}-{batchEnd} after {tryCount} attempts. Failed batches: {failedBatches}");
                    continue;
                }

                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(tryCount, 6))));
                ReportProgress(completedLines, totalLines, $"Batch {batchStart}-{batchEnd} failed. Will retry later in {delay.TotalSeconds:F0}s... (attempt {tryCount}/{maxAttemptsPerBatch})");
                await Task.Delay(delay, _cts.Token);
                batches.Enqueue((startIndex, batch));
            }
        }

        var finalStatus = failedBatches > 0
            ? $"Translation complete with {failedBatches} failed batch(es)"
            : "Translation complete";
        ReportProgress(totalLines, totalLines, finalStatus);
        result.IsDirty = true;
        return result;
    }

    private static TimeSpan ComputeRateLimitDelay(Dictionary<string, string> headers, int attempt)
    {
        if (headers.TryGetValue("Retry-After", out var retryAfterValue))
        {
            if (int.TryParse(retryAfterValue, out var seconds) && seconds > 0)
                return TimeSpan.FromSeconds(Math.Min(300, seconds));
        }

        if (headers.TryGetValue("X-RateLimit-Reset", out var resetValue))
        {
            if (long.TryParse(resetValue, out var unixSeconds) && unixSeconds > 0)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var delta = Math.Max(1, unixSeconds - now);
                return TimeSpan.FromSeconds(Math.Min(600, delta));
            }
        }

        var backoffSeconds = Math.Min(120, Math.Pow(2, Math.Min(attempt, 6)));
        return TimeSpan.FromSeconds(backoffSeconds);
    }

    private static string BuildSystemPrompt(TranslationOptions options)
    {
        var style = options.Mode switch
        {
            TranslationMode.Literal => "literal, close to the original",
            TranslationMode.Colloquial => "colloquial, conversational",
            _ => "natural, fluent"
        };

        return $"You are a professional subtitle translator. Translate each item's text from '{options.SourceLanguage}' to '{options.TargetLanguage}' in a {style} style. Output MUST be valid JSON ONLY and MUST start with '{{' and end with '}}'. Do not include markdown, backticks, explanations, or any extra text. The JSON must match the schema exactly.";
    }

    private static string BuildUserPrompt(TranslationOptions options, List<object> items)
    {
        var schema = "{\n  \"schema\": \"AiSubtitlePro.Translation.v1\",\n  \"items\": [\n    { \"index\": 1, \"text\": \"...translated...\" }\n  ]\n}";
        var request = new
        {
            schema = "AiSubtitlePro.Translation.v1",
            source_language = options.SourceLanguage,
            target_language = options.TargetLanguage,
            items
        };

        var payload = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true });

        return $"Return JSON ONLY with this exact shape (no markdown, no backticks, no extra text):\n{schema}\n\nTranslate the following:\n{payload}";
    }

    private static string ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        // Models sometimes wrap JSON in ```json ... ```. Strip fences if present.
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];

            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
                trimmed = trimmed[..lastFence];

            trimmed = trimmed.Trim();
        }

        // If there's extra text around JSON, extract the first JSON object.
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed.Substring(start, end - start + 1).Trim();

        return trimmed;
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

    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _client.Dispose();
    }
}
