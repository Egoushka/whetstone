using Whetstone.Contracts;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.7, decision 1: Thompson sampling over several challengers, a floor per arm and a held-out share the bandit cannot touch.</summary>
public sealed class AllocationTests : IDisposable
{
    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public AllocationTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    [Fact]
    public void Shares_sum_to_one_and_none_falls_below_the_floor()
    {
        var shares = Allocation.Shares([new Tally(90, 10), new Tally(10, 90), new Tally(50, 50)]);

        Assert.Equal(1.0, shares.Sum(), 6);
        Assert.All(shares, s => Assert.True(s >= Allocation.Floor - 1e-9));
    }

    [Fact]
    public void The_arm_that_has_done_best_gets_the_most_and_the_worst_sits_on_the_floor()
    {
        var shares = Allocation.Shares([new Tally(40, 60), new Tally(95, 5), new Tally(5, 95)]);

        Assert.True(shares[1] > 0.6);
        Assert.True(shares[1] > shares[0]);
        Assert.Equal(Allocation.Floor, shares[2], 3);
    }

    [Fact]
    public void Arms_with_no_history_share_about_equally_and_the_same_tallies_give_the_same_shares()
    {
        var shares = Allocation.Shares([new Tally(0, 0), new Tally(0, 0), new Tally(0, 0)]);

        Assert.All(shares, s => Assert.InRange(s, 0.28, 0.39));
        Assert.Equal(shares, Allocation.Shares([new Tally(0, 0), new Tally(0, 0), new Tally(0, 0)]));
    }

    [Fact]
    public void The_held_out_requests_are_the_same_ones_whatever_the_bandit_does()
    {
        double[] even = [1.0 / 3, 1.0 / 3, 1.0 / 3], skewed = [0.7, 0.15, 0.15];
        var ids = Enumerable.Range(0, 5_000).Select(i => $"req-{i}").ToList();

        var a = ids.Where(id => Arms.Assign("k", id, even).Arm == Arms.HeldOut).ToList();
        var b = ids.Where(id => Arms.Assign("k", id, skewed).Arm == Arms.HeldOut).ToList();
        var fixedByHash = ids.Where(id => Arms.Assign("k", id, true) == Arms.HeldOut).ToList();

        Assert.Equal(a, b);
        Assert.Equal(a, fixedByHash);
        Assert.InRange(a.Count, 440, 560);
    }

    [Fact]
    public void The_rest_follows_the_shares_and_every_challenger_is_reachable()
    {
        double[] shares = [0.5, 0.2, 0.15, 0.15];
        var served = Enumerable.Range(0, 40_000).Select(i => Arms.Assign("k", $"req-{i}", shares)).Where(a => a.Arm != Arms.HeldOut).ToList();

        var counts = Enumerable.Range(-1, 4).Select(c => served.Count(a => a.Challenger == c)).ToArray();

        Assert.InRange(counts[0], 17_000, 19_000);
        Assert.InRange(counts[1], 6_800, 7_800);
        Assert.InRange(counts[2], 5_000, 5_800);
        Assert.InRange(counts[3], 5_000, 5_800);
        Assert.All(served.Where(a => a.Arm == Arms.Champion), a => Assert.Equal(-1, a.Challenger));
    }

    [Fact]
    public async Task A_kind_may_hold_up_to_five_challengers_and_ForKind_lists_them_oldest_first()
    {
        var admin = _stores.AdminFor("owner");
        await _stores.TemplatesFor("owner").SeedAsync([new Template("base", "1", "typed/implement", "", "Base.", "hand")], TemplateRoles.Champion, CancellationToken.None);
        for (var i = 1; i <= KindTemplates.MaxChallengers; i++)
            Assert.Null(await admin.AddTemplateAsync(new Template($"c{i}", "1", "typed/implement", "", $"Text {i}.", "hand"), TemplateRoles.Challenger, null, CancellationToken.None));

        var sixth = await admin.AddTemplateAsync(new Template("c6", "1", "typed/implement", "", "Text 6.", "hand"), TemplateRoles.Challenger, null, CancellationToken.None);
        var found = await _stores.TemplatesFor("owner").ForKindAsync("typed/implement", CancellationToken.None);

        Assert.Contains("5 challengers", sixth, StringComparison.Ordinal);
        Assert.Equal(["c1", "c2", "c3", "c4", "c5"], found.Challengers.Select(t => t.Id));
    }

