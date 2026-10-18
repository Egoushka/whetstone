using System.Net;
using System.Text;
using System.Text.Json;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.7, decision 3: a configured model writes the next challenger under a cost cap, and is off until configured.</summary>
public sealed class ChallengerWriterTests : IDisposable
{
    private const string Kind = "typed/implement";

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly WriterSettings Settings = new(new Uri("https://writer.example.test/v1"), "model-w", "test-key", 0.05, 0.08, 3, 15);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public ChallengerWriterTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class Fake(Func<string, string, WriterReply> reply) : IChallengerWriter
    {
        public List<(string System, string User)> Calls { get; } = [];

        public Task<WriterReply> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct)
        {
            Calls.Add((system, user));
            return Task.FromResult(reply(system, user));
        }
    }

    private static Fake Says(string text) => new((_, _) => new WriterReply(text, 900, 120));

    private async Task Seed(int reworked = 3, string prompt = "add a retry to the upload client")
    {
        await _stores.TemplatesFor("owner").SeedAsync([new Template("base", "1", Kind, "", "Report in five lines when you finish.", "hand")], TemplateRoles.Champion, CancellationToken.None);
        for (var i = 0; i < reworked; i++)
        {
            var id = $"req-{i}";
            var response = new EnhanceResponse(prompt, true, "base", "1", "r", Kind, id, false) { Arm = Arms.Champion };
            await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest(prompt, new EnhanceContext(null, null, Kind, "claude-code")), response, Now.AddMinutes(-i)), CancellationToken.None);
            await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(Completed: true, CorrectionReason: "incomplete")), Now), CancellationToken.None);
        }
    }

    private async Task<(int Code, string Out, string Err)> Run(IChallengerWriter? client, WriterSettings? settings, params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.TemplateAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None, settings, client, () => Now);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void The_writer_is_off_without_settings_and_needs_every_one_when_any_is_given()
    {
        var all = new Dictionary<string, string>
        {
            ["WHETSTONE_WRITER_URL"] = "https://writer.example.test/v1",
            ["WHETSTONE_WRITER_MODEL"] = "model-w",
            ["WHETSTONE_WRITER_KEY"] = "k",
            ["WHETSTONE_WRITER_MAX_USD_CALL"] = "0.05",
            ["WHETSTONE_WRITER_MAX_USD_WEEK"] = "1",
            ["WHETSTONE_WRITER_PRICE"] = "3, 15",
        };

        Assert.Null(WriterSettings.From(_ => null, out var offProblem));
        Assert.Null(offProblem);
        var on = WriterSettings.From(all.GetValueOrDefault, out var onProblem);
        Assert.Null(onProblem);
        Assert.Equal(("model-w", 0.05, 1.0), (on!.Model, on.MaxUsdPerCall, on.MaxUsdPerWeek));
        Assert.Equal(0.0048, on.Cost(1000, 120), 9);
    }

    [Theory]
    [InlineData("WHETSTONE_WRITER_MODEL")]
    [InlineData("WHETSTONE_WRITER_KEY")]
    public void A_missing_setting_is_a_problem_not_a_silent_default(string missing)
    {
        var all = new Dictionary<string, string>
        {
            ["WHETSTONE_WRITER_URL"] = "https://writer.example.test/v1",
            ["WHETSTONE_WRITER_MODEL"] = "model-w",
            ["WHETSTONE_WRITER_KEY"] = "k",
            ["WHETSTONE_WRITER_MAX_USD_CALL"] = "0.05",
            ["WHETSTONE_WRITER_MAX_USD_WEEK"] = "1",
            ["WHETSTONE_WRITER_PRICE"] = "3,15",
        };
        all.Remove(missing);

        Assert.Null(WriterSettings.From(all.GetValueOrDefault, out var problem));
        Assert.Contains("are all needed", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WHETSTONE_WRITER_URL", "ftp://x")]
    [InlineData("WHETSTONE_WRITER_MAX_USD_CALL", "0")]
    [InlineData("WHETSTONE_WRITER_PRICE", "3")]
    [InlineData("WHETSTONE_WRITER_PRICE", "a,b")]
    public void A_setting_that_does_not_read_is_a_problem(string name, string value)
    {
        var all = new Dictionary<string, string>
        {
            ["WHETSTONE_WRITER_URL"] = "https://writer.example.test/v1",
            ["WHETSTONE_WRITER_MODEL"] = "model-w",
            ["WHETSTONE_WRITER_KEY"] = "k",
            ["WHETSTONE_WRITER_MAX_USD_CALL"] = "0.05",
            ["WHETSTONE_WRITER_MAX_USD_WEEK"] = "1",
            ["WHETSTONE_WRITER_PRICE"] = "3,15",
        };
        all[name] = value;

        Assert.Null(WriterSettings.From(all.GetValueOrDefault, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task With_no_settings_the_command_refuses_and_nothing_is_sent()
    {
        await Seed();

        var run = await Run(null, null, "write", "--kind", Kind, "--confirm");

        Assert.Equal(Commands.Usage, run.Code);
        Assert.Contains("the writer is off", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_confirm_it_says_what_it_would_send_and_sends_nothing()
    {
        await Seed();
        var writer = Says("Name the file first.");

        var run = await Run(writer, Settings, "write", "--kind", Kind);

        Assert.Equal(Commands.Ok, run.Code);
        Assert.Contains("3 runs of typed/implement that ended in rework", run.Out, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent", run.Out, StringComparison.Ordinal);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task A_confirmed_call_stores_the_text_as_a_challenger_with_the_runs_it_read_and_records_the_spend()
    {
        await Seed();
        var writer = Says("Name the file you will change, and list the tests that cover it, before you start.");

        var run = await Run(writer, Settings, "write", "--kind", Kind, "--confirm");

        Assert.Equal((Commands.Ok, ""), (run.Code, run.Err));
        var stored = await _stores.TemplatesFor("owner").ForKindAsync(Kind, CancellationToken.None);
        var challenger = Assert.Single(stored.Challengers);
        Assert.Equal((ChallengerWriting.Source, "Name the file you will change, and list the tests that cover it, before you start."), (challenger.Source, challenger.After));
        var export = new StringWriter();
        await Commands.TemplateAsync(["export"], _stores.AdminFor("owner"), export, new StringWriter(), CancellationToken.None);
        var record = export.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonSerializer.Deserialize<LibraryRecord>(l, ContractJson.Options)!).Single(r => r.Role == TemplateRoles.Challenger);
        Assert.Contains("req-0", record.Decisions[0].Counts, StringComparison.Ordinal);
        Assert.Contains("model-w", record.Decisions[0].Note, StringComparison.Ordinal);
        Assert.Equal(0.0045, await _stores.AdminFor("owner").WriterSpendSinceAsync(Now.AddDays(-1), CancellationToken.None), 9);
        var (system, user) = Assert.Single(writer.Calls);
        Assert.Contains("Report in five lines", user, StringComparison.Ordinal);
        Assert.Contains("[incomplete] add a retry to the upload client", user, StringComparison.Ordinal);
        Assert.Contains("slots", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_is_sent_is_redacted()
    {
        var token = "sk-ant-api03-" + new string('A', 95);
        await Seed(prompt: "call the api with " + token);
        var writer = Says("Name the file you will change first.");

        await Run(writer, Settings, "write", "--kind", Kind, "--confirm");

        var (_, user) = Assert.Single(writer.Calls);
        Assert.DoesNotContain("sk-ant", user, StringComparison.Ordinal);
        Assert.Contains("REDACTED", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_without_a_champion_or_without_rework_has_nothing_to_write_from()
    {
        var none = await Run(Says("x"), Settings, "write", "--kind", Kind, "--confirm");
        await Seed(reworked: 0);
        var clean = await Run(Says("x"), Settings, "write", "--kind", Kind, "--confirm");

        Assert.Contains("no champion", none.Err, StringComparison.Ordinal);
        Assert.Contains("no run of typed/implement has ended in rework", clean.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_that_could_cost_more_than_the_limit_is_refused_before_it_is_sent()
    {
        await Seed();
        var writer = Says("Name the file first.");

        var run = await Run(writer, Settings with { MaxUsdPerCall = 0.001 }, "write", "--kind", Kind, "--confirm");

        Assert.Equal(Commands.Failed, run.Code);
        Assert.Contains("per call", run.Err, StringComparison.Ordinal);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task The_weekly_limit_counts_what_was_spent_and_a_week_later_the_money_is_free_again()
    {
        await Seed();
        await _stores.AdminFor("owner").AddWriterSpendAsync(Now.AddDays(-2), Kind, "model-w", 0.0795, CancellationToken.None);
        var writer = Says("Name the file you will change first, then list its tests.");

        var blocked = await Run(writer, Settings, "write", "--kind", Kind, "--confirm");
        await _stores.AdminFor("owner").AddWriterSpendAsync(Now.AddDays(-9), Kind, "model-w", 5, CancellationToken.None);

        Assert.Equal(Commands.Failed, blocked.Code);
        Assert.Contains("weekly limit", blocked.Err, StringComparison.Ordinal);
        Assert.Empty(writer.Calls);
        Assert.Equal(0.0795, await _stores.AdminFor("owner").WriterSpendSinceAsync(Now.AddDays(-7), CancellationToken.None), 9);
    }

    [Theory]
    [InlineData("", "no text")]
    [InlineData("Use {{secret}} here.", "slot")]
    [InlineData("Report in five lines when you finish.", "too close to the champion")]
    public async Task A_text_that_fails_a_check_is_not_stored_but_the_call_is_still_paid_for(string reply, string why)
    {
        await Seed();

        var run = await Run(Says(reply), Settings, "write", "--kind", Kind, "--confirm");

        Assert.Equal(Commands.Failed, run.Code);
        Assert.Contains(why, run.Err, StringComparison.Ordinal);
        Assert.Empty((await _stores.TemplatesFor("owner").ForKindAsync(Kind, CancellationToken.None)).Challengers);
        Assert.True(await _stores.AdminFor("owner").WriterSpendSinceAsync(Now.AddDays(-1), CancellationToken.None) > 0);
    }

    [Fact]
    public async Task A_rewording_of_a_text_the_kind_already_lost_is_dropped_and_the_lost_text_is_shown_to_the_model()
    {
        await Seed();
        var lost = "Before changing anything, name the file you will edit and list the tests that cover it. Keep the diff small and stop when those tests pass.";
        Assert.Null(await _stores.AdminFor("owner").AddTemplateAsync(new Template("lost", "1", Kind, "", lost, "hand"), TemplateRoles.Challenger, null, CancellationToken.None));
        Assert.Null(await _stores.AdminFor("owner").RetireTemplateAsync(Kind, "lost", "1", null, CancellationToken.None));
        var writer = Says("Before changing anything, name the file you will edit and list the tests that cover it. Keep the diff small, and stop once the tests pass.");

        var run = await Run(writer, Settings, "write", "--kind", Kind, "--confirm");

        Assert.Equal(Commands.Failed, run.Code);
        Assert.Contains("too close to lost v1", run.Err, StringComparison.Ordinal);
        Assert.Contains("tried and lost", Assert.Single(writer.Calls).User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_http_writer_posts_a_chat_completion_with_the_key_in_a_header_and_reads_text_and_usage()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new Handler(async request =>
        {
            seen = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"New text."}}],"usage":{"prompt_tokens":321,"completion_tokens":45}}""", Encoding.UTF8, "application/json") };
        });
        var writer = new OpenAiWriter(Settings, new HttpClient(handler));

        var reply = await writer.CompleteAsync("sys", "usr", 600, CancellationToken.None);

        Assert.Equal(("New text.", 321L, 45L), (reply.Text, reply.TokensIn, reply.TokensOut));
        Assert.Equal("https://writer.example.test/v1/chat/completions", seen!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer test-key", seen.Headers.Authorization!.ToString());
        Assert.Contains("\"model\":\"model-w\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_status_is_an_error_that_does_not_echo_the_response_or_the_key()
    {
        var writer = new OpenAiWriter(Settings, new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret detail") }))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync("s", "u", 10, CancellationToken.None));

        Assert.Equal("the writer's endpoint answered 401", error.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
