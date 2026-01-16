using AiSubtitlePro.Core.Models;
using System.Text.Json;

namespace AiSubtitlePro.Infrastructure.AI;

public sealed class OpenRouterTranslationService : IDisposable
{
    private readonly OpenRouterClient _client;
    private CancellationTokenSource? _cts;

    public event EventHandler<TranslationProgress>? ProgressChanged;

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

        ReportProgress(0, totalLines, "Preparing translation...");

        for (int i = 0; i < lines.Count; i += options.BatchSize)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var batch = lines.Skip(i).Take(options.BatchSize).ToList();
            var payloadItems = new List<object>();
            for (var j = 0; j < batch.Count; j++)
            {
                var line = batch[j];
                var text = TranslationService.ExtractTranslatableTextPublic(line.Text, options.PreserveAssTags);
                payloadItems.Add(new { index = line.Index, text });
            }

            ReportProgress(completedLines, totalLines, $"Translating lines {i + 1}-{Math.Min(i + options.BatchSize, totalLines)}...");

            var system = BuildSystemPrompt(options);
            var user = BuildUserPrompt(options, payloadItems);

            var content = await _client.ChatCompletionAsync(apiKey, model, system, user, _cts.Token);

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

            ReportProgress(completedLines, totalLines, "Translating...");
        }

        ReportProgress(totalLines, totalLines, "Translation complete");
        result.IsDirty = true;
        return result;
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
