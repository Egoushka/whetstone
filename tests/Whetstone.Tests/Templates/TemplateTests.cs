using Whetstone.Contracts;
using Whetstone.Kinds;
using Whetstone.Templates;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.5: arms from a hash, templates around an untouched prompt, and the enhancer that serves them.</summary>
public class TemplateTests
{
    private static readonly EnhanceContext Context = new("example-app", "abc1234", "agent/explore", "claude-code/agent");

    [Fact]
    public void An_arm_is_the_same_every_time_for_the_same_kind_and_id()
    {
        Assert.Equal(Arms.Assign("agent/explore", "req-1", true), Arms.Assign("agent/explore", "req-1", true));
        Assert.Equal(Arms.Bucket("agent/explore", "req-1"), Arms.Bucket("agent/explore", "req-1"));
    }

    [Fact]
    public void The_split_is_60_30_10_with_a_challenger_and_90_10_without()
    {
        var withChallenger = Enumerable.Range(0, 20_000).Select(i => Arms.Assign("agent/explore", $"req-{i}", true)).GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        var without = Enumerable.Range(0, 20_000).Select(i => Arms.Assign("agent/explore", $"req-{i}", false)).GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());

        Assert.InRange(withChallenger[Arms.Champion], 11_600, 12_400);
        Assert.InRange(withChallenger[Arms.Challenger], 5_600, 6_400);
        Assert.InRange(withChallenger[Arms.HeldOut], 1_800, 2_200);
        Assert.InRange(without[Arms.Champion], 17_600, 18_400);
        Assert.False(without.ContainsKey(Arms.Challenger));
        Assert.InRange(without[Arms.HeldOut], 1_800, 2_200);
    }

    [Fact]
    public void The_kind_is_part_of_the_hash_so_two_kinds_do_not_share_their_arms()
    {
        var differ = Enumerable.Range(0, 200).Count(i => Arms.Bucket("agent/explore", $"req-{i}") != Arms.Bucket("agent/plan", $"req-{i}"));

        Assert.True(differ > 150);
        Assert.All(Enumerable.Range(0, 500), i => Assert.InRange(Arms.Bucket("k", $"r{i}"), 0, 99));
    }

    [Fact]
    public void A_template_puts_text_around_the_prompt_and_leaves_the_prompt_byte_for_byte()
    {
        var template = new Template("t", "1", "agent/explore", "Before.", "After.", TemplateSources.BuiltIn);
        var prompt = "  list the files\n\twith {{repository}} kept as typed  \n";

        var result = template.Render(prompt, Context);

        Assert.Equal("Before.\n\n" + prompt + "\n\nAfter.", result);
    }

    [Fact]
    public void Slots_are_filled_from_the_context_and_a_line_whose_slot_has_no_value_is_dropped()
    {
        var template = new Template("t", "1", "k", "", "Repository: {{repository}}\nCommit: {{commit}}\nKind: {{task_kind}}", TemplateSources.BuiltIn);

        Assert.Equal("p\n\nRepository: example-app\nCommit: abc1234\nKind: agent/explore", template.Render("p", Context));
        Assert.Equal("p\n\nRepository: example-app", template.Render("p", new EnhanceContext(Repository: "example-app")));
        Assert.Equal("p", template.Render("p", null));
    }

    [Fact]
    public void A_template_with_a_slot_whetstone_does_not_fill_is_not_served()
    {
        var template = new Template("t", "1", "k", "", "See {{secret}}", TemplateSources.BuiltIn);

        Assert.Equal(["secret"], template.UnknownSlots());
        Assert.Null(template.Render("p", Context));
    }

    [Fact]
    public void The_shipped_templates_cover_the_kinds_the_importer_assigns_and_state_nothing_of_their_own()
    {
        var kinds = BuiltInTemplates.All.Select(t => t.Kind).ToList();

        Assert.Equal(kinds.Count, kinds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(PromptKinds.ForAgent("Explore", null), kinds);
        Assert.Contains(PromptKinds.ForAgent(null, "Review the diff"), kinds);
        Assert.Contains(PromptKinds.ForAgent(null, "Something unusual"), kinds);
        Assert.DoesNotContain(PromptKinds.Fetch, kinds);
        Assert.All(BuiltInTemplates.All, t => Assert.Empty(t.UnknownSlots()));
        var readOnly = BuiltInTemplates.All.Single(t => t.Kind == "agent/explore").Render("find x", Context)!;
        Assert.StartsWith("find x\n\n", readOnly, StringComparison.Ordinal);
        Assert.Contains("under 300 words", readOnly, StringComparison.Ordinal);
        Assert.Contains("read-only", readOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("read-only", BuiltInTemplates.All.Single(t => t.Kind == "agent/general-purpose/implement").Render("find x", Context)!, StringComparison.Ordinal);
    }

    private sealed class FakeTemplates(KindTemplates found) : ITemplates
    {
        public Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct) => Task.FromResult(found);

        public Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class BrokenTemplates : ITemplates
    {
        public Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Echo : IEnhancer
    {
        public Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct) => Task.FromResult(PassThrough.Of(request, PassThrough.NothingLearned) with { RequestId = request.Prompt });
    }

    private static readonly Template Champion = new("champ", "1", "agent/explore", "", "CHAMPION TEXT", TemplateSources.BuiltIn);
    private static readonly Template Challenger = new("chall", "2", "agent/explore", "", "CHALLENGER TEXT", TemplateSources.BuiltIn);

    private static Task<EnhanceResponse> Enhance(ITemplates templates, string id, EnhanceContext? context = null) =>
        new TemplateEnhancer(new Echo(), templates).EnhanceAsync(new EnhanceRequest(id, context ?? Context), CancellationToken.None);

    [Fact]
    public async Task Each_arm_gets_what_it_should_and_the_prompt_is_inside_every_served_answer()
    {
        var templates = new FakeTemplates(new KindTemplates(Champion, Challenger));
        var answers = new List<EnhanceResponse>();
        for (var i = 0; i < 300; i++)
            answers.Add(await Enhance(templates, $"id-{i}"));

        var champion = answers.First(a => a.Arm == Arms.Champion);
        var challenger = answers.First(a => a.Arm == Arms.Challenger);
        var held = answers.First(a => a.Arm == Arms.HeldOut);
        Assert.Equal((true, "champ", "1", false, "agent/explore"), (champion.Changed, champion.TemplateId, champion.TemplateVersion, champion.HeldOut, champion.TaskKind));
        Assert.EndsWith("CHAMPION TEXT", champion.Prompt, StringComparison.Ordinal);
        Assert.Equal(("chall", "2"), (challenger.TemplateId, challenger.TemplateVersion));
        Assert.EndsWith("CHALLENGER TEXT", challenger.Prompt, StringComparison.Ordinal);
        Assert.Equal((false, true, null), (held.Changed, held.HeldOut, held.TemplateId));
        Assert.Equal(held.RequestId, held.Prompt);
        Assert.All(answers.Where(a => a.Changed), a => Assert.StartsWith(a.RequestId, a.Prompt, StringComparison.Ordinal));
        Assert.Contains(Arms.Champion, answers.Select(a => a.Arm));
    }

    [Fact]
    public async Task A_kind_with_no_champion_is_held_out_entirely()
    {
        var answers = new List<EnhanceResponse>();
        for (var i = 0; i < 50; i++)
            answers.Add(await Enhance(new FakeTemplates(KindTemplates.None), $"id-{i}"));

        Assert.All(answers, a => Assert.Equal((false, true, Arms.HeldOut), (a.Changed, a.HeldOut, a.Arm)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    [InlineData("")]
    public async Task A_request_with_no_kind_or_kind_other_is_not_templated_and_not_an_arm(string? kind)
    {
        var answer = await Enhance(new FakeTemplates(new KindTemplates(Champion, null)), "id-1", new EnhanceContext(TaskKind: kind));

        Assert.Equal((false, false, null), (answer.Changed, answer.HeldOut, answer.Arm));
    }

    [Fact]
    public async Task A_failing_template_store_returns_the_inner_answer_and_is_counted()
    {
        var enhancer = new TemplateEnhancer(new Echo(), new BrokenTemplates());

        var answer = await enhancer.EnhanceAsync(new EnhanceRequest("p", Context), CancellationToken.None);

        Assert.Equal(("p", false, null), (answer.Prompt, answer.Changed, answer.Arm));
        Assert.Equal(1, enhancer.Failures);
    }

    [Fact]
    public async Task A_template_that_cannot_be_rendered_leaves_the_prompt_unchanged_but_keeps_the_arm()
    {
        var bad = new Template("bad", "1", "agent/explore", "", "{{nope}}", TemplateSources.BuiltIn);
        var arms = new List<EnhanceResponse>();
        for (var i = 0; i < 40; i++)
            arms.Add(await Enhance(new FakeTemplates(new KindTemplates(bad, null)), $"id-{i}"));

        Assert.All(arms.Where(a => a.Arm == Arms.Champion), a => Assert.Equal((false, null), (a.Changed, a.TemplateId)));
    }
}
