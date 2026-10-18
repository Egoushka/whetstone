using Whetstone.Contracts;
using Whetstone.Templates;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.7, decision 4: the filler stripper deletes listed phrases and nothing else.</summary>
public class FillerStripperTests
{
    [Theory]
    [InlineData("Add a retry to the upload client. Be thorough.", "Add a retry to the upload client.")]
    [InlineData("Be thorough. Add a retry to the upload client.", "Add a retry to the upload client.")]
    [InlineData("Add a retry. Double-check everything. Then run the tests.", "Add a retry. Then run the tests.")]
    [InlineData("Fix the parser and be careful.", "Fix the parser.")]
    [InlineData("Fix the parser, and please take your time!", "Fix the parser!")]
    [InlineData("Fix the parser, do your best, then add a test.", "Fix the parser, then add a test.")]
    [InlineData("Summarise the diff.\nTake your time.\nKeep it short.", "Summarise the diff.\nKeep it short.")]
    public void Listed_phrases_go_and_the_rest_stays_as_it_was(string prompt, string expected)
    {
        var (stripped, removed) = FillerStripper.Strip(prompt);

        Assert.Equal(expected, stripped);
        Assert.NotEmpty(removed);
    }

    [Theory]
    [InlineData("Fix the parser and be thorough about the edge cases.")]
    [InlineData("Explain why `be careful` is a bad variable name.")]
    [InlineData("Add a test:\n```\n// be thorough\nassert(x)\n```")]
    [InlineData("Be thorough.")]
    [InlineData("Add a retry to the upload client.")]
    [InlineData("")]
    public void Anything_else_is_left_untouched(string prompt)
    {
        var (stripped, removed) = FillerStripper.Strip(prompt);

        Assert.Equal(prompt, stripped);
        Assert.Empty(removed);
    }

    [Fact]
    public void Matching_ignores_case_and_names_each_phrase_once_per_use()
    {
        var (_, removed) = FillerStripper.Strip("BE THOROUGH. Fix it. Think hard.");

        Assert.Equal(["be thorough", "think hard"], removed);
    }

    [Fact]
    public async Task A_filler_strip_template_serves_the_stripped_prompt_names_what_went_and_frames_it_like_any_template()
    {
        var champion = new Template("base", "1", "typed/implement", "", "Report in five lines.", TemplateSources.BuiltIn);
        var strip = new Template("strip", "1", "typed/implement", "", "Report in five lines.", TemplateSources.FillerStrip);
        var enhancer = new TemplateEnhancer(new Echo(), new Fake(new KindTemplates(champion, strip)));
        var served = new List<EnhanceResponse>();
        for (var i = 0; i < 200; i++)
            served.Add(await enhancer.EnhanceAsync(new EnhanceRequest("Fix the parser. Be thorough.", new EnhanceContext("r", "c", "typed/implement", "claude-code")), CancellationToken.None));

        var challenger = served.First(a => a.Arm == Arms.Challenger);
        var control = served.First(a => a.Arm == Arms.Champion);
        Assert.Equal("Fix the parser.\n\nReport in five lines.", challenger.Prompt);
        Assert.Equal((true, "strip"), (challenger.Changed, challenger.TemplateId));
        Assert.Contains("removed: be thorough", challenger.Reason, StringComparison.Ordinal);
        Assert.Equal("Fix the parser. Be thorough.\n\nReport in five lines.", control.Prompt);
    }

    [Fact]
    public async Task A_filler_strip_template_with_nothing_to_strip_serves_the_prompt_as_a_plain_template_would()
    {
        var champion = new Template("base", "1", "typed/implement", "", "Report in five lines.", TemplateSources.BuiltIn);
        var strip = new Template("strip", "1", "typed/implement", "", "", TemplateSources.FillerStrip);
        var enhancer = new TemplateEnhancer(new Echo(), new Fake(new KindTemplates(champion, strip)));
        EnhanceResponse? challenger = null;
        for (var i = 0; challenger is null && i < 200; i++)
            if (await enhancer.EnhanceAsync(new EnhanceRequest("Fix the parser.", new EnhanceContext("r", "c", "typed/implement", "claude-code")), CancellationToken.None) is { Arm: Arms.Challenger } found)
                challenger = found;

        Assert.Equal("Fix the parser.", challenger!.Prompt);
        Assert.DoesNotContain("removed", challenger.Reason, StringComparison.Ordinal);
    }

    private sealed class Fake(KindTemplates found) : ITemplates
    {
        public Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct) => Task.FromResult(found);

        public Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Echo : IEnhancer
    {
        public Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct) =>
            Task.FromResult(PassThrough.Of(request, PassThrough.NothingLearned) with { RequestId = request.Prompt + Guid.NewGuid() });
    }
}
