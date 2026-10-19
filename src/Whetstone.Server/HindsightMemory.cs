using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whetstone.Context;

namespace Whetstone.Server;

/// <summary>
/// <see cref="IMemorySource"/> over a Hindsight server's recall route (<c>POST /v1/default/banks/{bank}/memories/recall</c>). It reads only:
/// nothing is written back (docs/specs/2026-10-19-context-slots-design.md, decision 1). A memory with no source document is keyed by its own
/// id. Answers are cached for a few minutes per query and repository, so one burst of requests makes one call.
/// </summary>
public sealed class HindsightMemory(HttpClient http, Uri baseUrl, string bank, string? key) : IMemorySource
{
    private static readonly TimeSpan CacheLife = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, IReadOnlyList<Memory> Found)> _cache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<Memory>> RecallAsync(string query, string? repository, int max, CancellationToken ct)
    {
        var cacheKey = MemoryRanking.Hash($"{repository}\n{max}\n{query}");
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Until > now)
            return cached.Found;
        var body = new Dictionary<string, object?>
        {
            ["query"] = query.Length > 2000 ? query[..2000] : query,
            ["budget"] = "low",
            ["max_tokens"] = 2048,
            ["types"] = new[] { "world", "observation" },
        };
        if (repository is { Length: > 0 })
        {
            body["tags"] = new[] { $"project:{repository.Split('/')[^1]}" };
            body["tags_match"] = "any_strict";
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, $"v1/default/banks/{Uri.EscapeDataString(bank)}/memories/recall"))
        {
            Content = JsonContent.Create(body),
        };
        if (key is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var found = Parse(json.RootElement, max);
        _cache[cacheKey] = (now + CacheLife, found);
        return found;
    }

    internal static IReadOnlyList<Memory> Parse(JsonElement root, int max)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];
        var found = new List<Memory>();
        foreach (var r in results.EnumerateArray())
        {
            if (r.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } memoryId && r.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } words)
            {
                var document = r.TryGetProperty("document_id", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } doc ? "doc:" + doc : "memory:" + memoryId;
                var score = r.TryGetProperty("scores", out var s) && s.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
                found.Add(new Memory(memoryId, document, words, score));
            }
            if (found.Count == max)
                break;
        }
        return found;
    }
}
