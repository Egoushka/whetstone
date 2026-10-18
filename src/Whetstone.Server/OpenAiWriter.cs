using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Whetstone.Templates;

namespace Whetstone.Server;

/// <summary>
/// The writer's one outbound call: a chat completion at the endpoint the owner configured. The key goes in a header and is never
/// written anywhere; the reply is read for its text and its token counts and nothing else is kept.
/// </summary>
internal sealed class OpenAiWriter(WriterSettings settings, HttpClient? client = null) : IChallengerWriter
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<WriterReply> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = settings.Model,
            max_tokens = maxTokens,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
        });
        var address = new Uri(settings.Endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Key);
        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"the writer's endpoint answered {(int)response.StatusCode}");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var content = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;
        if (content is null)
            throw new InvalidOperationException("the writer's endpoint returned no message");
        long? input = null, output = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pi) ? pi : null;
            output = usage.TryGetProperty("completion_tokens", out var o) && o.TryGetInt64(out var oi) ? oi : null;
        }
        return new WriterReply(content, input, output);
    }
}
