using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whetstone.Gym;

namespace Whetstone.Server;

/// <summary>The gym client's settings (<c>WHETSTONE_GYM_*</c>): where chargehand listens, its key, the preset, and the folders that hold checkouts.</summary>
public sealed record GymSettings(Uri Endpoint, string Key, string Preset, double RunBudgetUsd, IReadOnlyList<string> RepositoryRoots, TimeSpan Wait)
{
    public const string Usage =
        "WHETSTONE_GYM_URL (chargehand's address), WHETSTONE_GYM_KEY and WHETSTONE_GYM_REPOS (folders holding the checkouts, separated by ':') are "
        + "needed; WHETSTONE_GYM_PRESET (default cheap), WHETSTONE_GYM_RUN_USD (the budget of one run, default 0.15) and WHETSTONE_GYM_WAIT_MINUTES (default 15) are optional";

    /// <summary>The settings, null when none is set (no gym client) or, with <paramref name="problem"/>, when they are incomplete or do not read.</summary>
    public static GymSettings? From(Func<string, string?> setting, out string? problem)
    {
        problem = null;
        var url = setting("WHETSTONE_GYM_URL");
        var key = setting("WHETSTONE_GYM_KEY");
        var repos = setting("WHETSTONE_GYM_REPOS");
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(repos))
            return null;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(repos))
        {
            problem = Usage;
            return null;
        }
        var usd = 0.15;
        var minutes = 15.0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
            || (setting("WHETSTONE_GYM_RUN_USD") is { Length: > 0 } u && !(double.TryParse(u, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out usd) && usd > 0))
            || (setting("WHETSTONE_GYM_WAIT_MINUTES") is { Length: > 0 } m && !(double.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minutes) && minutes > 0)))
        {
            problem = "WHETSTONE_GYM_URL is an http(s) address and the budget and wait are numbers above 0; " + Usage;
            return null;
        }
        var preset = setting("WHETSTONE_GYM_PRESET") is { Length: > 0 } p ? p : "cheap";
        return new GymSettings(endpoint, key.Trim(), preset, usd, [.. repos.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], TimeSpan.FromMinutes(minutes));
    }
}

/// <summary>
/// The gym client for chargehand (ADR 0006): runs a prompt read-only at a commit through chargehand's HTTP API and reports how it went.
/// Every request carries <c>context.enhance: false</c>, so chargehand neither asks whetstone to enhance the replay nor reports it as live
/// feedback. The key goes in a header and is never written anywhere; the answer is returned for the judge and not kept.
/// </summary>
public sealed class ChargehandRunner(GymSettings settings, HttpClient? client = null, TimeSpan? poll = null) : IReplayRunner
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromSeconds(5);

    public string Client => "chargehand";

    public async Task<ReplayResult> RunAsync(ReplayRequest request, CancellationToken ct)
    {
        var path = CheckoutOf(request.Repository);
        if (path is null)
            return new ReplayResult(null, "no_checkout");
        var body = new JsonObject
        {
            ["contract_version"] = "request/v1",
            ["text"] = request.Prompt,
            ["context"] = new JsonObject
            {
                ["interactive"] = false,
                ["preset"] = settings.Preset,
                ["budget_usd"] = settings.RunBudgetUsd,
                ["enhance"] = false,
                ["repository"] = new JsonObject { ["path"] = path, ["commit"] = request.Commit },
            },
        };
        var clock = Stopwatch.StartNew();
        using var post = Message(HttpMethod.Post, "v1/runs", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        post.Headers.TryAddWithoutValidation("Prefer", "wait=30");
        var (status, text) = await SendAsync(post, ct);
        string? runId = null;
        while (true)
        {
            if (status == HttpStatusCode.OK)
                return Read(text, runId, clock.ElapsedMilliseconds);
            if (status == HttpStatusCode.Accepted && JsonNode.Parse(text) is JsonObject accepted)
                runId ??= accepted["run_id"]?.GetValue<string>();
            else
                return new ReplayResult(false, ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), runId, settings.Preset, DurationMs: clock.ElapsedMilliseconds);
            if (runId is null || clock.Elapsed > settings.Wait)
                return new ReplayResult(false, "timeout", runId, settings.Preset, DurationMs: clock.ElapsedMilliseconds);
            await Task.Delay(_poll, ct);
            using var get = Message(HttpMethod.Get, $"v1/runs/{Uri.EscapeDataString(runId)}", null);
            (status, text) = await SendAsync(get, ct);
        }
    }

    /// <summary>The checkout of <c>owner/name</c> (or a bare folder name) under one of the configured folders, or null.</summary>
    private string? CheckoutOf(string repository)
    {
        var name = repository[(repository.LastIndexOf('/') + 1)..];
        if (name.Length == 0 || name is "." or ".." || name.Contains('\\'))
            return null;
        return settings.RepositoryRoots.Select(root => Path.Combine(root, name)).FirstOrDefault(p => Directory.Exists(Path.Combine(p, ".git")) || File.Exists(Path.Combine(p, ".git")));
    }

    private ReplayResult Read(string text, string? runId, long elapsedMs)
    {
        var result = JsonNode.Parse(text) as JsonObject;
        var status = result?["status"]?.GetValue<string>() ?? "unreadable";
        var usage = result?["usage"] as JsonObject;
        var answer = new StringBuilder(result?["summary"]?.GetValue<string>());
        if (result?["claims"] is JsonArray claims)
            foreach (var claim in claims)
                if (claim?["text"]?.GetValue<string>() is { Length: > 0 } t)
                    answer.Append('\n').Append(t);
        return new ReplayResult(
            status == "completed", status, result?["task_id"]?.GetValue<string>() ?? runId, settings.Preset,
            TokensIn: usage?["input"]?.GetValue<long>(), TokensOut: usage?["output"]?.GetValue<long>(),
            CacheReadTokens: usage?["cache_read"]?.GetValue<long>(), CacheWriteTokens: usage?["cache_write"]?.GetValue<long>(),
            CostUsd: usage?["usd"]?.GetValue<double>(), DurationMs: elapsedMs, Answer: answer.Length == 0 ? null : answer.ToString());
    }

    private HttpRequestMessage Message(HttpMethod method, string route, HttpContent? content)
    {
        var message = new HttpRequestMessage(method, new Uri(settings.Endpoint.AbsoluteUri.TrimEnd('/') + "/" + route)) { Content = content };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Key);
        return message;
    }

    private async Task<(HttpStatusCode, string)> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            using var response = await _client.SendAsync(message, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("chargehand did not answer in time");
        }
    }
}
