using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.Client;
using Whetstone.Contracts;
using Whetstone.Retrieval;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Redaction;
using Whetstone.Tests.Storage;
using static Whetstone.Tests.McpHarness;

namespace Whetstone.Tests.Retrieval;

/// <summary>Goal 0.3, task 3: the retriever on invented prompts, with a fake index and with a real store.</summary>
public sealed class RetrieverTests : IAsyncDisposable
{
    private const string Key = "test-key";
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly RetrievalOptions Bar = new(1.0);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;
    private WebApplication? _app;

    public RetrieverTests() => _stores = new SqliteStores(_data.Path);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class FakeIndex(IReadOnlyList<(string?, double)> scores, IReadOnlyList<Candidate> candidates) : IRetrievalIndex
    {
        public List<string[]> Searched { get; } = [];

        public Task<IReadOnlyList<(string? TaskKind, double Score)>> ScoresAsync(CancellationToken ct) => Task.FromResult(scores);

        public Task<IReadOnlyList<Candidate>> SearchAsync(IReadOnlyList<string> terms, int limit, CancellationToken ct)
        {
            Searched.Add([.. terms]);
            return Task.FromResult(candidates);
        }
    }

    private sealed class BrokenIndex(Exception failure) : IRetrievalIndex
    {
        public Task<IReadOnlyList<(string? TaskKind, double Score)>> ScoresAsync(CancellationToken ct) => Task.FromException<IReadOnlyList<(string?, double)>>(failure);

        public Task<IReadOnlyList<Candidate>> SearchAsync(IReadOnlyList<string> terms, int limit, CancellationToken ct) => throw failure;
    }

    private sealed class SlowIndex : IRetrievalIndex
    {
        public async Task<IReadOnlyList<(string? TaskKind, double Score)>> ScoresAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }

        public Task<IReadOnlyList<Candidate>> SearchAsync(IReadOnlyList<string> terms, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candidate>>([]);
    }

    private static Candidate Earlier(string id, string prompt, double score = 0.9, double relevance = 5, string? repository = "example/app", string? kind = "review") =>
        new(id, "2026-10-02T09:00:00.0000000Z", prompt, repository, kind, score, relevance);

    private static Retriever Over(IRetrievalIndex index, RetrievalOptions? bar = null) => new(new PassThrough(), index, bar ?? Bar);

    private static Task<EnhanceResponse> Ask(Retriever enhancer, string prompt, CancellationToken ct = default) => enhancer.EnhanceAsync(new EnhanceRequest(prompt), ct);

    private static FakeIndex OneGood(Candidate candidate) => new([(candidate.TaskKind, candidate.Score)], [candidate]);

    [Fact]
    public async Task A_close_earlier_prompt_that_went_well_is_added_after_the_original_which_stays_whole()
    {
        var index = OneGood(Earlier("req-old", "the upload client needs retry and backoff"));

        var answer = await Ask(Over(index), "add retry to the upload client");

        Assert.True(answer.Changed);
        Assert.Equal(Retriever.Reason, answer.Reason);
        Assert.Equal("req-old", answer.SourceRequestId);
        Assert.Equal("add retry to the upload client\n\n---\nA similar earlier prompt that went well (example/app, 2026-10-02):\n> the upload client needs retry and backoff", answer.Prompt);
    }

    [Fact]
    public async Task Below_the_bar_the_answer_is_the_pass_through()
    {
        var index = OneGood(Earlier("req-old", "the upload client needs retry", relevance: 0.4));

        var answer = await Ask(Over(index), "add retry to the upload client");

        Assert.False(answer.Changed);
        Assert.Equal("add retry to the upload client", answer.Prompt);
        Assert.Null(answer.SourceRequestId);
    }

    [Fact]
    public async Task With_no_scored_request_at_all_nothing_is_retrieved()
    {
        var answer = await Ask(Over(new FakeIndex([], [Earlier("req-old", "the upload client needs retry")])), "add retry to the upload client");

        Assert.False(answer.Changed);
    }

