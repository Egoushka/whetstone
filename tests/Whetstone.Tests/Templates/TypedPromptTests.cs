using Whetstone.Contracts;
using Whetstone.Kinds;
using Whetstone.Templates;

namespace Whetstone.Tests.Templates;

/// <summary>ADR 0005: prompts a person types in Claude Code get a kind from their opening words and run template trials.</summary>
public class TypedPromptTests
{
    [Theory]
    [InlineData("Implement retry with backoff in the upload client", "typed/implement")]
    [InlineData("Can you add a test for the empty case in the parser?", "typed/implement")]
    [InlineData("Fix the failing build on the main branch please", "typed/debug")]
    [InlineData("Debug the nightly job that fails on the second step", "typed/debug")]
    [InlineData("Review the changes on this branch before I merge", "typed/review")]
    [InlineData("Research how the cache expires entries in this library", "typed/research")]
    [InlineData("Continue with the next step of the migration plan we made", "typed/continue")]
    [InlineData("Merge the pull request once the checks are green", "typed/ship")]
    [InlineData("The weather today is fine and the report is attached", "other")]
    [InlineData("yes, go ahead", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void A_typed_prompt_gets_a_kind_from_its_opening_words(string? prompt, string kind) =>
        Assert.Equal(kind, PromptKinds.ForTyped(prompt));

    [Fact]
    public void Every_typed_kind_with_a_champion_also_has_the_model_rewrite_as_challenger()
    {
        var champions = BuiltInTemplates.All.Where(t => t.Kind.StartsWith("typed/", StringComparison.Ordinal)).Select(t => t.Kind).Order().ToList();

        Assert.Equal(BuiltInTemplates.TypedKinds.Order(), champions);
        Assert.Equal(champions, BuiltInTemplates.TypedChallengers.Select(t => t.Kind).Order());
        Assert.All(BuiltInTemplates.TypedChallengers, t => Assert.Equal((TemplateSources.Model, "", ""), (t.Source, t.Before, t.After)));
        Assert.All(BuiltInTemplates.All.Where(t => t.Kind.StartsWith("typed/", StringComparison.Ordinal)), t =>
        {
            Assert.Empty(t.UnknownSlots());
            Assert.Equal("original", t.Render("original", null)![..8]);
        });
    }

    private sealed class Found(KindTemplates found) : ITemplates
    {
        public string? AskedFor;

        public Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct)
        {
            AskedFor = kind;
            return Task.FromResult(found);
        }

        public Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly Template Champion = BuiltInTemplates.All.Single(t => t.Kind == "typed/implement");
    private static readonly Template Rewrite = BuiltInTemplates.TypedChallengers.Single(t => t.Kind == "typed/implement");

    private static Task<EnhanceResponse> Typed(Found templates, string prompt, string? kind = null) =>
        new TemplateEnhancer(new PassThrough(), templates).EnhanceAsync(
            new EnhanceRequest(prompt, new EnhanceContext(Repository: "example/app", TaskKind: kind, Client: PromptKinds.TypedClient)), CancellationToken.None);

    [Fact]
    public async Task A_typed_prompt_with_no_kind_sent_is_classified_by_the_server_and_served_by_arm()
    {
        var templates = new Found(new KindTemplates(Champion, Rewrite));
        var answers = new List<EnhanceResponse>();
        for (var i = 0; i < 200; i++)
            answers.Add(await Typed(templates, $"Implement feature number {i} in the example service"));

        Assert.Equal("typed/implement", templates.AskedFor);
        var champion = answers.First(a => a.Arm == Arms.Champion);
        Assert.True(champion.Changed);
        Assert.Equal("typed/implement", champion.TaskKind);
        Assert.Contains("repository's own build and tests", champion.Prompt, StringComparison.Ordinal);
        Assert.StartsWith("Implement feature number", champion.Prompt, StringComparison.Ordinal);
        var rewrite = answers.First(a => a.Arm == Arms.Challenger);
        Assert.Equal((false, BuiltInTemplates.RewriteId, BuiltInTemplates.RewriteVersion), (rewrite.Changed, rewrite.TemplateId, rewrite.TemplateVersion));
        Assert.Contains(answers, a => a.HeldOut && !a.Changed);
    }

    [Fact]
    public async Task A_short_typed_prompt_is_not_templated()
    {
        var templates = new Found(new KindTemplates(Champion, null));

        var answer = await Typed(templates, "yes, go ahead");

        Assert.Null(templates.AskedFor);
        Assert.Equal((false, false, null), (answer.Changed, answer.HeldOut, answer.Arm));
    }

    [Fact]
    public async Task A_client_that_is_not_the_typed_one_gets_no_kind_from_its_words()
    {
        var templates = new Found(new KindTemplates(Champion, null));

        await new TemplateEnhancer(new PassThrough(), templates).EnhanceAsync(
            new EnhanceRequest("Implement feature number 3 in the example service", new EnhanceContext(Client: "someone-else")), CancellationToken.None);

        Assert.Null(templates.AskedFor);
    }
}
