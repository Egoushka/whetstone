using System.Text.Json;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Redaction;

namespace Whetstone.Tests.Storage;

/// <summary>Goal 0.2, bar part 2: export and forget, from the command line, on one user's own store.</summary>
public sealed class CommandTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day3 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public CommandTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private async Task Seed(string id, string repository, DateTimeOffset at, string prompt, string user = "owner")
    {
        var answer = new EnhanceResponse(prompt, false, null, null, "", null, id, false);
        await _stores.ForUser(user).RecordAsync(RequestRow.From(new EnhanceRequest(prompt, new EnhanceContext(repository, "abc1234", "review", "test")), answer, at), CancellationToken.None);
    }

    private async Task SeedThree()
    {
        await Seed("req-1", "example/app", Day1, "first prompt about the parser");
        await Seed("req-2", "example/app", Day2, "second prompt about the exporter");
        await Seed("req-3", "example/other", Day3, "third prompt about something else");
    }

    private async Task<(int Code, string Out, string Err)> Export(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.ExportAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private async Task<(int Code, string Out, string Err)> Forget(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.ForgetAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Export_writes_one_valid_record_per_line_oldest_first()
    {
        await SeedThree();

        var (code, output, error) = await Export();

        Assert.Equal(0, code);
        Assert.Equal("", error);
        var lines = Lines(output);
        Assert.Equal(3, lines.Length);
        foreach (var line in lines)
            Assert.Empty(ContractSchemas.ValidateExport(JsonDocument.Parse(line).RootElement));
        Assert.Equal(["req-1", "req-2", "req-3"], lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("request_id").GetString()));
        Assert.EndsWith("\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exported_record_carries_the_context_and_the_outcome_once_reported()
    {
        await Seed("req-1", "example/app", Day1, "review my diff");
        await _stores.ForUser("owner").RecordOutcomeAsync(
            OutcomeRow.From(new FeedbackRequest("req-1", new FeedbackOutcome(true, false, 0.9, 0.04m, "provider/model-a")), Day2), CancellationToken.None);
        await Seed("req-2", "example/app", Day2, "no feedback yet");

        var records = Lines((await Export()).Out).Select(l => JsonSerializer.Deserialize<ExportRecord>(l, ContractJson.Options)!).ToList();

        Assert.Equal("review my diff", records[0].Prompt);
        Assert.Equal(new ExportContext("example/app", "abc1234", "review", "test"), records[0].Context);
        Assert.Equal(new ExportOutcome(true, false, 0.9, 0.04, "provider/model-a", "2026-10-02T09:00:00.0000000Z"), records[0].Outcome);
        Assert.Null(records[1].Outcome);
    }

    [Fact]
    public async Task Run_measures_reach_the_export_and_are_left_out_when_unreported()
    {
        await Seed("req-1", "example/app", Day1, "explore the retry policy");
        await Seed("req-2", "example/app", Day2, "scored only");
        var measures = new FeedbackOutcome(Completed: true, TokensIn: 1840, TokensOut: 612, CacheReadTokens: 41200, CacheWriteTokens: 3100, DurationMs: 48250, ToolCalls: 14, AskedAgain: false, Effort: "medium");
        await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest("req-1", measures), Day2), CancellationToken.None);
        await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest("req-2", new FeedbackOutcome(Score: 0.5)), Day2), CancellationToken.None);

        var lines = Lines((await Export()).Out);

        foreach (var line in lines)
            Assert.Empty(ContractSchemas.ValidateExport(JsonDocument.Parse(line).RootElement));
        var first = JsonDocument.Parse(lines[0]).RootElement.GetProperty("outcome");
        Assert.Equal((true, 1840, 41200, 14, "medium"), (first.GetProperty("completed").GetBoolean(), first.GetProperty("tokens_in").GetInt32(), first.GetProperty("cache_read_tokens").GetInt32(), first.GetProperty("tool_calls").GetInt32(), first.GetProperty("effort").GetString()));
        var second = JsonDocument.Parse(lines[1]).RootElement.GetProperty("outcome");
        Assert.False(second.TryGetProperty("completed", out _));
        Assert.False(second.TryGetProperty("tokens_in", out _));
    }

    [Fact]
    public async Task Export_filters_by_repository_and_by_date()
    {
        await SeedThree();

        Assert.Equal(["req-1", "req-2"], Ids((await Export("--repository", "example/app")).Out));
        Assert.Equal(["req-1", "req-2"], Ids((await Export("--before", "2026-10-03")).Out));
        Assert.Equal(["req-1"], Ids((await Export("--before", "2026-10-02T09:00:00Z")).Out));
        Assert.Equal(["req-1"], Ids((await Export("--repository", "example/app", "--before", "2026-10-02")).Out));
    }

    private static string[] Ids(string output) => [.. Lines(output).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("request_id").GetString()!)];

    [Fact]
    public async Task Export_for_a_user_with_no_store_prints_nothing_and_creates_nothing()
    {
        var (code, output, _) = await Export();

        Assert.Equal(0, code);
        Assert.Equal("", output);
        Assert.False(Directory.Exists(Path.Combine(_data.Path, "owner")));
    }

    [Fact]
    public async Task Forget_counts_and_deletes_nothing_until_confirmed()
    {
        await SeedThree();

        var (code, output, _) = await Forget("--repository", "example/app");

        Assert.Equal(0, code);
        Assert.Contains("2 requests match. Nothing was deleted.", output, StringComparison.Ordinal);
        Assert.Equal(3, Db.Requests(_data.FileFor("owner")).Count);
    }

    [Fact]
    public async Task Forget_with_a_repository_removes_only_that_repository()
    {
        await SeedThree();

        var (code, output, _) = await Forget("--repository", "example/app", "--confirm");

        Assert.Equal(0, code);
        Assert.Equal("Deleted 2 requests.\n", output);
        Assert.Equal(["req-3"], Ids((await Export()).Out));
    }

    [Fact]
    public async Task Forget_with_text_removes_only_prompts_that_match_ignoring_case()
    {
        await SeedThree();

        var (code, output, _) = await Forget("--text", "PARSER|exporter", "--confirm");

        Assert.Equal(0, code);
        Assert.Equal("Deleted 2 requests.\n", output);
        Assert.Equal(["req-3"], Ids((await Export()).Out));
    }

    [Fact]
    public async Task Text_narrows_together_with_the_other_filters_and_export_takes_it_too()
    {
        await SeedThree();

        Assert.Equal(["req-1"], Ids((await Export("--text", "prompt about", "--before", "2026-10-02")).Out));
        Assert.Equal("1 request match. Nothing was deleted. Run `whetstone export` first if you want a copy, then add --confirm to delete.\n",
            (await Forget("--text", "something", "--repository", "example/other")).Out);
        Assert.Equal(3, Db.Requests(_data.FileFor("owner")).Count);
    }

    [Fact]
    public async Task Forget_before_a_date_removes_only_what_came_before()
    {
        await SeedThree();

        await Forget("--before", "2026-10-03", "--confirm");

        Assert.Equal(["req-3"], Ids((await Export()).Out));
    }

    [Fact]
    public async Task Export_then_forget_all_then_export_leaves_nothing()
    {
        await SeedThree();
        Assert.Equal(3, Lines((await Export()).Out).Length);

        var (code, output, _) = await Forget("--all", "--confirm");

        Assert.Equal(0, code);
        Assert.Equal("Deleted 3 requests.\n", output);
        Assert.Equal("", (await Export()).Out);
        Assert.Empty(Db.Requests(_data.FileFor("owner")));
    }

    [Fact]
    public async Task What_was_forgotten_is_not_left_in_the_file_and_what_stays_is()
    {
        await Seed("req-1", "example/app", Day1, "zebra-unique-text-to-forget");
        await Seed("req-2", "example/keep", Day2, "giraffe-unique-text-to-keep");

        await Forget("--repository", "example/app", "--confirm");

        var file = Db.Everything(_data.FileFor("owner"));
        Assert.DoesNotContain("zebra-unique-text-to-forget", file, StringComparison.Ordinal);
        Assert.DoesNotContain("example/app", file, StringComparison.Ordinal);
        Assert.Contains("giraffe-unique-text-to-keep", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forget_leaves_another_users_store_alone()
    {
        await Seed("req-1", "example/app", Day1, "mine");
        await Seed("req-2", "example/app", Day1, "theirs", user: "bob");

        await Forget("--all", "--confirm");

        Assert.Equal("theirs", Assert.Single(Db.Requests(_data.FileFor("bob")))["prompt"]);
    }

    [Fact]
    public async Task Forget_for_a_user_with_no_store_deletes_nothing_and_creates_nothing()
    {
        var (code, output, _) = await Forget("--all", "--confirm");

        Assert.Equal(0, code);
        Assert.Equal("Deleted 0 requests.\n", output);
        Assert.False(Directory.Exists(Path.Combine(_data.Path, "owner")));
    }

    [Theory]
    [InlineData]
    [InlineData("--confirm")]
    [InlineData("--all", "--repository", "example/app")]
    [InlineData("--all", "--before", "2026-10-03")]
    [InlineData("--repositry", "example/app")]
    [InlineData("--repository")]
    [InlineData("--repository", "--confirm")]
    [InlineData("--before", "next tuesday")]
    [InlineData("--all", "--all")]
    [InlineData("--repository", "a", "--repository", "b")]
    [InlineData("example/app")]
    [InlineData("--all", "--text", "parser")]
    [InlineData("--text", "")]
    [InlineData("--text", "(unclosed")]
    [InlineData("--text", "parser", "--text", "exporter")]
    public async Task A_forget_that_is_not_clearly_asked_for_is_a_usage_error_and_deletes_nothing(params string[] options)
    {
        await SeedThree();

        var (code, output, error) = await Forget(options.Contains("--confirm") ? options : [.. options, "--confirm"]);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("usage: whetstone forget", error, StringComparison.Ordinal);
        Assert.Equal(3, Db.Requests(_data.FileFor("owner")).Count);
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("--before", "yesterday-ish")]
    [InlineData("--repository")]
    [InlineData("--confirm")]
    [InlineData("--text", "[")]
    public async Task An_export_with_a_bad_option_is_a_usage_error(params string[] options)
    {
        await SeedThree();

        var (code, output, error) = await Export(options);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("usage: whetstone export", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_from_a_newer_whetstone_is_a_failure_with_the_reason_not_a_crash()
    {
        await SeedThree();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_data.FileFor("owner")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        var (code, _, error) = await Forget("--all", "--confirm");

        Assert.Equal(1, code);
        Assert.Contains("schema version 99", error, StringComparison.Ordinal);
        Assert.Equal(3, Db.Requests(_data.FileFor("owner")).Count);
    }

    [Fact]
    public async Task Exported_prompts_hold_no_seeded_secret()
    {
        var i = 0;
        foreach (var seeded in Corpus.Secrets)
            await Seed($"req-{i++}", "example/app", Day1, $"Fix this error: {seeded.Text}");

        var (_, output, _) = await Export();

        foreach (var seeded in Corpus.Secrets)
            Assert.False(output.Contains(seeded.Value, StringComparison.Ordinal), $"{seeded.Name} is in the export");
    }
}