    [Fact]
    public async Task A_candidate_below_its_kinds_median_is_skipped_for_the_next_one()
    {
        var scores = new (string?, double)[] { ("review", 0.2), ("review", 0.9) };
        var index = new FakeIndex(scores, [Earlier("req-weak", "retry the upload client", score: 0.2, relevance: 9), Earlier("req-good", "upload client retry plan", score: 0.9, relevance: 4)]);

        var answer = await Ask(Over(index), "add retry to the upload client");

        Assert.Equal("req-good", answer.SourceRequestId);
    }

    [Fact]
    public async Task An_identical_prompt_adds_nothing()
    {
        var index = OneGood(Earlier("req-old", "Add  retry to the Upload client"));

        var answer = await Ask(Over(index), "add retry to the upload client");

        Assert.False(answer.Changed);
    }

    [Fact]
    public async Task A_prompt_with_no_usable_word_is_not_searched()
    {
        var index = OneGood(Earlier("req-old", "anything"));

        var answer = await Ask(Over(index), "ok go");

        Assert.False(answer.Changed);
        Assert.Empty(index.Searched);
    }

    [Fact]
    public async Task A_secret_in_the_prompt_never_reaches_the_search()
    {
        var token = Corpus.Secrets.Single(s => s.Name == "github classic token").Text;
        var index = OneGood(Earlier("req-old", "unrelated"));

        await Ask(Over(index), $"deploy the service with {token} now");

        var terms = Assert.Single(index.Searched);
        Assert.Contains("deploy", terms);
        Assert.DoesNotContain(terms, t => token.Contains(t, StringComparison.OrdinalIgnoreCase) && t.Length > 8);
        Assert.DoesNotContain(terms, t => t == "redacted");
    }

    [Fact]
    public async Task The_quote_marks_every_line_names_the_repository_and_cuts_a_long_prompt()
    {
        var lines = new string('x', Retriever.MaxQuotedChars + 500);
        var index = OneGood(Earlier("req-old", "first line about parsing\nsecond line about parsing\n" + lines, repository: "example/other"));

        var answer = await Ask(Over(index), "parsing question");

        Assert.Contains("(example/other, 2026-10-02):\n> first line about parsing\n> second line about parsing\n> xxx", answer.Prompt, StringComparison.Ordinal);
        Assert.EndsWith("…", answer.Prompt, StringComparison.Ordinal);
        Assert.True(answer.Prompt.Length < "parsing question".Length + Retriever.MaxQuotedChars + 200);
    }

    [Fact]
    public async Task A_failing_index_returns_the_inner_answer_and_is_counted()
    {
        var retriever = Over(new BrokenIndex(new InvalidOperationException("boom")));

        var answer = await Ask(retriever, "add retry to the upload client");

        Assert.False(answer.Changed);
        Assert.Equal("add retry to the upload client", answer.Prompt);
        Assert.Equal(1, retriever.Failures);
    }

    [Fact]
    public async Task A_lookup_that_runs_out_of_time_returns_the_inner_answer_and_is_counted()
    {
        var retriever = Over(new SlowIndex());
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var answer = await Ask(retriever, "add retry to the upload client", budget.Token);

        Assert.False(answer.Changed);
        Assert.Equal(1, retriever.Failures);
    }

    // ---- with a real store ----

