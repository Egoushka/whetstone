using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.11: the pre-send coach's advice, the expander as a model challenger, and the weekly report on the person's time.</summary>
public sealed class PersonFacingTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public PersonFacingTests() => _stores = new SqliteStores(_data.Path);

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

    private async Task Seed(int count, Func<int, FeedbackOutcome> outcome, string kind = "typed/debug")
    {
        for (var i = 0; i < count; i++)
        {
            var answer = new EnhanceResponse("p", false, null, null, "", kind, $"t{i}", false);
            await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest("fix it", new EnhanceContext(null, null, kind, "claude-code")), answer, T0.AddMinutes(i)), CancellationToken.None);
            await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest($"t{i}", outcome(i)), T0.AddMinutes(i)), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_kind_with_enough_runs_gets_advice_with_its_rework_rate_and_most_common_reason()
    {
        await Seed(40, i => new FeedbackOutcome(Completed: true, AskedAgain: i % 4 == 0, CorrectionReason: i % 4 == 0 ? (i % 8 == 0 ? "incomplete" : "misread") : null));
        var enhancer = new TemplateEnhancer(new Echo(), _stores.TemplatesFor("owner"));

        var answer = await enhancer.EnhanceAsync(new EnhanceRequest("debug the importer", new EnhanceContext(null, null, "typed/debug", "claude-code")), CancellationToken.None);
        var quiet = await enhancer.EnhanceAsync(new EnhanceRequest("explore the cache", new EnhanceContext(null, null, "agent/explore", "claude-code/agent")), CancellationToken.None);

        Assert.Equal("typed/debug: 25% of the last 40 runs needed rework, most often 'incomplete'.", answer.Advice);
        Assert.Null(quiet.Advice);
    }

    [Fact]
    public void Advice_needs_thirty_runs_and_names_no_reason_when_nothing_was_reworked()
    {
        Assert.Null(new KindAdvice(29, 10, "style").Text("k"));
        Assert.Equal("k: 0% of the last 30 runs needed rework.", new KindAdvice(30, 0, null).Text("k"));
    }

    [Fact]
    public async Task The_expander_is_a_model_template_the_owner_adds_without_text()
    {
        var file = _data.FileFor("owner");
        await Seed(1, _ => new FeedbackOutcome(Completed: true));
        var (output, error) = (new StringWriter(), new StringWriter());

        var code = await Commands.TemplateAsync(["add", "--kind", "typed/debug", "--id", BuiltInTemplates.ExpandId, "--version", BuiltInTemplates.ExpandVersion, "--source", "model"],
            _stores.AdminFor("owner"), output, error, CancellationToken.None);

        Assert.Equal((Commands.Ok, ""), (code, error.ToString()));
        Assert.Single(Db.Query(file, $"SELECT * FROM templates WHERE template_id = '{BuiltInTemplates.ExpandId}' AND source = 'model'"));
        Assert.NotEqual(Commands.Ok, await Commands.TemplateAsync(["add", "--kind", "typed/debug", "--id", "empty", "--version", "1"], _stores.AdminFor("owner"), new StringWriter(), new StringWriter(), CancellationToken.None));
    }

    [Fact]
    public async Task The_person_report_shows_time_by_week_and_kind_and_rework_by_reason()
    {
        await Seed(4, i => new FeedbackOutcome(Completed: true, PersonMs: 120_000, AskedAgain: i == 0, CorrectionReason: i == 0 ? "incomplete" : null));
        var output = new StringWriter();

        Assert.Equal(Commands.Ok, await Commands.ReportAsync(["--person"], _stores.AdminFor("owner"), output, new StringWriter(), CancellationToken.None));

        Assert.Contains("2026-W42: 4 typed requests, your time 8 min over 4 of them (median 120 s)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("  typed/debug: 8 min", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("  rework 1: incomplete 1", output.ToString(), StringComparison.Ordinal);
    }
}
