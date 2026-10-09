using System.Text.Json;
using Whetstone.Contracts;
using Whetstone.Import;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Redaction;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Import;

/// <summary>Goal 0.7, brought forward: past sessions become stored requests, each with a score from what came next.</summary>
public sealed class ImportTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;
    private readonly string _sessions;

    public ImportTests()
    {
        _stores = new SqliteStores(_data.Path);
        _sessions = Directory.CreateDirectory(Path.Combine(_data.Path, "sessions")).FullName;
    }

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class History(bool committed = false) : IRepositoryHistory
    {
        public string? RepositoryOf(string workingDirectory) => workingDirectory.EndsWith("/elsewhere", StringComparison.Ordinal) ? null : "example-app";

        public bool CommittedBetween(string workingDirectory, DateTimeOffset since, DateTimeOffset until) => committed;
    }

    private static string Typed(string session, string id, int minute, string text, string cwd = "/home/someone/code/example-app", object? origin = null)
    {
        var line = new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["uuid"] = id,
            ["sessionId"] = session,
            ["cwd"] = cwd,
            ["timestamp"] = T0.AddMinutes(minute).ToString("O"),
            ["message"] = new { role = "user", content = text },
        };
        if (origin is not null)
            line["turnOrigin"] = origin;
        return JsonSerializer.Serialize(line);
    }

    private static string Reply(string session, int minute, string cwd = "/home/someone/code/example-app") => JsonSerializer.Serialize(new
    {
        type = "assistant",
        uuid = $"a-{minute}",
        sessionId = session,
        cwd,
        timestamp = T0.AddMinutes(minute).ToString("O"),
        message = new { role = "assistant", content = "done" },
    });

    private void Write(string file, params string[] lines) =>
        File.WriteAllLines(Path.Combine(Directory.CreateDirectory(Path.Combine(_sessions, "project")).FullName, file), lines);

    private async Task<(int Code, string Out, string Err)> Import(IRepositoryHistory? history = null, params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.ImportAsync(options, _stores.ForUser("owner"), _stores.AdminFor("owner"), history ?? new History(), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private List<Dictionary<string, object?>> Rows() =>
        File.Exists(_data.FileFor("owner")) ? Db.Requests(_data.FileFor("owner")) : [];

    [Theory]
    [InlineData("no, use the other table", ImplicitOutcome.Correction)]
    [InlineData("That's wrong, the id is a string", ImplicitOutcome.Correction)]
    [InlineData("it still fails on CI", ImplicitOutcome.Correction)]
    [InlineData("ні, не так", ImplicitOutcome.Correction)]
    [InlineData("ok but you forgot the tests", ImplicitOutcome.Correction)]
    [InlineData("thanks!", ImplicitOutcome.Praise)]
    [InlineData("LGTM, merge it", ImplicitOutcome.Praise)]
    [InlineData("дякую", ImplicitOutcome.Praise)]
    [InlineData("now add a retry to the upload client", ImplicitOutcome.MovedOn)]
    [InlineData("nothing changed in the logs", ImplicitOutcome.MovedOn)]
    public void The_next_prompt_corrects_praises_or_moves_on(string next, double score) =>
        Assert.Equal(score, ImplicitOutcome.Judge(next));

    [Fact]
    public void The_last_prompt_gets_the_default_and_a_commit_adds_the_bonus_up_to_one()
    {
        Assert.Equal([ImplicitOutcome.Praise, ImplicitOutcome.Last], ImplicitOutcome.Scores(["fix it", "thanks"], committed: false));
        Assert.Equal([1.0, 0.7], ImplicitOutcome.Scores(["fix it", "thanks"], committed: true));
    }

    [Fact]
    public void Only_what_a_person_typed_is_read()
    {
        var toolResult = JsonSerializer.Serialize(new
        {
            type = "user",
            uuid = "t1",
            sessionId = "s1",
            cwd = "/c",
            timestamp = T0.ToString("O"),
            message = new { content = new object[] { new { type = "tool_result", content = "output" } } },
        });
        var parts = JsonSerializer.Serialize(new
        {
            type = "user",
            uuid = "p1",
            sessionId = "s1",
            cwd = "/c",
            timestamp = T0.AddMinutes(9).ToString("O"),
            message = new { content = new object[] { new { type = "text", text = "first part" }, new { type = "text", text = "second part" } } },
        });
        Write("s1.jsonl",
            Typed("s1", "u1", 1, "make the parser accept tabs"),
            "not json at all",
            toolResult,
            Typed("s1", "u2", 2, "/compact"),
            Typed("s1", "u3", 3, "<task-notification>done</task-notification>"),
            Typed("s1", "u4", 4, "[Request interrupted by user]"),
            Typed("s1", "u5", 5, "a prompt an agent sent", origin: "sdk"),
            Typed("s1", "u6", 6, "a scheduled run", origin: "scheduled"),
            Typed("s1", "u7", 7, "a prompt the person typed", origin: "human"),
            parts,
            Reply("s1", 10));

        var session = Assert.Single(ClaudeCodeTranscripts.Read(_sessions));

        Assert.Equal(["make the parser accept tabs", "a prompt the person typed", "first part\nsecond part"], session.Prompts.Select(p => p.Text));
        Assert.Equal(T0.AddMinutes(10), session.LastActivity);
    }

    [Fact]
    public async Task Without_confirm_it_counts_and_stores_nothing()
    {
        Write("s1.jsonl", Typed("s1", "u1", 1, "make the parser accept tabs"), Typed("s1", "u2", 2, "thanks"));

        var (code, output, _) = await Import(null, "claude-code", _sessions);

        Assert.Equal(Commands.Ok, code);
        Assert.Contains("2 typed prompts: 2 new", output, StringComparison.Ordinal);
        Assert.Contains("Nothing was stored", output, StringComparison.Ordinal);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task With_confirm_each_prompt_is_stored_at_its_time_with_its_score()
    {
        Write("s1.jsonl",
            Typed("s1", "u1", 1, "make the parser accept tabs"),
            Typed("s1", "u2", 2, "no, tabs and spaces both"),
            Typed("s1", "u3", 3, "great"),
            Reply("s1", 4));

        var (code, _, _) = await Import(null, "claude-code", _sessions, "--confirm");

        Assert.Equal(Commands.Ok, code);
        var rows = Rows();
        Assert.Equal([ImplicitOutcome.Correction, ImplicitOutcome.Praise, ImplicitOutcome.Last], rows.Select(r => (double)r["score"]!));
        Assert.All(rows, r => Assert.Equal("claude-code", r["client"]));
        Assert.All(rows, r => Assert.Equal("example-app", r["repository"]));
        Assert.Equal(T0.AddMinutes(1).UtcDateTime.ToString("O"), rows[0]["created_at"]);
        Assert.All(rows, r => Assert.StartsWith("imp-", (string)r["request_id"]!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Importing_twice_changes_nothing()
    {
        Write("s1.jsonl", Typed("s1", "u1", 1, "make the parser accept tabs"), Typed("s1", "u2", 2, "thanks"));
        await Import(null, "claude-code", _sessions, "--confirm");
        var first = Rows();

        var (_, output, _) = await Import(null, "claude-code", _sessions, "--confirm");

        Assert.Equal(first.Select(r => (r["request_id"], r["score"])), Rows().Select(r => (r["request_id"], r["score"])));
        Assert.Contains("0 new, 0 already stored by a live client, 2 imported before", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_a_live_client_already_sent_is_scored_not_stored_again()
    {
        var live = new EnhanceResponse("make the parser accept tabs", false, null, null, "", null, "req-live", false);
        await _stores.ForUser("owner").RecordAsync(
            RequestRow.From(new EnhanceRequest("make the parser accept tabs", new EnhanceContext(Client: "claude-code")), live, T0.AddMinutes(1).AddSeconds(3)),
            CancellationToken.None);
        Write("s1.jsonl", Typed("s1", "u1", 1, "make the parser accept tabs"), Typed("s1", "u2", 2, "thanks"));

        await Import(null, "claude-code", _sessions, "--confirm");

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(ImplicitOutcome.Praise, (double)rows.Single(r => (string)r["request_id"]! == "req-live")["score"]!);
    }

    [Fact]
    public async Task An_excluded_folder_stays_out_even_when_a_message_was_copied_into_another_session()
    {
        Write("s1.jsonl", Typed("s1", "u1", 1, "a prompt from a private folder", cwd: "/home/someone/private/thing"));
        Write("s2.jsonl", Typed("s2", "u1", 1, "a prompt from a private folder"), Typed("s2", "u2", 2, "a prompt from an open folder"));

        var (_, output, _) = await Import(null, "claude-code", _sessions, "--exclude", "/home/someone/private", "--confirm");

        Assert.Contains("1 excluded", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private", Db.Everything(_data.FileFor("owner")), StringComparison.Ordinal);
        Assert.Single(Rows());
    }

    [Fact]
    public async Task A_session_with_a_prompt_matching_the_excluded_text_stays_out_whole()
    {
        Write("s1.jsonl", Typed("s1", "u1", 1, "look at ticket ACME-123 first"), Typed("s1", "u2", 2, "now fix the parser"));
        Write("s2.jsonl", Typed("s2", "u3", 1, "make the exporter faster"));

        var (_, output, _) = await Import(null, "claude-code", _sessions, "--exclude-text", "acme-[0-9]+", "--confirm");

        Assert.Contains("1 excluded", output, StringComparison.Ordinal);
        Assert.Equal(["make the exporter faster"], Rows().Select(r => (string)r["prompt"]!));
    }

    [Fact]
    public async Task A_message_repeated_by_a_resumed_session_is_stored_once_and_judged_by_what_followed_it()
    {
        Write("s1.jsonl", Typed("s1", "u1", 1, "make the parser accept tabs"));
        Write("s2.jsonl", Typed("s2", "u1", 1, "make the parser accept tabs"), Typed("s2", "u2", 30, "that's wrong"));

        await Import(null, "claude-code", _sessions, "--confirm");

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(ImplicitOutcome.Correction, (double)rows[0]["score"]!);
    }

    [Fact]
    public async Task A_session_with_a_commit_gets_the_bonus()
    {
        Write("s1.jsonl", Typed("s1", "u1", 1, "make the parser accept tabs"), Typed("s1", "u2", 2, "now the exporter"));

        await Import(new History(committed: true), "claude-code", _sessions, "--confirm");

        Assert.Equal([0.8, 0.7], Rows().Select(r => (double)r["score"]!));
    }

    [Fact]
    public async Task Secrets_in_past_prompts_are_redacted_on_the_way_in()
    {
        var lines = Corpus.Secrets.Select((s, i) => Typed("s1", $"u{i}", i, $"use this key {s.Text} for the call")).ToArray();
        Write("s1.jsonl", lines);

        await Import(null, "claude-code", _sessions, "--confirm");

        var file = Db.Everything(_data.FileFor("owner"));
        Assert.Equal(Corpus.Secrets.Count, Rows().Count);
        foreach (var seeded in Corpus.Secrets)
            Assert.DoesNotContain(seeded.Value, file, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("claude-code")]
    [InlineData("other-client", "/tmp")]
    [InlineData("claude-code", "--confirm")]
    [InlineData("claude-code", "{dir}", "--exclude")]
    [InlineData("claude-code", "{dir}", "--confirm", "--confirm")]
    [InlineData("claude-code", "{dir}", "--wider")]
    [InlineData("claude-code", "/no/such/folder/anywhere")]
    [InlineData("claude-code", "{dir}", "--exclude-text", "(")]
    public async Task A_wrong_command_line_is_a_usage_error_and_stores_nothing(params string[] options)
    {
        Write("s1.jsonl", Typed("s1", "u1", 1, "make the parser accept tabs"));

        var (code, _, error) = await Import(null, [.. options.Select(o => o.Replace("{dir}", _sessions, StringComparison.Ordinal))]);

        Assert.Equal(Commands.Usage, code);
        Assert.Contains(Commands.ImportUsage, error, StringComparison.Ordinal);
        Assert.Empty(Rows());
    }

    [Theory]
    [InlineData("/a/b", "/a/b", true)]
    [InlineData("/a/b/c", "/a/b", true)]
    [InlineData("/a/b/", "/a/b", true)]
    [InlineData("/a/bc", "/a/b", false)]
    [InlineData("/a", "/a/b", false)]
    public void A_folder_is_under_a_prefix_segment_by_segment(string directory, string prefix, bool under) =>
        Assert.Equal(under, Importer.Under(directory, prefix));
}