    [Fact]
    public async Task A_second_champion_is_still_refused()
    {
        var admin = _stores.AdminFor("owner");
        await _stores.TemplatesFor("owner").SeedAsync([new Template("base", "1", "k", "", "Base.", "hand")], TemplateRoles.Champion, CancellationToken.None);

        var problem = await admin.AddTemplateAsync(new Template("other", "1", "k", "", "Other.", "hand"), TemplateRoles.Champion, null, CancellationToken.None);

        Assert.Contains("already has a champion", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tallies_count_completions_not_reworked_as_wins_and_the_rest_of_the_reported_runs_as_losses()
    {
        var at = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        async Task Run(string id, string arm, string template, bool completed, string? reason = null)
        {
            var response = new EnhanceResponse("p", true, template, "1", "r", "k", id, false) { Arm = arm };
            await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest("a prompt", new EnhanceContext(null, null, "k", "claude-code/agent")), response, at), CancellationToken.None);
            await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(Completed: completed, CorrectionReason: reason)), at), CancellationToken.None);
        }
        await Run("1", Arms.Challenger, "c1", true);
        await Run("2", Arms.Challenger, "c1", true, "wrong_target");
        await Run("3", Arms.Challenger, "c1", false);
        await Run("4", Arms.Champion, "base", true, "new_task");

        var tallies = await _stores.TemplatesFor("owner").TalliesAsync("k", CancellationToken.None);

        Assert.Equal(new Tally(1, 2), tallies[Allocation.Key("c1", "1")]);
        Assert.Equal(new Tally(1, 0), tallies[Allocation.Key("base", "1")]);
    }

    [Fact]
    public void Several_challengers_are_judged_one_at_a_time_and_the_sample_ratio_looks_at_the_held_out_share_only()
    {
        var champion = new Template("a", "1", "k", "", "x", "s");
        var templates = new KindTemplates(champion, [new Template("b", "1", "k", "", "y", "s"), new Template("c", "1", "k", "", "z", "s")]);
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        TrialRun Run(int minute, string arm, string? template, bool done) => new(start.AddMinutes(minute), arm, done, null, null, null, "m", template, template is null ? null : "1");
        var runs = new List<TrialRun>();
        runs.AddRange(Enumerable.Range(0, 60).Select(i => Run(i, Arms.Champion, "a", i % 10 < 7)));
        runs.AddRange(Enumerable.Range(0, 30).Select(i => Run(100 + i, Arms.Challenger, "b", true)));
        runs.AddRange(Enumerable.Range(0, 10).Select(i => Run(200 + i, Arms.Challenger, "c", i % 2 == 0)));
        runs.AddRange(Enumerable.Range(0, 11).Select(i => Run(300 + i, Arms.HeldOut, null, true)));

        var report = Trials.Evaluate("k", runs, templates);

        Assert.Empty(report.Looks);
        Assert.Equal(["b", "c"], report.Rivals!.Select(r => r.TemplateId));
        Assert.Equal([30, 10], report.Rivals!.Select(r => r.Counted));
        Assert.Single(report.Rivals![0].Looks);
        Assert.Empty(report.Rivals![1].Looks);
        Assert.StartsWith("ok", report.SampleRatio!, StringComparison.Ordinal);
        Assert.Contains("held-out share only", report.SampleRatio!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_held_out_share_far_from_ten_percent_fails_the_check_however_the_rest_is_split()
    {
        var champion = new Template("a", "1", "k", "", "x", "s");
        var templates = new KindTemplates(champion, [new Template("b", "1", "k", "", "y", "s"), new Template("c", "1", "k", "", "z", "s")]);
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        TrialRun Run(int minute, string arm) => new(start.AddMinutes(minute), arm, true, null, null, null, "m");
        var runs = Enumerable.Range(0, 70).Select(i => Run(i, Arms.Champion)).Concat(Enumerable.Range(0, 30).Select(i => Run(100 + i, Arms.HeldOut))).ToList();

        Assert.StartsWith("FAILED", Trials.Evaluate("k", runs, templates).SampleRatio!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_enhancer_serves_every_challenger_of_a_kind_with_two_or_more_and_keeps_the_held_out_arm_bare()
    {
        var champion = new Template("champ", "1", "agent/explore", "", "CHAMPION TEXT", TemplateSources.BuiltIn);
        var one = new Template("one", "1", "agent/explore", "", "ONE TEXT", TemplateSources.BuiltIn);
        var two = new Template("two", "1", "agent/explore", "", "TWO TEXT", TemplateSources.BuiltIn);
        var enhancer = new TemplateEnhancer(new Echo(), new Fake(new KindTemplates(champion, [one, two])));
        var answers = new List<EnhanceResponse>();
        for (var i = 0; i < 600; i++)
            answers.Add(await enhancer.EnhanceAsync(new EnhanceRequest($"id-{i}", new EnhanceContext("r", "c", "agent/explore", "claude-code/agent")), CancellationToken.None));

        Assert.Contains(answers, a => a.TemplateId == "one" && a.Arm == Arms.Challenger);
        Assert.Contains(answers, a => a.TemplateId == "two" && a.Arm == Arms.Challenger);
        Assert.Contains(answers, a => a.TemplateId == "champ" && a.Arm == Arms.Champion);
        Assert.All(answers.Where(a => a.Arm == Arms.HeldOut), a => Assert.Equal((false, null), (a.Changed, a.TemplateId)));
        Assert.InRange(answers.Count(a => a.Arm == Arms.HeldOut), 30, 90);
    }

    private sealed class Fake(KindTemplates found) : ITemplates
    {
        public Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct) => Task.FromResult(found);

        public Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Echo : IEnhancer
    {
        public Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct) => Task.FromResult(PassThrough.Of(request, PassThrough.NothingLearned) with { RequestId = request.Prompt });
    }
}
