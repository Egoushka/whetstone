using Whetstone.Agents;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.12: parents scored by their children, output kinds by their reader, kinds proposed from words, graduation proposals.</summary>
public sealed class AgentStructureTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public AgentStructureTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private async Task Seed(string id, string prompt, string? kind, string? parent = null, FeedbackOutcome? outcome = null, int minute = 0)
    {
        var answer = new EnhanceResponse(prompt, false, null, null, "", kind, id, false);
        await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest(prompt, new EnhanceContext(null, null, kind, "test", ParentRequestId: parent)), answer, T0.AddMinutes(minute)), CancellationToken.None);
        if (outcome is not null)
            await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, outcome), T0), CancellationToken.None);
    }

    private static async Task<string> Run(Func<StringWriter, Task<int>> command)
    {
        var output = new StringWriter();
        Assert.Equal(Commands.Ok, await command(output));
        return output.ToString();
    }

    [Fact]
    public async Task An_orchestrator_kind_is_scored_by_its_children_and_an_output_kind_by_its_reader()
    {
        await Seed("p1", "plan the release", "orchestrator/turn");
        await Seed("c1", "explore the changelog", "agent/explore", "p1", new FeedbackOutcome(Completed: true, AskedAgain: false, TokensIn: 100, TokensOut: 10, CacheReadTokens: 0, CacheWriteTokens: 0));
        await Seed("c2", "explore the tags", "agent/explore", "p1", new FeedbackOutcome(Completed: false, TokensIn: 100, TokensOut: 10, CacheReadTokens: 0, CacheWriteTokens: 0));
        await Seed("o1", "write the handoff", "output/handoff", outcome: new FeedbackOutcome(ReadMs: 40_000, EditChars: 120));

        var text = await Run(o => Commands.ReportAsync(["--agents"], _stores.AdminFor("owner"), o, new StringWriter(), CancellationToken.None));

        Assert.Contains("orchestrator/turn: 1 prompts started 2 runs; 50% (1/2) of the measured ones completed without rework; median cost of one prompt's children 300", text, StringComparison.Ordinal);
        Assert.Contains("output/handoff  no template: 1 runs, median read 40 s, median edits 120 characters", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kinds_are_proposed_from_a_distinctive_word_shared_by_five_prompts()
    {
        for (var i = 0; i < 6; i++)
            await Seed($"g{i}", $"Telegram export number {i}: list the chats that changed", "agent/general-purpose", minute: i);
        for (var i = 0; i < 3; i++)
            await Seed($"x{i}", $"something unrelated {i}", "other", minute: 10 + i);
        await Seed("named", "Telegram export already has a kind", "agent/explore", minute: 20);

        var text = await Run(o => Commands.KindsAsync(["suggest"], _stores.AdminFor("owner"), o, new StringWriter(), CancellationToken.None));

        Assert.Contains("6 prompts", text, StringComparison.Ordinal);
        Assert.DoesNotContain("7 prompts", text, StringComparison.Ordinal);
        Assert.Contains("Proposals only", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Graduate_proposes_promoted_champions_with_their_evidence_and_says_when_there_is_none()
    {
        var admin = _stores.AdminFor("owner");
        await Seed("r", "p", "agent/explore");
        Assert.Contains("None yet: no champion has been promoted", await Run(o => Commands.GraduateAsync([], admin, o, new StringWriter(), CancellationToken.None)), StringComparison.Ordinal);
        Assert.Null(await admin.AddTemplateAsync(new Template("a", "1", "agent/explore", "", "Report in five bullets.", "hand"), TemplateRoles.Champion, null, CancellationToken.None));
        Assert.Null(await admin.AddTemplateAsync(new Template("b", "1", "agent/explore", "", "Name every file you read.", "hand"), TemplateRoles.Challenger, null, CancellationToken.None));
        Assert.Null(await admin.PromoteTemplateAsync("agent/explore", "b", "1", new DecisionRecord("{\"challenger\":{\"runs\":100}}", "won at the look of 100"), CancellationToken.None));

        var text = await Run(o => Commands.GraduateAsync([], admin, o, new StringWriter(), CancellationToken.None));

        Assert.Contains("### agent/explore: b v1", text, StringComparison.Ordinal);
        Assert.Contains("Evidence: {\"challenger\":{\"runs\":100}}", text, StringComparison.Ordinal);
        Assert.Contains("> Name every file you read.", text, StringComparison.Ordinal);
    }
}
