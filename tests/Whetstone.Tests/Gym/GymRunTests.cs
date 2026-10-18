using Whetstone.Contracts;
using Whetstone.Gym;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Gym;

/// <summary>ADR 0006: replay pairs in alternating order within a budget, a judge per fresh pair, and a screen that only lets a challenger into a live trial.</summary>
public sealed class GymRunTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Template Challenger = new("explore-steps", "1", "agent/explore", "", "\n\nList the files you read, then answer in five bullets.", TemplateSources.BuiltIn);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;
    private DateTimeOffset _now = T0.AddDays(1);

    public GymRunTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class Runner(Func<ReplayRequest, ReplayResult> answer) : IReplayRunner
    {
        public List<ReplayRequest> Calls { get; } = [];

        public string Client => "fake";

        public Task<ReplayResult> RunAsync(ReplayRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Judge(string winner) : IJudge
    {
        public List<bool> Flips { get; } = [];

        public string Model => "judge-model";

        public Task<(string Winner, double? CostUsd)> JudgeAsync(string prompt, string control, string challenger, bool flipped, CancellationToken ct)
        {
            Flips.Add(flipped);
            return Task.FromResult((winner, (double?)0.005));
        }
    }

    private static ReplayResult Done(ReplayRequest r, bool completed = true, double cost = 0.04, string model = "m") =>
        new(completed, completed ? "completed" : "failed", Model: model, CostUsd: cost, Answer: $"answer for {r.Arm}");

    private async Task SeedCases(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var answer = new EnhanceResponse("p", false, null, null, "", "agent/explore", $"r{i}", false);
            await _stores.ForUser("owner").RecordAsync(
                RequestRow.From(new EnhanceRequest($"find where setting {i} is read", new EnhanceContext("example-app", "0123456789abcdef0123456789abcdef01234567", "agent/explore", "claude-code/agent")), answer, T0.AddMinutes(i)),
                CancellationToken.None);
        }
        await Commands.GymAsync(["select", "--kind", "agent/explore", "--confirm"], _stores.AdminFor("owner"), new StringWriter(), new StringWriter(), CancellationToken.None);
        Assert.Null(await _stores.AdminFor("owner").AddTemplateAsync(Challenger, TemplateRoles.Challenger, CancellationToken.None));
    }

    private async Task<(int Code, string Out, string Err)> Gym(IReplayRunner? runner, IJudge? judge, params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.GymAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None, runner, judge, () => _now = _now.AddSeconds(1));
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_run_replays_each_case_as_a_pair_in_alternating_order_and_judges_it()
    {
        await SeedCases(10);
        var runner = new Runner(r => Done(r));
        var judge = new Judge(GymArms.Challenger);

        var (code, output, _) = await Gym(runner, judge, "run", "--challenger", "explore-steps@1");

        Assert.Equal(Commands.Ok, code);
        Assert.Contains("10 cases of agent/explore: 20 runs, 0 controls reused, 0 skipped, 10 judged", output, StringComparison.Ordinal);
        Assert.Equal([GymArms.Control, GymArms.Challenger, GymArms.Challenger, GymArms.Control], runner.Calls.Take(4).Select(c => c.Arm));
        Assert.All(runner.Calls.Where(c => c.Arm == GymArms.Challenger), c => Assert.EndsWith("five bullets.", c.Prompt, StringComparison.Ordinal));
        Assert.All(runner.Calls.Where(c => c.Arm == GymArms.Control), c => Assert.StartsWith("find where setting", c.Prompt, StringComparison.Ordinal));
        Assert.Equal([false, true], judge.Flips.Take(2));
        Assert.Contains("screen pass", (await Gym(null, null, "report", "--challenger", "explore-steps@1")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_stops_before_the_budget_would_be_passed()
    {
        await SeedCases(5);

        var (_, output, _) = await Gym(new Runner(r => Done(r, cost: 0.05)), null, "run", "--challenger", "explore-steps@1", "--budget-usd", "0.15");

        Assert.Contains("2 runs", output, StringComparison.Ordinal);
        Assert.Contains("stopped at the budget", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_challenger_reuses_recent_controls_and_a_case_is_never_run_twice_for_one_version()
    {
        await SeedCases(3);
        await Gym(new Runner(r => Done(r)), null, "run", "--challenger", "explore-steps@1");
        var next = Challenger with { Version = "2" };
        Assert.Null(await _stores.AdminFor("owner").RetireTemplateAsync(Challenger.Kind, Challenger.Id, "1", CancellationToken.None));
        Assert.Null(await _stores.AdminFor("owner").AddTemplateAsync(next, TemplateRoles.Challenger, CancellationToken.None));
        var runner = new Runner(r => Done(r));

        var again = await Gym(new Runner(r => Done(r)), null, "run", "--challenger", "explore-steps@1");
        var second = await Gym(runner, null, "run", "--challenger", "explore-steps@2");

        Assert.Contains("0 runs, 0 controls reused, 3 skipped", again.Out, StringComparison.Ordinal);
        Assert.Contains("3 runs, 3 controls reused", second.Out, StringComparison.Ordinal);
        Assert.All(runner.Calls, c => Assert.Equal(GymArms.Challenger, c.Arm));
    }

    [Fact]
    public void The_screen_needs_enough_pairs_on_one_model_and_fails_a_challenger_that_completes_less()
    {
        GymRun Run(string caseId, string arm, bool completed, string model = "m", int minute = 0) =>
            new($"{caseId}-{arm}", caseId, arm, arm == GymArms.Challenger ? "t" : null, arm == GymArms.Challenger ? "1" : null, "fake",
                new ReplayResult(completed, null, Model: model, CostUsd: 0.04), T0.AddMinutes(minute));
        var few = Enumerable.Range(0, 7).SelectMany(i => new[] { Run($"c{i}", GymArms.Control, true), Run($"c{i}", GymArms.Challenger, true) }).ToList();
        var worse = Enumerable.Range(0, 10).SelectMany(i => new[] { Run($"c{i}", GymArms.Control, true), Run($"c{i}", GymArms.Challenger, i % 2 == 0) }).ToList();
        var otherModel = Enumerable.Range(0, 10).SelectMany(i => new[] { Run($"c{i}", GymArms.Control, true), Run($"c{i}", GymArms.Challenger, true, model: "n") }).ToList();

        Assert.Equal("not enough pairs", Screen.Of(few, [], "t", "1").Verdict);
        Assert.Equal("screen fail", Screen.Of(worse, [], "t", "1").Verdict);
        Assert.Equal(0, Screen.Of(otherModel, [], "t", "1").Pairs);
    }

    [Fact]
    public async Task Without_a_gym_client_run_refuses_and_gym_rows_never_reach_the_trial_report()
    {
        await SeedCases(3);
        var before = new StringWriter();
        await Commands.ReportAsync([], _stores.AdminFor("owner"), before, new StringWriter(), CancellationToken.None);

        var (code, _, error) = await Gym(null, null, "run", "--challenger", "explore-steps@1");
        await Gym(new Runner(r => Done(r)), new Judge(GymArms.Challenger), "run", "--challenger", "explore-steps@1");
        var after = new StringWriter();
        await Commands.ReportAsync([], _stores.AdminFor("owner"), after, new StringWriter(), CancellationToken.None);

        Assert.NotEqual(Commands.Ok, code);
        Assert.Contains("no gym client is configured", error, StringComparison.Ordinal);
        Assert.Equal(before.ToString(), after.ToString());
    }
}