    private static async Task Seed(IStore store, string id, string prompt, double? score, bool? overridden = null, string kind = "review")
    {
        var answer = new EnhanceResponse(prompt, false, null, null, "", null, id, false);
        await store.RecordAsync(RequestRow.From(new EnhanceRequest(prompt, new EnhanceContext("example/app", "abc1234", kind, "test")), answer, At), CancellationToken.None);
        if (score is not null || overridden is not null)
            await store.RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(null, overridden, score)), At.AddMinutes(5)), CancellationToken.None);
    }

    // BM25 weighs a word by how rare it is among the stored prompts, so a handful of rows gives every word almost no weight.
    private static async Task Filler(IStore store)
    {
        for (var i = 0; i < 30; i++)
            await Seed(store, $"req-filler-{i}", $"unrelated note number {i} about gardening and weather", score: null);
    }

    [Fact]
    public async Task With_a_real_store_only_a_scored_not_overridden_request_of_this_user_is_found()
    {
        var owner = _stores.ForUser("owner");
        await Filler(owner);
        await Seed(owner, "req-unscored", "upload client retry backoff idea", score: null);
        await Seed(owner, "req-overridden", "upload client retry backoff attempt", score: 0.95, overridden: true);
        await Seed(owner, "req-good", "upload client retry backoff plan", score: 0.9);
        await Seed(owner, "req-low", "something else entirely different", score: 0.1);
        await Seed(_stores.ForUser("guest"), "req-guest", "upload client retry backoff guest note", score: 0.99);

        var answer = await Ask(Over(_stores.IndexFor("owner"), new RetrievalOptions(0.1)), "add retry and backoff to the upload client");

        Assert.Equal("req-good", answer.SourceRequestId);
        Assert.Contains("> upload client retry backoff plan", answer.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("guest", answer.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Through_the_server_the_rewrite_is_sent_the_row_keeps_the_original_and_the_source_and_health_counts_failures()
    {
        await Filler(_stores.ForUser("owner"));
        await Seed(_stores.ForUser("owner"), "req-good", "upload client retry backoff plan", score: 0.9);
        var retriever = Over(_stores.IndexFor("owner"), new RetrievalOptions(0.1));
        _app = WhetstoneServer.Create(new ServerSettings(0, Key), retriever, _stores.ForUser("owner"));
        await _app.StartAsync();
        var address = new Uri(_app.Urls.First());
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address, "/v1/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Key}" },
        }));

        var call = await Call(client, Tools.Enhance, """{"prompt":"add retry and backoff to the upload client"}""");

        var answer = Enhanced(call);
        Assert.True(answer.Changed);
        Assert.Equal("req-good", answer.SourceRequestId);
        var row = Db.Requests(_data.FileFor("owner")).Single(r => (string)r["request_id"]! == answer.RequestId);
        Assert.Equal("add retry and backoff to the upload client", row["prompt"]);
        Assert.Equal("req-good", row["source_request_id"]);
        Assert.Equal(1L, row["changed"]);

        using var http = new HttpClient { BaseAddress = address };
        var health = JsonDocument.Parse(await http.GetStringAsync(new Uri("/health", UriKind.Relative))).RootElement;
        Assert.Equal(0, health.GetProperty("retrieval_failures").GetInt64());
    }

    [Fact]
    public async Task A_prompt_shorter_than_the_minimum_gets_the_inner_answer_and_no_lookup()
    {
        var index = OneGood(Earlier("req-1", "add retry and backoff to the upload client, with a cap on attempts"));

        var answer = await Ask(Over(index, new RetrievalOptions(1.0, MinPromptChars: 40)), "retry the upload");

        Assert.False(answer.Changed);
        Assert.Empty(index.Searched);
    }

    [Fact]
    public async Task A_candidate_shorter_than_the_minimum_is_passed_over_for_a_longer_one()
    {
        var shortOne = Earlier("req-short", "retry upload", relevance: 9);
        var longOne = Earlier("req-long", "add retry and backoff to the upload client, with a cap on attempts", relevance: 5);
        var index = new FakeIndex([("review", 0.9)], [shortOne, longOne]);

        var answer = await Ask(Over(index, new RetrievalOptions(1.0, MinPromptChars: 30)), "make the upload client retry failed parts with backoff");

        Assert.Equal("req-long", answer.SourceRequestId);
    }
}
