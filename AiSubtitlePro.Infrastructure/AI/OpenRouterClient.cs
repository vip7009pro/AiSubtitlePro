using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Net;

namespace AiSubtitlePro.Infrastructure.AI;

public sealed class OpenRouterHttpException : Exception
{
    public int StatusCode { get; }
    public Dictionary<string, string> Headers { get; }
    public string ResponseBody { get; }

    public OpenRouterHttpException(int statusCode, Dictionary<string, string> headers, string responseBody)
        : base($"OpenRouter error {statusCode}: {responseBody}")
    {
        StatusCode = statusCode;
        Headers = headers;
        ResponseBody = responseBody;
    }
}

public sealed class OpenRouterClient : IDisposable
{
    private readonly HttpClient _http;

    public OpenRouterClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<string> ChatCompletionAsync(
        string apiKey,
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            response_format = new { type = "json_object" },
            temperature = 0.2,
            max_tokens = 4096,
            stream = false
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpReq.Headers.Add("HTTP-Referer", "https://localhost");
        httpReq.Headers.Add("X-Title", "AiSubtitlePro");
        httpReq.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers)
                headers[h.Key] = string.Join(",", h.Value);
            foreach (var h in resp.Content.Headers)
                headers[h.Key] = string.Join(",", h.Value);

            throw new OpenRouterHttpException((int)resp.StatusCode, headers, body);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var content = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? string.Empty;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
