using System.Text.Json;
using Microsoft.Data.Sqlite;
using Whetstone.Contracts;
using Whetstone.Import;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Import;

/// <summary>Goal 0.7, decision 7: OpenCode's session database becomes stored requests under the rules of the Claude Code import.</summary>
public sealed class OpenCodeImportTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;
    private readonly string _folder;

    public OpenCodeImportTests()
    {
        _stores = new SqliteStores(_data.Path);
        _folder = Directory.CreateDirectory(Path.Combine(_data.Path, "opencode")).FullName;
        Exec("""
            CREATE TABLE session (id TEXT PRIMARY KEY, project_id TEXT NOT NULL, parent_id TEXT, directory TEXT NOT NULL, time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL);
            CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL, time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL);
            """);
    }

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private string Database => Path.Combine(_folder, OpenCodeSessions.DatabaseName);

    private void Exec(string sql, params (string, object)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static long Ms(int minute) => T0.AddMinutes(minute).ToUnixTimeMilliseconds();

    private void Session(string id, string directory, string? parent = null) =>
        Exec("INSERT INTO session VALUES ($id, 'p', $parent, $dir, $t, $t)", ("$id", id), ("$parent", (object?)parent ?? DBNull.Value), ("$dir", directory), ("$t", Ms(0)));

    private void Message(string session, string id, int minute, string role, params string[] parts)
    {
        Exec("INSERT INTO message VALUES ($id, $s, $t, $t, $data)", ("$id", id), ("$s", session), ("$t", Ms(minute)), ("$data", JsonSerializer.Serialize(new { role })));
        for (var i = 0; i < parts.Length; i++)
            Exec("INSERT INTO part VALUES ($id, $m, $s, $t, $t, $data)", ("$id", $"{id}-{i}"), ("$m", id), ("$s", session), ("$t", Ms(minute)), ("$data", parts[i]));
    }

    private static string Text(string text, bool synthetic = false) => JsonSerializer.Serialize(new { type = "text", text, synthetic });

    private sealed class History : IRepositoryHistory
    {
        public string? RepositoryOf(string workingDirectory) => "example-app";

        public bool CommittedBetween(string workingDirectory, DateTimeOffset since, DateTimeOffset until) => false;
    }

    private async Task<(int Code, string Out, string Err)> Import(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.ImportAsync(options, _stores.ForUser("owner"), _stores.AdminFor("owner"), new History(), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Typed_prompts_come_out_in_order_and_generated_text_subagent_sessions_and_commands_stay_out()
    {
        Session("s1", "/home/someone/code/example-app");
        Message("s1", "m1", 1, "user", Text("add a retry to the upload client"));
        Message("s1", "m2", 2, "assistant", Text("Done."));
        Message("s1", "m3", 3, "user", Text("also log each attempt"), Text("<system-reminder>generated</system-reminder>", synthetic: true));
        Message("s1", "m4", 4, "user", Text("/compact"));
        Session("s2", "/home/someone/code/example-app", parent: "s1");
        Message("s2", "m5", 5, "user", Text("a prompt an agent wrote for a subagent"));

        var session = Assert.Single(OpenCodeSessions.Read(Database));

        Assert.Equal(["add a retry to the upload client", "also log each attempt"], session.Prompts.Select(p => p.Text));
        Assert.Equal(T0.AddMinutes(1), session.Prompts[0].At);
        Assert.StartsWith(OpenCodeSessions.Prefix, session.Prompts[0].MessageId, StringComparison.Ordinal);
        Assert.Equal("/home/someone/code/example-app", session.WorkingDirectory);
    }

    [Fact]
    public void A_path_a_tool_was_given_is_kept_for_exclusion_by_folder()
    {
        Session("s1", "/home/someone/code/example-app");
        Message("s1", "m1", 1, "user", Text("read the notes"), JsonSerializer.Serialize(new { type = "tool", state = new { input = new { filePath = "/home/someone/private/notes.md" } } }));

        var session = Assert.Single(OpenCodeSessions.Read(Database));

        Assert.Contains("/home/someone/private/notes.md", session.ToolPaths);
    }

    [Fact]
    public async Task The_import_counts_first_then_stores_redacted_prompts_under_the_opencode_client_and_twice_changes_nothing()
    {
        Session("s1", "/home/someone/code/example-app");
        Message("s1", "m1", 1, "user", Text("add a retry using " + "sk-ant-api03-" + new string('A', 95)));
        Message("s1", "m2", 3, "user", Text("thanks, now ship it"));

        var count = await Import("opencode", _folder);
        var stored = await Import("opencode", Database, "--confirm");
        var again = await Import("opencode", _folder, "--confirm");

        Assert.Contains("1 sessions read", count.Out, StringComparison.Ordinal);
        Assert.Contains("Nothing was stored", count.Out, StringComparison.Ordinal);
        Assert.Contains("Stored 2", stored.Out, StringComparison.Ordinal);
        Assert.Contains("2 imported before", again.Out, StringComparison.Ordinal);
        var rows = Db.Requests(_data.FileFor("owner"));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(Importer.OpenCodeClient, r["client"]));
        Assert.All(rows, r => Assert.DoesNotContain("sk-ant", (string)r["prompt"]!, StringComparison.Ordinal));
        Assert.All(rows, r => Assert.StartsWith("s-", (string)r["session_id"]!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_is_left_out_by_folder_or_by_text_like_a_claude_code_one()
    {
        Session("s1", "/home/someone/employer/app");
        Message("s1", "m1", 1, "user", Text("fix the build"));
        Session("s2", "/home/someone/code/example-app");
        Message("s2", "m2", 2, "user", Text("rotate the staging password"));
        Session("s3", "/home/someone/code/example-app");
        Message("s3", "m3", 3, "user", Text("add a retry"));

        var run = await Import("opencode", _folder, "--exclude", "/home/someone/employer", "--exclude-text", "password", "--confirm");

        Assert.Contains("1 sessions read, 2 excluded", run.Out, StringComparison.Ordinal);
        Assert.Equal("add a retry", Assert.Single(Db.Requests(_data.FileFor("owner")))["prompt"]);
    }

    [Fact]
    public async Task A_path_with_no_database_is_a_usage_error()
    {
        var run = await Import("opencode", Path.Combine(_data.Path, "missing"));

        Assert.Equal(Commands.Usage, run.Code);
        Assert.Contains(OpenCodeSessions.DatabaseName, run.Err, StringComparison.Ordinal);
    }
}
