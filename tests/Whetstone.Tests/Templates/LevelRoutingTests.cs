using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.10: a champion per price level, and routing evidence per model and effort that only suggests.</summary>
public sealed class LevelRoutingTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public LevelRoutingTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class Echo : IEnhancer
    {
        public Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct) =>
            Task.FromResult(PassThrough.Of(request, PassThrough.NothingLearned) with { RequestId = $"req-{request.Prompt.GetHashCode():x}" });
    }

    [Fact]
    public async Task A_level_with_its_own_champion_is_served_and_stored_under_its_own_kind()
    {
        var templates = _stores.TemplatesFor("owner");
        await templates.SeedAsync([new Template("base", "1", "agent/explore", "", "BASE", TemplateSources.BuiltIn), new Template("cheap", "1", "agent/explore@cheap", "", "CHEAP", TemplateSources.BuiltIn)], TemplateRoles.Champion, CancellationToken.None);
        var enhancer = new TemplateEnhancer(new Echo(), templates);
        EnhanceResponse? cheap = null, best = null;
        for (var i = 0; cheap is null || best is null; i++)
        {
            var c = await enhancer.EnhanceAsync(new EnhanceRequest($"find x {i}", new EnhanceContext(null, null, "agent/explore", "test", Level: "cheap")), CancellationToken.None);
            var b = await enhancer.EnhanceAsync(new EnhanceRequest($"find y {i}", new EnhanceContext(null, null, "agent/explore", "test", Level: "best")), CancellationToken.None);
            cheap ??= c.Arm == Arms.Champion ? c : null;
            best ??= b.Arm == Arms.Champion ? b : null;
        }

        Assert.Equal(("agent/explore@cheap", "cheap"), (cheap.TaskKind, cheap.TemplateId));
        Assert.Equal(("agent/explore", "base"), (best.TaskKind, best.TemplateId));
        var row = RequestRow.From(new EnhanceRequest("find x", new EnhanceContext(null, null, "agent/explore", "test", Level: "cheap")), cheap, T0);
        Assert.Equal("agent/explore@cheap", row.TaskKind);
    }

    private async Task Seed(string model, int runs, int completed, long tokensIn, string kind = "agent/explore", string? effort = null)
    {
        for (var i = 0; i < runs; i++)
        {
            var id = $"{model}-{effort}-{i}";
            var answer = new EnhanceResponse("p", false, null, null, "", kind, id, false);
            await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest("p", new EnhanceContext(null, null, kind, "test")), answer, T0.AddMinutes(i)), CancellationToken.None);
            await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(
                Model: model, Effort: effort, Completed: i < completed, TokensIn: tokensIn, TokensOut: 100, CacheReadTokens: 0, CacheWriteTokens: 0)), T0.AddMinutes(i)), CancellationToken.None);
        }
    }

    private async Task<string> Report(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        Assert.Equal(Commands.Ok, await Commands.ReportAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None));
        return output.ToString();
    }

    [Fact]
    public async Task Routing_names_the_cheapest_model_shown_no_worse_than_the_best()
    {
        await Seed("big", 100, 90, 5000);
        await Seed("small", 100, 89, 1000);
        await Seed("tiny", 100, 50, 200);
        await Seed("rare", 5, 5, 10);

        var text = await Report("--routing");

        Assert.Contains("  big: 100 runs, completed 90% (90/100), median cost 5500; best completion", text, StringComparison.Ordinal);
        Assert.Contains("  small: 100 runs, completed 89% (89/100), median cost 1500; cheapest shown no worse than the best: a routing candidate", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tiny: 100 runs, completed 50% (50/100), median cost 700; cheapest", text, StringComparison.Ordinal);
        Assert.Contains("  rare: 5 runs", text, StringComparison.Ordinal);
        Assert.Contains("Suggestions only", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Effort_is_compared_the_same_way_and_the_two_reports_are_one_at_a_time()
    {
        await Seed("m", 100, 90, 3000, effort: "high");
        await Seed("m", 100, 89, 1000, effort: "low");

        Assert.Contains("  low: 100 runs, completed 89% (89/100), median cost 1500; cheapest shown no worse", await Report("--effort"), StringComparison.Ordinal);
        var error = new StringWriter();
        Assert.Equal(Commands.Usage, await Commands.ReportAsync(["--routing", "--effort"], _stores.AdminFor("owner"), new StringWriter(), error, CancellationToken.None));
    }
}
