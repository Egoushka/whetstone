using Whetstone.Contracts;
using Whetstone.Gym;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Gym;

/// <summary>Spec decisions 3 to 5: unit cases pinned per template and replayed, the drift canary on a new model, gym clients compared.</summary>
public sealed class UnitCaseTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Template Champion = new("explore-steps", "1", "agent/explore", "", "\n\nList the files you read, then answer in five bullets.", TemplateSources.BuiltIn);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;
    private DateTimeOffset _now = T0.AddDays(1);

    public UnitCaseTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class Runner(string client, Func<ReplayRequest, ReplayResult> answer) : IReplayRunner
    {
        public string Client => client;

        public Task<ReplayResult> RunAsync(ReplayRequest request, CancellationToken ct) => Task.FromResult(answer(request));
    }

    private static Runner Ok(string client = "fake", string model = "m", bool completed = true) =>
        new(client, r => new ReplayResult(completed, completed ? "completed" : "failed", Model: model, CostUsd: 0.04, DurationMs: 20_000, Answer: "a"));

    private async Task<(int Code, string Out)> Gym(IReplayRunner? runner, params string[] options)
    {
        var output = new StringWriter();
        var code = await Commands.GymAsync(options, _stores.AdminFor("owner"), output, new StringWriter(), CancellationToken.None, runner, null, () => _now = _now.AddSeconds(1));
        return (code, output.ToString());
    }

    private async Task Seed(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var answer = new EnhanceResponse("p", false, null, null, "", "agent/explore", $"r{i}", false);
            await _stores.ForUser("owner").RecordAsync(
                RequestRow.From(new EnhanceRequest($"find where setting {i} is read", new EnhanceContext("example-app", "0123456789abcdef0123456789abcdef01234567", "agent/explore", "claude-code/agent")), answer, T0.AddMinutes(i)),
                CancellationToken.None);
        }
        await Gym(null, "select", "--kind", "agent/explore", "--confirm");
        Assert.Null(await _stores.AdminFor("owner").AddTemplateAsync(Champion, TemplateRoles.Champion, null, CancellationToken.None));
        await Gym(Ok(), "run", "--challenger", "explore-steps@1");
    }

    [Fact]
    public async Task Pin_marks_five_cases_where_the_template_and_the_control_both_completed()
    {
        await Seed(7);

        Assert.Contains("Pinned 5 of 5", (await Gym(null, "pin", "--template", "explore-steps@1")).Out, StringComparison.Ordinal);
        Assert.Equal(5, (await _stores.AdminFor("owner").GymCasesAsync(CancellationToken.None)).Count(c => c.Pinned));
    }

    [Fact]
    public async Task Test_passes_while_the_template_still_completes_and_fails_when_it_stops()
    {
        await Seed(5);
        await Gym(null, "pin", "--template", "explore-steps@1");

        var pass = await Gym(Ok(), "test", "--template", "explore-steps@1");
        var fail = await Gym(Ok(completed: false), "test", "--template", "explore-steps@1");

        Assert.Equal((Commands.Ok, true), (pass.Code, pass.Out.Contains("on 5 unit cases: pass", StringComparison.Ordinal)));
        Assert.Equal(Commands.Failed, fail.Code);
        Assert.Contains("completed 0 of 5, before 5", fail.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_canary_names_a_champion_whose_kind_now_runs_on_a_model_its_unit_cases_never_saw()
    {
        await Seed(5);
        await Gym(null, "pin", "--template", "explore-steps@1");
        async Task Live(string id, string model, int minute)
        {
            var answer = new EnhanceResponse("p", false, null, null, "", "agent/explore", id, false);
            await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest("explore", new EnhanceContext(null, null, "agent/explore", "claude-code/agent")), answer, T0.AddHours(minute)), CancellationToken.None);
            await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(Completed: true, Model: model)), T0.AddHours(minute)), CancellationToken.None);
        }

        await Live("live-1", "m", 1);
        Assert.Contains("No champion runs live on a model", (await Gym(null, "canary")).Out, StringComparison.Ordinal);
        await Live("live-2", "n", 2);
        Assert.Contains("agent/explore: live runs use n, its unit cases ran on m; run whetstone gym test --template explore-steps@1", (await Gym(null, "canary")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harnesses_compares_gym_clients_on_control_runs()
    {
        await Seed(3);
        var next = Champion with { Version = "2" };
        Assert.Null(await _stores.AdminFor("owner").AddTemplateAsync(next, TemplateRoles.Challenger, null, CancellationToken.None));
        _now = _now.AddDays(8);
        await Gym(Ok(client: "other-harness"), "run", "--challenger", "explore-steps@2");

        var output = (await Gym(null, "harnesses")).Out;

        Assert.Contains("agent/explore  fake: 3 runs, completed 100% (3/3), median cost $0.040, median 20 s", output, StringComparison.Ordinal);
        Assert.Contains("agent/explore  other-harness: 3 runs", output, StringComparison.Ordinal);
    }
}
