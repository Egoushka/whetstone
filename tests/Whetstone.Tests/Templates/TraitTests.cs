using System.Text.Json;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.7, decision 2: traits are drawn on or off per request and their effect is read across kinds.</summary>
public sealed class TraitTests : IDisposable
{
    private static readonly Template Champion = new("base", "1", "typed/implement", "", "Base text.", TemplateSources.BuiltIn);
    private static readonly Trait Name = new("name-the-file", "typed/implement", "Name the file you will change first.");
    private static readonly Trait Stop = new("stop-rule", "typed/implement", "Stop when the tests pass.");

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public TraitTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    [Fact]
    public void A_draw_repeats_for_the_same_request_and_is_about_half_on_and_independent_between_traits()
    {
        var ids = Enumerable.Range(0, 4_000).Select(i => $"req-{i}").ToList();

        Assert.All(ids.Take(50), id => Assert.Equal(Name.On(id), Name.On(id)));
        var on = ids.Count(Name.On);
        var both = ids.Count(id => Name.On(id) && Stop.On(id));
        Assert.InRange(on, 1_850, 2_150);
        Assert.InRange(both, 850, 1_150);
    }

    private sealed class Fake(KindTemplates found) : ITemplates
    {
        public Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct) => Task.FromResult(found);

        public Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Echo : IEnhancer
    {
        public Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct) =>
            Task.FromResult(PassThrough.Of(request, PassThrough.NothingLearned) with { RequestId = request.Prompt });
    }

    [Fact]
    public async Task Traits_that_draw_on_are_added_after_the_template_and_the_draw_is_kept_with_the_answer()
    {
        var enhancer = new TemplateEnhancer(new Echo(), new Fake(new KindTemplates(Champion, (Template?)null) { Traits = [Name, Stop] }));
        var answers = new List<EnhanceResponse>();
        for (var i = 0; i < 400; i++)
            answers.Add(await enhancer.EnhanceAsync(new EnhanceRequest($"id-{i}", new EnhanceContext("r", "c", "typed/implement", "claude-code")), CancellationToken.None));

        var held = answers.Where(a => a.Arm == Arms.HeldOut).ToList();
        var served = answers.Where(a => a.Arm == Arms.Champion).ToList();
        Assert.All(held, a => Assert.Null(a.Traits));
        Assert.All(served, a =>
        {
            Assert.Equal(["name-the-file", "stop-rule"], a.Traits!.Keys.Order());
            Assert.Equal(a.Traits["name-the-file"], a.Prompt.Contains(Name.Text, StringComparison.Ordinal));
            Assert.Equal(a.Traits["stop-rule"], a.Prompt.Contains(Stop.Text, StringComparison.Ordinal));
            Assert.StartsWith(a.RequestId, a.Prompt, StringComparison.Ordinal);
        });
        Assert.Contains(served, a => a.Traits!.Values.All(v => v));
        Assert.Contains(served, a => a.Traits!.Values.All(v => !v));
    }

    [Fact]
    public async Task A_kind_with_no_traits_is_served_exactly_as_before()
    {
        var enhancer = new TemplateEnhancer(new Echo(), new Fake(new KindTemplates(Champion, (Template?)null)));
        var answer = await enhancer.EnhanceAsync(new EnhanceRequest("id-1", new EnhanceContext("r", "c", "typed/implement", "claude-code")), CancellationToken.None);

        Assert.Null(answer.Traits);
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.TraitAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Traits_are_added_listed_read_back_with_the_kind_and_retired()
    {
        await _stores.TemplatesFor("owner").SeedAsync([Champion], TemplateRoles.Champion, CancellationToken.None);

        var add = await Run("add", "--kind", "typed/implement", "--id", "name-the-file", "--text", Name.Text);
        var again = await Run("add", "--kind", "typed/implement", "--id", "name-the-file", "--text", "Other.");
        var found = await _stores.TemplatesFor("owner").ForKindAsync("typed/implement", CancellationToken.None);
        var list = await Run("list");
        var retire = await Run("retire", "--kind", "typed/implement", "--id", "name-the-file");
        var after = await _stores.TemplatesFor("owner").ForKindAsync("typed/implement", CancellationToken.None);

        Assert.Equal((Commands.Ok, ""), (add.Code, add.Err));
        Assert.Equal(Commands.Failed, again.Code);
        Assert.Equal([Name], found.Traits);
        Assert.Contains("name-the-file  active", list.Out, StringComparison.Ordinal);
        Assert.Equal(Commands.Ok, retire.Code);
        Assert.Empty(after.Traits);
        Assert.Contains("retired", (await Run("list")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_takes_four_traits_and_bad_input_is_a_usage_error()
    {
        await _stores.TemplatesFor("owner").SeedAsync([new Template("base", "1", "k", "", "Base.", "hand")], TemplateRoles.Champion, CancellationToken.None);
        for (var i = 1; i <= Trait.MaxPerKind; i++)
            Assert.Equal(Commands.Ok, (await Run("add", "--kind", "k", "--id", $"t{i}", "--text", $"Text {i}.")).Code);

        var fifth = await Run("add", "--kind", "k", "--id", "t5", "--text", "Text 5.");

        Assert.Equal(Commands.Failed, fifth.Code);
        Assert.Equal(Commands.Usage, (await Run("add", "--kind", "k", "--id", "bad id", "--text", "x")).Code);
        Assert.Equal(Commands.Usage, (await Run("add", "--kind", "other", "--id", "x", "--text", "x")).Code);
        Assert.Equal(Commands.Usage, (await Run("add", "--kind", "k", "--id", "x", "--text", "see {{secret}}")).Code);
        Assert.Equal(Commands.Usage, (await Run("frobnicate")).Code);
        Assert.Equal(Commands.Failed, (await Run("retire", "--kind", "k", "--id", "nope")).Code);
    }

    [Fact]
    public async Task The_draw_is_stored_with_the_request_and_exported()
    {
        var response = new EnhanceResponse("p", true, "base", "1", "r", "typed/implement", "req-1", false) { Arm = Arms.Champion, Traits = new Dictionary<string, bool> { ["name-the-file"] = true, ["stop-rule"] = false } };
        await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest("add a retry", new EnhanceContext(null, null, "typed/implement", "claude-code")), response, DateTimeOffset.UtcNow), CancellationToken.None);
        var (output, error) = (new StringWriter(), new StringWriter());

        await Commands.ExportAsync([], _stores.AdminFor("owner"), output, error, CancellationToken.None);

        var line = output.ToString().Trim();
        Assert.Empty(ContractSchemas.ValidateExport(JsonDocument.Parse(line).RootElement));
        var record = JsonSerializer.Deserialize<ExportRecord>(line, ContractJson.Options)!;
        Assert.Equal(new Dictionary<string, bool> { ["name-the-file"] = true, ["stop-rule"] = false }, record.Answer.Traits);
    }

    private static TrialRun Run(int minute, bool on, bool done, string kind = "k") =>
        new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute), Arms.Champion, done, null, null, null, "m", "base", "1",
            Traits: new Dictionary<string, bool> { ["stop-rule"] = on });

    [Fact]
    public void A_trait_is_judged_across_kinds_with_the_counts_by_kind_and_a_verdict_only_once_both_sides_have_enough_runs()
    {
        var kindA = Enumerable.Range(0, 80).Select(i => Run(i, i % 2 == 0, i % 2 == 0 ? i % 10 != 0 : i % 4 != 1)).ToList();
        var kindB = Enumerable.Range(0, 80).Select(i => Run(i, i % 2 == 0, i % 2 == 0 ? i % 10 != 0 : i % 4 != 1)).ToList();
        var thin = Enumerable.Range(0, 10).Select(i => Run(i, i % 2 == 0, true)).ToList();

        var strong = Assert.Single(Trials.TraitEffects(new Dictionary<string, IReadOnlyList<TrialRun>> { ["a"] = kindA, ["b"] = kindB }));
        var weak = Assert.Single(Trials.TraitEffects(new Dictionary<string, IReadOnlyList<TrialRun>> { ["a"] = thin }));

        Assert.Equal(("stop-rule", 80, 80), (strong.Trait, strong.OnRuns, strong.OffRuns));
        Assert.Equal([new TraitKindCount("a", 40, 40), new TraitKindCount("b", 40, 40)], strong.Kinds);
        Assert.True(strong.Z > Trials.Z);
        Assert.StartsWith("candidate", strong.Verdict, StringComparison.Ordinal);
        Assert.StartsWith("keep running", weak.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void Rework_counts_as_not_completed_and_runs_without_a_draw_or_a_completion_take_no_part()
    {
        var reworked = Enumerable.Range(0, 70).Select(i => Run(i, i % 2 == 0, true) with { Reason = i % 2 == 0 ? "incomplete" : null }).ToList();
        var unmeasured = new TrialRun(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), Arms.Champion, null, null, null, null, "m", Traits: new Dictionary<string, bool> { ["stop-rule"] = true });
        var undrawn = new TrialRun(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), Arms.Champion, true, null, null, null, "m");

        var report = Assert.Single(Trials.TraitEffects(new Dictionary<string, IReadOnlyList<TrialRun>> { ["a"] = [.. reworked, unmeasured, undrawn] }));

        Assert.Equal((35, 0, 35, 35), (report.OnRuns, report.OnWins, report.OffRuns, report.OffWins));
        Assert.StartsWith("drop", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_prints_the_traits_section()
    {
        var response = new EnhanceResponse("p", true, "base", "1", "r", "typed/implement", "req-1", false) { Arm = Arms.Champion, Traits = new Dictionary<string, bool> { ["stop-rule"] = true } };
        await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest("add a retry", new EnhanceContext(null, null, "typed/implement", "claude-code")), response, DateTimeOffset.UtcNow), CancellationToken.None);
        await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest("req-1", new FeedbackOutcome(Completed: true)), DateTimeOffset.UtcNow), CancellationToken.None);
        var (output, error) = (new StringWriter(), new StringWriter());

        await Commands.ReportAsync([], _stores.AdminFor("owner"), output, error, CancellationToken.None);

        Assert.Contains("stop-rule: on 100% (1/1), off -", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("typed/implement 1 on / 0 off", output.ToString(), StringComparison.Ordinal);
    }
}
