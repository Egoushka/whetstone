using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Redaction;
using static Whetstone.Tests.McpHarness;

namespace Whetstone.Tests.Storage;

/// <summary>Goal 0.2 through the real tools: what enhance and feedback leave on disk, and that no storage fault changes an answer.</summary>
public sealed class StoreServerTests : IAsyncDisposable
{
    private const string Key = "test-key";

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;
    private WebApplication? _app;
    private IHost? _host;

    public StoreServerTests() => _stores = new SqliteStores(_data.Path);

    private async Task<(McpClient Client, Uri Address)> Start(IEnhancer? enhancer, IStore store)
    {
        _app = WhetstoneServer.Create(new ServerSettings(0, Key), enhancer ?? new PassThrough(), store);
        await _app.StartAsync();
        var address = new Uri(_app.Urls.First());
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address, "/v1/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Key}" },
        }));
        return (client, address);
    }

    private static string Json(string prompt, object? context = null, int? deadline = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["prompt"] = prompt, ["context"] = context, ["deadline_ms"] = deadline }
            .Where(p => p.Value is not null).ToDictionary());

    private IStore Owner() => _stores.ForUser("owner");

    [Fact]
    public async Task Enhance_stores_a_redacted_row_and_feedback_fills_its_outcome()
    {
        var (client, _) = await Start(null, Owner());
        var token = Corpus.Secrets.Single(s => s.Name == "github classic token").Text;

        var answer = Enhanced(await Call(client, Tools.Enhance, Json($"review my diff, the header was {token}", new { repository = "example/app", task_kind = "review" })));
        await Call(client, Tools.Feedback, $$$"""{"request_id":"{{{answer.RequestId}}}","outcome":{"rewrite_accepted":false,"score":0.7}}""");

        var row = Assert.Single(Db.Requests(_data.FileFor("owner")));
        Assert.Equal(answer.RequestId, row["request_id"]);
        Assert.Equal("review my diff, the header was [REDACTED:token]", row["prompt"]);
        Assert.Equal("example/app", row["repository"]);
        Assert.Equal("review", row["task_kind"]);
        Assert.Equal("chargehand", row["client"] ?? "chargehand");
        Assert.Equal(0L, row["rewrite_accepted"]);
        Assert.Equal(0.7, row["score"]);
        Assert.DoesNotContain(token, Db.Everything(_data.FileFor("owner")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_answer_still_carries_the_original_prompt_unredacted()
    {
        var (client, _) = await Start(null, Owner());
        var token = Corpus.Secrets.Single(s => s.Name == "npm token").Text;

        var answer = Enhanced(await Call(client, Tools.Enhance, Json($"use {token}")));

        Assert.Equal($"use {token}", answer.Prompt);
    }

    [Fact]
    public async Task A_replay_of_the_corpus_through_enhance_leaves_no_seeded_secret_on_disk()
    {
        var (client, _) = await Start(null, Owner());
        foreach (var seeded in Corpus.Secrets)
            Enhanced(await Call(client, Tools.Enhance, Json($"Fix this error: {seeded.Text}\nthen retry", new { repository = "example/app" })));

        var file = Db.Everything(_data.FileFor("owner"));

        Assert.Equal(Corpus.Secrets.Count, Db.Requests(_data.FileFor("owner")).Count);
        foreach (var seeded in Corpus.Secrets)
            Assert.False(file.Contains(seeded.Value, StringComparison.Ordinal), $"{seeded.Name} is in the file");
    }

    [Fact]
    public async Task A_rewrite_is_not_kept_only_the_prompt_that_was_sent()
    {
        var (client, _) = await Start(Enhancers.Rewriting(), Owner());

        var answer = Enhanced(await Call(client, Tools.Enhance, Json("review my diff")));

        Assert.True(answer.Changed);
        var row = Assert.Single(Db.Requests(_data.FileFor("owner")));
        Assert.Equal("review my diff", row["prompt"]);
        Assert.Equal(1L, row["changed"]);
        Assert.Equal("tpl-test", row["template_id"]);
        Assert.DoesNotContain("step by step", Db.Everything(_data.FileFor("owner")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feedback_for_an_unknown_request_is_accepted_and_stores_nothing()
    {
        var (client, _) = await Start(null, Owner());
        Enhanced(await Call(client, Tools.Enhance, Json("review my diff")));

        var call = await Call(client, Tools.Feedback, """{"request_id":"req-never-seen","outcome":{"rewrite_accepted":true}}""");

        Assert.NotEqual(true, call.IsError);
        Assert.Null(Assert.Single(Db.Requests(_data.FileFor("owner")))["rewrite_accepted"]);
    }

    [Fact]
    public async Task An_invalid_request_stores_nothing()
    {
        var (client, _) = await Start(null, Owner());

        Error(await Call(client, Tools.Enhance, """{"prompt":""}"""));

        Assert.False(File.Exists(_data.FileFor("owner")));
    }

    private sealed class Failing(Exception failure) : IStore
    {
        public long Failures => 0;

        public Task RecordAsync(RequestRow row, CancellationToken ct) => throw failure;

        public Task<bool> RecordOutcomeAsync(OutcomeRow outcome, CancellationToken ct) => throw failure;
    }

    private sealed class Blocking : IStore
    {
        public long Failures => 0;

        public Task RecordAsync(RequestRow row, CancellationToken ct)
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }

        public Task<bool> RecordOutcomeAsync(OutcomeRow outcome, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task A_failing_store_changes_no_answer_and_is_counted_in_health()
    {
        var guarded = new GuardedStore(new Failing(new IOException("disk full")));
        var (client, address) = await Start(Enhancers.Rewriting(), guarded);

        var answer = Enhanced(await Call(client, Tools.Enhance, Json("review my diff")));
        var feedback = await Call(client, Tools.Feedback, """{"request_id":"req-1","outcome":{"score":1}}""");

        Assert.True(answer.Changed);
        Assert.Equal("review my diff, step by step", answer.Prompt);
        Assert.NotEqual(true, feedback.IsError);
        using var http = new HttpClient { BaseAddress = address };
        var health = JsonDocument.Parse(await http.GetStringAsync(new Uri("/health", UriKind.Relative))).RootElement;
        Assert.Equal(2, health.GetProperty("store_failures").GetInt64());
        Assert.Equal("ok", health.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_blocked_store_does_not_hold_the_answer_past_its_deadline()
    {
        var guarded = new GuardedStore(new Blocking());
        var (client, _) = await Start(null, guarded);
        var clock = Stopwatch.StartNew();

        var answer = Enhanced(await Call(client, Tools.Enhance, Json("review my diff", deadline: 400)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"answered after {clock.ElapsedMilliseconds} ms");
        Assert.Equal("review my diff", answer.Prompt);
        Assert.Equal(1, guarded.Failures);
    }

    [Fact]
    public async Task A_data_directory_that_cannot_be_written_changes_no_answer()
    {
        var blocker = Path.Combine(_data.Path, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");
        using var stores = new SqliteStores(Path.Combine(blocker, "inside"));
        var guarded = new GuardedStore(stores.ForUser("owner"));
        var (client, _) = await Start(Enhancers.Rewriting(), guarded);

        var answer = Enhanced(await Call(client, Tools.Enhance, Json("review my diff")));

        Assert.True(answer.Changed);
        Assert.Equal(1, guarded.Failures);
    }

    [Fact]
    public async Task Health_without_a_store_reports_no_failures()
    {
        var (_, address) = await Start(null, NullStore.Instance);
        using var http = new HttpClient { BaseAddress = address };

        var health = JsonDocument.Parse(await http.GetStringAsync(new Uri("/health", UriKind.Relative))).RootElement;

        Assert.Equal(0, health.GetProperty("store_failures").GetInt64());
    }

    [Fact]
    public async Task Stdio_stores_the_same_way()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        _host = WhetstoneServer.CreateStdio(new PassThrough(), toServer.Reader.AsStream(), toClient.Writer.AsStream(), Owner());
        await _host.StartAsync();
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));

        Enhanced(await Call(client, Tools.Enhance, Json("review my diff")));

        Assert.Equal("review my diff", Assert.Single(Db.Requests(_data.FileFor("owner")))["prompt"]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _stores.Dispose();
        _data.Dispose();
    }
}
