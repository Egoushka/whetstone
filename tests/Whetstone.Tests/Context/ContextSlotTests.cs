using System.Text.Json;
using Whetstone.Context;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Context;

/// <summary>Goal 0.9: hint and memory slots, the memory ranking learned in whetstone's own store, and ablations in the report.</summary>
public sealed class ContextSlotTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Template WithMemory = new("mem", "1", "agent/explore", "", "Known decisions:\n{{memory}}\nFailing test: {{hint_last_ci_failure}}", TemplateSources.BuiltIn);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public ContextSlotTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class Found(KindTemplates found) : ITemplates
    {
        public Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct) => Task.FromResult(found);

        public Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Echo : IEnhancer
    {
        public Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct) =>
            Task.FromResult(PassThrough.Of(request, PassThrough.NothingLearned) with { RequestId = $"req-{request.Prompt.GetHashCode():x}" });
    }

    private sealed class Source(Func<CancellationToken, Task<IReadOnlyList<Memory>>> recall) : IMemorySource
    {
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<Memory>> RecallAsync(string query, string? repository, int max, CancellationToken ct)
        {
            Queries.Add(query);
            return recall(ct);
        }
    }

    private static readonly IReadOnlyList<Memory> Recalled =
    [
        new("m1", "doc:adr-4", "Templates never change the original prompt.", 0.9),
        new("m2", "doc:adr-4", "A duplicate from the same document.", 0.8),
        new("m3", "doc:noise", "An unrelated note.", 0.7),
    ];

    /// <summary>A request id whose arm is the champion, so the template is served.</summary>
    private static async Task<EnhanceResponse> Served(TemplateEnhancer enhancer, EnhanceContext context)
    {
        for (var i = 0; ; i++)
        {
            var answer = await enhancer.EnhanceAsync(new EnhanceRequest($"find where settings are read {i}", context), CancellationToken.None);
            if (answer.Arm == Arms.Champion)
                return answer;
        }
    }

    private static readonly EnhanceContext Context = new("example-app", null, "agent/explore", "claude-code/agent", Hints: new Dictionary<string, string> { ["last_ci_failure"] = "tests/ParserTests.cs" });

    [Fact]
    public void Hint_and_memory_slots_fill_and_an_empty_one_drops_its_line()
    {
        var full = WithMemory.Render("p", Context, "<whetstone_memory>\n- x\n</whetstone_memory>")!;
        var bare = WithMemory.Render("p", Context with { Hints = null })!;

        Assert.Contains("Failing test: tests/ParserTests.cs", full, StringComparison.Ordinal);
        Assert.Contains("- x", full, StringComparison.Ordinal);
        Assert.DoesNotContain("Failing test", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("{{memory}}", bare, StringComparison.Ordinal);
        Assert.True(WithMemory.UsesMemory);
        Assert.Empty(WithMemory.UnknownSlots());
        Assert.Null((WithMemory with { After = "{{nonsense}}" }).Render("p", Context));
    }

    [Fact]
    public void A_documents_weight_moves_only_after_enough_exposures_and_noise_is_dropped()
    {
        Assert.Equal(1, MemoryRanking.Weight(new MemoryRecord(4, 0)));
        Assert.Equal(0, MemoryRanking.Weight(new MemoryRecord(12, 1)));
        Assert.True(MemoryRanking.Weight(new MemoryRecord(10, 9)) > 1.5);

        var ranked = MemoryRanking.Rank(Recalled, new Dictionary<string, MemoryRecord> { ["doc:noise"] = new(12, 0) });

        Assert.Equal(["m1"], ranked.Select(m => m.Id));
        Assert.StartsWith("<whetstone_memory>", MemoryRanking.Block(ranked), StringComparison.Ordinal);
        Assert.Equal("", MemoryRanking.Block([]));
    }

    [Fact]
    public async Task A_template_with_a_memory_slot_is_filled_and_its_exposures_are_recorded()
    {
        var ledger = _stores.MemoryLedgerFor("owner");
        var enhancer = new TemplateEnhancer(new Echo(), new Found(new KindTemplates(WithMemory, (Template?)null)), new Source(_ => Task.FromResult(Recalled)), ledger);

        var answer = await Served(enhancer, Context);

        Assert.Contains("<whetstone_memory>\n- Templates never change the original prompt.\n- An unrelated note.\n</whetstone_memory>", answer.Prompt, StringComparison.Ordinal);
        Assert.Equal(["doc:adr-4", "doc:noise"], Db.Query(_data.FileFor("owner"), $"SELECT document_key FROM memory_exposures WHERE request_id = '{answer.RequestId}' ORDER BY rank").Select(r => (string)r["document_key"]!));
        Assert.DoesNotContain(Db.Query(_data.FileFor("owner"), "SELECT * FROM memory_exposures"), r => r.Values.Any(v => v is string s && s.Contains("original prompt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Only_redacted_text_is_sent_to_the_memory_service()
    {
        var source = new Source(_ => Task.FromResult(Recalled));
        var enhancer = new TemplateEnhancer(new Echo(), new Found(new KindTemplates(WithMemory, (Template?)null)), source);
        var secret = "ghp_" + new string('a', 36);

        for (var i = 0; i < 20; i++)
            await enhancer.EnhanceAsync(new EnhanceRequest($"why does the token {secret} fail {i}", Context), CancellationToken.None);

        Assert.NotEmpty(source.Queries);
        Assert.All(source.Queries, q => Assert.DoesNotContain(secret, q, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_or_slow_recall_serves_the_template_without_the_memory_line()
    {
        var failing = new TemplateEnhancer(new Echo(), new Found(new KindTemplates(WithMemory, (Template?)null)), new Source(_ => throw new HttpRequestException("down")));
        var slow = new TemplateEnhancer(new Echo(), new Found(new KindTemplates(WithMemory, (Template?)null)), new Source(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Recalled;
        }));

        foreach (var enhancer in new[] { failing, slow })
        {
            var answer = await Served(enhancer, Context);
            Assert.Equal("mem", answer.TemplateId);
            Assert.DoesNotContain("whetstone_memory", answer.Prompt, StringComparison.Ordinal);
            Assert.Contains("Failing test: tests/ParserTests.cs", answer.Prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_ledger_credits_a_document_with_runs_that_completed_without_rework()
    {
        var store = _stores.ForUser("owner");
        var ledger = _stores.MemoryLedgerFor("owner");
        for (var i = 0; i < 4; i++)
        {
            var answer = new EnhanceResponse("p", false, null, null, "", "agent/explore", $"r{i}", false);
            await store.RecordAsync(RequestRow.From(new EnhanceRequest("p", Context), answer, T0.AddMinutes(i)), CancellationToken.None);
            await ledger.RecordExposuresAsync($"r{i}", [new MemoryExposure("doc:adr-4", "m1", "h", 1, 0.9)], CancellationToken.None);
            if (i < 3)
                await store.RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest($"r{i}", new FeedbackOutcome(Completed: true, AskedAgain: i == 2, CorrectionReason: i == 2 ? "incomplete" : null)), T0), CancellationToken.None);
        }

        var records = await ledger.MemoryRecordsAsync(["doc:adr-4", "doc:none"], CancellationToken.None);

        Assert.Equal(new MemoryRecord(3, 2), records["doc:adr-4"]);
        Assert.False(records.ContainsKey("doc:none"));
    }

    [Fact]
    public void Recall_results_are_keyed_by_their_document_or_else_their_own_id()
    {
        using var json = JsonDocument.Parse("""
            {"results":[{"id":"a","text":"one","document_id":"decision-x","scores":{"final":0.8}},{"id":"b","text":"two","document_id":null,"scores":{"final":0.5}},{"id":"c","text":""}]}
            """);

        var found = HindsightMemory.Parse(json.RootElement, 10);

        Assert.Equal([("doc:decision-x", 0.8), ("memory:b", 0.5)], found.Select(m => (m.DocumentKey, m.Score)));
    }

    [Fact]
    public async Task The_report_compares_each_ablation_with_sessions_that_held_nothing_out()
    {
        var store = _stores.ForUser("owner");
        for (var i = 0; i < 220; i++)
        {
            var ablation = i % 2 == 0 ? "rule:stop-rule" : null;
            var answer = new EnhanceResponse("p", false, null, null, "", "typed/implement", $"t{i}", false);
            await store.RecordAsync(RequestRow.From(new EnhanceRequest("p", new EnhanceContext("example-app", null, "typed/implement", "claude-code", $"s-{i}", Ablation: ablation)), answer, T0.AddMinutes(i)), CancellationToken.None);
            await store.RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest($"t{i}", new FeedbackOutcome(Completed: i % 20 is not (0 or 1), AskedAgain: false)), T0.AddMinutes(i)), CancellationToken.None);
        }
        var output = new StringWriter();

        await Commands.ReportAsync([], _stores.AdminFor("owner"), output, new StringWriter(), CancellationToken.None);

        Assert.Contains("  nothing held out: 110 sessions, 110 runs", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("  rule:stop-rule: 110 sessions, 110 runs, completed 90% (99/110), rework 0% (0/110); no effect after 100 sessions: a removal candidate", output.ToString(), StringComparison.Ordinal);
    }
}
