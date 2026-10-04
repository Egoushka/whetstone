using Whetstone.Contracts;
using Whetstone.Retrieval;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Retrieval;

/// <summary>Goal 0.3, task 1: eligibility and the replay over invented prompts.</summary>
public sealed class ReplayTests
{
    private static ExportRecord Row(string id, string prompt, double? score, string kind = "review", bool? overridden = null, string repository = "example/app") =>
        new(id, "2026-10-01T09:00:00.0000000Z", prompt, false, new ExportContext(repository, null, kind, "test"), new ExportAnswer(false, null, null, false),
            score is null && overridden is null ? null : new ExportOutcome(null, overridden, score, null, null, "2026-10-01T10:00:00.0000000Z"));

    [Fact]
    public void Unscored_and_overridden_rows_are_never_eligible()
    {
        var rows = new[] { Row("a", "alpha", 0.9), Row("b", "beta", null), Row("c", "gamma", 0.95, overridden: true), Row("z", "zeta", 0.1) };

        Assert.Equal(["a"], Eligibility.Eligible(rows).Select(r => r.RequestId));
    }

    [Fact]
    public void A_small_kind_uses_the_overall_median_and_a_large_kind_its_own()
    {
        // Ten reviews scored 0.1..1.0 (median 0.55); two fixes at 0.2 and 0.9. Overall median of all twelve is also 0.55.
        var reviews = Enumerable.Range(1, 10).Select(i => Row($"r{i}", $"review {i}", i / 10.0));
        var fixes = new[] { Row("f1", "fix one", 0.2, kind: "fix"), Row("f2", "fix two", 0.9, kind: "fix") };

        var eligible = Eligibility.Eligible([.. reviews, .. fixes]).Select(r => r.RequestId).ToHashSet();

        Assert.Equal(["f2", "r10", "r6", "r7", "r8", "r9"], eligible.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("r5", eligible);
        Assert.DoesNotContain("f1", eligible);
    }

    [Fact]
    public void The_best_earlier_match_is_found_and_the_query_never_matches_itself()
    {
        var rows = new[]
        {
            Row("q", "refactor the invoice parser to stream lines instead of reading the whole file", 0.9),
            Row("m", "make the invoice parser stream its lines and not read the whole file at once", 0.95),
            Row("x", "rotate the signing certificate before the weekend", 0.97),
        };

        var pairs = Replay.Run(rows).Pairs.ToDictionary(p => p.Query.RequestId);

        Assert.Equal("m", pairs["q"].Match?.RequestId);
        Assert.True(pairs["q"].Score > 0);
        Assert.NotEqual("q", pairs["m"].Match?.RequestId);
    }

    [Fact]
    public void An_identical_prompt_is_not_a_match()
    {
        var rows = new[] { Row("a", "Rename the  Widget class", 0.9), Row("b", "rename the widget CLASS", 0.95) };

        Assert.All(Replay.Run(rows).Pairs, p => Assert.Null(p.Match));
    }

    [Fact]
    public void Prompts_sharing_no_word_do_not_match_and_redaction_markers_do_not_count_as_words()
    {
        var rows = new[]
        {
            Row("a", "use [REDACTED:token] for billing", 0.9),
            Row("b", "paste [REDACTED:token] into config", 0.95),
            Row("c", "zzz qqq", 0.99),
        };

        var pairs = Replay.Run(rows).Pairs.ToDictionary(p => p.Query.RequestId);

        Assert.Null(pairs["a"].Match);
        Assert.Null(pairs["c"].Match);
    }

    [Fact]
    public void Only_scored_requests_are_replayed_and_the_counts_are_reported()
    {
        var rows = new[] { Row("a", "alpha beta gamma", 0.9), Row("b", "alpha beta delta", null), Row("c", "alpha beta epsilon", 0.4) };

        var report = Replay.Run(rows);

        Assert.Equal((3, 2), (report.Total, report.Scored));
        Assert.Equal(["a", "c"], report.Pairs.Select(p => p.Query.RequestId));
        Assert.Equal(1, report.Eligible);
    }

    [Fact]
    public async Task The_command_prints_pairs_reads_only_and_leaves_the_file_unchanged()
    {
        using var data = new TempData();
        using var stores = new SqliteStores(data.Path);
        var store = stores.ForUser("owner");
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        foreach (var (id, prompt, score) in new[] { ("req-1", "add retry with backoff to the upload client", 0.9), ("req-2", "the upload client needs retry and backoff", 0.8), ("req-3", "unrelated words only here", 0.7) })
        {
            var answer = new EnhanceResponse(prompt, false, null, null, "", null, id, false);
            await store.RecordAsync(RequestRow.From(new EnhanceRequest(prompt, new EnhanceContext("example/app", "abc1234", "review", "test")), answer, at), CancellationToken.None);
            await store.RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(null, null, score)), at.AddMinutes(5)), CancellationToken.None);
        }
        var before = File.ReadAllBytes(data.FileFor("owner"));
        var (output, error) = (new StringWriter(), new StringWriter());

        var code = await Commands.ReplayAsync([], stores.AdminFor("owner"), output, error, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        Assert.StartsWith("3 requests, 3 scored, 2 eligible to be retrieved", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("the upload client needs retry and backoff", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("no match", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(data.FileFor("owner")));
    }

    [Fact]
    public async Task The_command_on_a_user_with_no_store_says_nothing_is_scored()
    {
        using var data = new TempData();
        using var stores = new SqliteStores(data.Path);
        var output = new StringWriter();

        var code = await Commands.ReplayAsync([], stores.AdminFor("owner"), output, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("0 requests, 0 scored, 0 eligible", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Nothing is scored yet", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_refuses_an_option()
    {
        using var data = new TempData();
        using var stores = new SqliteStores(data.Path);
        var error = new StringWriter();

        var code = await Commands.ReplayAsync(["--all"], stores.AdminFor("owner"), new StringWriter(), error, CancellationToken.None);

        Assert.Equal(Commands.Usage, code);
        Assert.Contains(Commands.ReplayUsage, error.ToString(), StringComparison.Ordinal);
    }
}
