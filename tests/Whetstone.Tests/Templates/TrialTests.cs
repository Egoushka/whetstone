using Whetstone.Contracts;
using Whetstone.Templates;

namespace Whetstone.Tests.Templates;

/// <summary>The promotion rule of the design as arithmetic: looks, z, non-inferiority, sample ratio, the regression monitor.</summary>
public class TrialTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly KindTemplates Both = new(new Template("a", "1", "k", "", "x", "s"), new Template("b", "1", "k", "", "y", "s"));

    private static TrialRun Run(int minute, string arm, bool completed, double? cost = null, bool? asked = null, string? model = "m") =>
        new(T0.AddMinutes(minute), arm, completed, asked, cost, 1000, model);

    /// <summary>Runs of one arm in order: the first <paramref name="wins"/> of <paramref name="count"/> complete, spread evenly in time.</summary>
    private static List<TrialRun> Many(string arm, int count, int wins, int offset = 0, double? cost = null, bool? asked = null) =>
        [.. Enumerable.Range(0, count).Select(i => Run(offset + (i * 2), arm, i * wins / count != (i + 1) * wins / count, cost, asked))];

    [Fact]
    public void The_z_of_two_rates_is_the_pooled_two_proportion_statistic()
    {
        Assert.Equal(2.83, Trials.Difference(60, 100, 40, 100), 2);
        Assert.Equal(0, Trials.Difference(10, 10, 10, 10));
        Assert.Equal(0, Trials.Difference(0, 0, 5, 10));
    }

    [Fact]
    public void Only_runs_with_a_completion_since_the_model_last_changed_count()
    {
        var runs = new List<TrialRun> { Run(1, Arms.Champion, true, model: "a"), Run(2, Arms.Champion, true, model: "a"), Run(3, Arms.Champion, true, model: "b"), Run(4, Arms.Champion, false, model: "b") };
        runs.Add(new TrialRun(T0.AddMinutes(5), Arms.Champion, null, null, null, null, "b"));

        var (counted, model) = Trials.Window(runs);

        Assert.Equal(("b", 2), (model, counted.Count));
    }

    [Fact]
    public void A_run_without_a_model_is_its_own_group_not_mixed_with_the_rest()
    {
        var runs = new List<TrialRun> { Run(1, Arms.Champion, true, model: "a"), Run(2, Arms.Champion, true, model: null), Run(3, Arms.Champion, true, model: null) };

        var (counted, model) = Trials.Window(runs);

        Assert.Equal((null, 2), (model, counted.Count));
    }

    [Fact]
    public void No_decision_is_taken_before_the_first_look()
    {
        var runs = Many(Arms.Champion, 60, 42).Concat(Many(Arms.Challenger, 29, 29, 1)).ToList();

        Assert.Empty(Trials.Evaluate("k", runs, Both).Looks);
    }

    [Fact]
    public void A_clearly_better_challenger_is_promoted_at_the_first_look()
    {
        var runs = Many(Arms.Champion, 60, 42).Concat(Many(Arms.Challenger, 30, 29, 1)).ToList();

        var look = Assert.Single(Trials.Evaluate("k", runs, Both).Looks);

        Assert.Equal((30, "promote"), (look.Runs, look.Verdict));
        Assert.True(look.Z > Trials.Z);
    }

    [Fact]
    public void A_clearly_worse_challenger_is_retired()
    {
        var runs = Many(Arms.Champion, 60, 54).Concat(Many(Arms.Challenger, 30, 15, 1)).ToList();

        Assert.Equal("retire", Assert.Single(Trials.Evaluate("k", runs, Both).Looks).Verdict);
    }

    [Fact]
    public void With_no_difference_the_trial_keeps_running_until_the_last_look_then_retires()
    {
        var runs = Many(Arms.Champion, 300, 210).Concat(Many(Arms.Challenger, 200, 140, 1)).ToList();

        var looks = Trials.Evaluate("k", runs, Both).Looks;

        Assert.Equal([30, 60, 100, 150, 200], looks.Select(l => l.Runs));
        Assert.All(looks.Take(4), l => Assert.Equal("keep running", l.Verdict));
        Assert.Equal("retire", looks[^1].Verdict);
        Assert.Contains("no difference found", looks[^1].Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_look_uses_only_the_champion_runs_that_existed_when_the_challenger_reached_it()
    {
        var early = Many(Arms.Champion, 40, 28, -100);
        var challenger = Many(Arms.Challenger, 30, 21, 1);
        var late = Many(Arms.Champion, 100, 0, 1000);

        var look = Assert.Single(Trials.Evaluate("k", [.. early, .. challenger, .. late], Both).Looks);

        Assert.Equal(40, look.ChampionRuns);
    }

    [Fact]
    public void A_cheaper_challenger_is_promoted_only_when_completion_and_re_asks_are_shown_no_worse()
    {
        // Fifteen points of margin cannot be verified with fewer than about a hundred runs per arm (the design's note on the margin).
        var champion = Many(Arms.Champion, 150, 105, cost: 1000, asked: false);
        var cheap = Many(Arms.Challenger, 100, 70, 1, cost: 600, asked: false);

        var shown = Trials.Evaluate("k", [.. champion, .. cheap], Both).Looks[^1];
        var unreported = Trials.Evaluate("k", [.. champion.Select(r => r with { AskedAgain = null }), .. cheap.Select(r => r with { AskedAgain = null })], Both).Looks[^1];

        Assert.Equal((100, "promote", true), (shown.Runs, shown.Verdict, shown.Why.Contains("10% lower", StringComparison.Ordinal)));
        Assert.Equal("keep running", unreported.Verdict);
        Assert.Contains("asked_again", unreported.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cheaper_challenger_that_may_complete_much_less_often_is_not_promoted()
    {
        var champion = Many(Arms.Champion, 150, 135, cost: 1000, asked: false);
        var cheap = Many(Arms.Challenger, 100, 60, 1, cost: 500, asked: false);

        Assert.DoesNotContain(Trials.Evaluate("k", [.. champion, .. cheap], Both).Looks, look => look.Verdict == "promote");
    }

    [Fact]
    public void The_bootstrap_interval_repeats_exactly()
    {
        List<double> a = [.. Enumerable.Range(1, 40).Select(i => (double)i)], b = [.. Enumerable.Range(10, 40).Select(i => (double)i)];

        Assert.Equal(Trials.BootstrapRatio(a, b), Trials.BootstrapRatio(a, b));
        var (low, high) = Trials.BootstrapRatio(a, b);
        Assert.True(low < high);
    }

    [Theory]
    [InlineData(600, 300, 100, true, false)]
    [InlineData(800, 100, 100, true, true)]
    [InlineData(900, 0, 100, false, false)]
    [InlineData(600, 0, 400, false, true)]
    public void The_sample_ratio_check_passes_the_planned_split_and_fails_a_broken_one(int champion, int challenger, int held, bool withChallenger, bool fails)
    {
        var runs = new List<TrialRun>();
        runs.AddRange(Many(Arms.Champion, champion, 0));
        runs.AddRange(Many(Arms.Challenger, challenger, 0));
        runs.AddRange(Many(Arms.HeldOut, held, 0));
        var templates = withChallenger ? Both : new KindTemplates(Both.Champion, null);

        var ratio = Trials.Evaluate("k", runs, templates).SampleRatio;

        Assert.Equal(fails, ratio!.StartsWith("FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public void Too_few_runs_say_nothing_about_the_split_or_the_monitor()
    {
        var report = Trials.Evaluate("k", [.. Many(Arms.Champion, 10, 5), .. Many(Arms.HeldOut, 2, 1)], new KindTemplates(Both.Champion, null));

        Assert.Null(report.SampleRatio);
        Assert.Null(report.Monitor);
    }

    [Fact]
    public void A_champion_that_completes_less_often_than_the_held_out_arm_is_flagged_for_demotion()
    {
        var report = Trials.Evaluate("k", [.. Many(Arms.Champion, 90, 45), .. Many(Arms.HeldOut, 40, 36, 1)], new KindTemplates(Both.Champion, null));

        Assert.Contains("demote", report.Monitor!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_champion_level_with_the_held_out_arm_shows_no_sign_of_harm()
    {
        var report = Trials.Evaluate("k", [.. Many(Arms.Champion, 90, 72), .. Many(Arms.HeldOut, 40, 32, 1)], new KindTemplates(Both.Champion, null));

        Assert.StartsWith("no sign of harm", report.Monitor!, StringComparison.Ordinal);
    }

    [Fact]
    public void Cost_is_weighted_tokens_and_missing_when_any_count_is_missing()
    {
        var full = new ExportOutcome(null, null, null, null, null, "t", TokensIn: 100, TokensOut: 10, CacheReadTokens: 1000, CacheWriteTokens: 40);

        Assert.Equal(100 + 50 + 100 + 50, CostWeights.Default.Of(full));
        Assert.Null(CostWeights.Default.Of(full with { CacheReadTokens = null }));
        Assert.Null(CostWeights.Default.Of(null));
    }
}
