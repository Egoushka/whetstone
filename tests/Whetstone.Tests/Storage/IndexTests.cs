using System.Text.Json;
using Microsoft.Data.Sqlite;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;

namespace Whetstone.Tests.Storage;

/// <summary>Goal 0.3, task 2: the full-text index and the source column, on a new file and on an upgraded version 1 file.</summary>
public sealed class IndexTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    // The version 1 table as 0.2 created it, copied here so the upgrade is tested against what is on disk today.
    private const string VersionOne = """
        CREATE TABLE requests (
          request_id TEXT PRIMARY KEY, created_at TEXT NOT NULL, prompt TEXT NOT NULL, repository TEXT, commit_sha TEXT, task_kind TEXT,
          client TEXT, changed INTEGER NOT NULL, template_id TEXT, template_version TEXT, held_out INTEGER NOT NULL, truncated INTEGER NOT NULL,
          rewrite_accepted INTEGER, model_overridden INTEGER, score REAL, cost_usd REAL, model TEXT, feedback_at TEXT
        );
        CREATE INDEX requests_by_repository ON requests (repository, created_at);
        PRAGMA user_version = 1;
        """;

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public IndexTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private Task Record(string id, string prompt, string repository = "example/app", string? source = null)
    {
        var answer = new EnhanceResponse(prompt, false, null, null, "", null, id, false);
        var row = RequestRow.From(new EnhanceRequest(prompt, new EnhanceContext(repository, "abc1234", "review", "test")), answer, At) with { SourceRequestId = source };
        return _stores.ForUser("owner").RecordAsync(row, CancellationToken.None);
    }

    private IEnumerable<string> Search(string term) =>
        Db.Query(_data.FileFor("owner"), $"SELECT r.request_id AS id FROM requests_fts f JOIN requests r ON r.rowid = f.rowid WHERE requests_fts MATCH '{term}' ORDER BY r.request_id")
            .Select(r => (string)r["id"]!);

    private string MakeVersionOneFile(params (string Id, string Prompt, string Repository)[] rows)
    {
        var file = _data.FileFor("owner");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = VersionOne;
        create.ExecuteNonQuery();
        foreach (var (id, prompt, repository) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO requests (request_id, created_at, prompt, repository, changed, held_out, truncated) VALUES ($id, '2026-10-01T09:00:00.0000000Z', $prompt, $repository, 0, 0, 0)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$prompt", prompt);
            insert.Parameters.AddWithValue("$repository", repository);
            insert.ExecuteNonQuery();
        }
        return file;
    }

    private static object? Pragma(string file, string name) => Db.Query(file, $"PRAGMA {name}")[0].Values.First();

    [Fact]
    public async Task A_new_file_is_the_current_version_and_finds_a_prompt_by_a_word_in_it()
    {
        await Record("req-1", "stream the invoice parser");
        await Record("req-2", "rotate the signing certificate");

        Assert.Equal(4L, Pragma(_data.FileFor("owner"), "user_version"));
        Assert.Equal(["req-1"], Search("invoice"));
        Assert.Equal(["req-2"], Search("certificate"));
        Assert.Empty(Search("nothing"));
    }

    [Fact]
    public async Task A_version_one_file_is_upgraded_keeping_every_row_and_indexing_them()
    {
        var file = MakeVersionOneFile(("old-1", "migrate the quokkaflux table", "example/app"), ("old-2", "unrelated words only", "example/other"));

        await Record("new-1", "another quokkaflux question");

        Assert.Equal(4L, Pragma(file, "user_version"));
        Assert.Equal(["new-1", "old-1"], Search("quokkaflux"));
        Assert.Equal(3, Db.Requests(file).Count);
        Assert.Contains(Db.Requests(file), r => (string)r["request_id"]! == "old-1" && (string)r["prompt"]! == "migrate the quokkaflux table" && r["source_request_id"] is null);
    }

    [Fact]
    public async Task A_version_two_file_gains_the_run_measure_columns_and_keeps_its_rows()
    {
        var file = MakeVersionOneFile(("old-1", "keep this row", "example/app"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            connection.Open();
            using var upgrade = connection.CreateCommand();
            upgrade.CommandText = "ALTER TABLE requests ADD COLUMN source_request_id TEXT; PRAGMA user_version = 2";
            upgrade.ExecuteNonQuery();
        }

        await Record("new-1", "after the upgrade");

        Assert.Equal(4L, Pragma(file, "user_version"));
        var old = Assert.Single(Db.Requests(file), r => (string)r["request_id"]! == "old-1");
        Assert.All(Db.MeasureColumns, c => Assert.Null(old[c]));
    }

    [Fact]
    public async Task A_failed_upgrade_leaves_the_version_one_file_as_it_was()
    {
        var file = MakeVersionOneFile(("old-1", "keep this row", "example/app"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            connection.Open();
            using var alter = connection.CreateCommand();
            // Makes the upgrade's ALTER TABLE fail halfway through the script.
            alter.CommandText = "ALTER TABLE requests ADD COLUMN source_request_id TEXT";
            alter.ExecuteNonQuery();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => Record("new-1", "this write must not happen"));

        Assert.Equal(1L, Pragma(file, "user_version"));
        Assert.Empty(Db.Query(file, "SELECT name FROM sqlite_master WHERE name LIKE 'requests_fts%'"));
        Assert.Single(Db.Requests(file));
    }

    [Fact]
    public async Task Recording_a_request_id_again_replaces_its_words_in_the_index()
    {
        await Record("req-1", "first wording zanzibar");
        await Record("req-1", "second wording kilimanjaro");

        Assert.Empty(Search("zanzibar"));
        Assert.Equal(["req-1"], Search("kilimanjaro"));
    }

    [Fact]
    public async Task Forget_takes_the_rows_out_of_the_index_and_leaves_their_words_nowhere_in_the_file()
    {
        await Record("req-1", "secretive zanzibarquokka plan", "example/app");
        await Record("req-2", "ordinary kilimanjaro plan", "example/other");

        var (output, error) = (new StringWriter(), new StringWriter());
        Assert.Equal(0, await Commands.ForgetAsync(["--repository", "example/app", "--confirm"], _stores.AdminFor("owner"), output, error, CancellationToken.None));

        Assert.Empty(Search("zanzibarquokka"));
        Assert.Equal(["req-2"], Search("kilimanjaro"));
        Assert.DoesNotContain("zanzibarquokka", Db.Everything(_data.FileFor("owner")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forget_all_leaves_an_empty_index()
    {
        await Record("req-1", "secretive zanzibarquokka plan");

        await Commands.ForgetAsync(["--all", "--confirm"], _stores.AdminFor("owner"), new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Empty(Search("plan"));
        Assert.DoesNotContain("zanzibarquokka", Db.Everything(_data.FileFor("owner")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_source_request_is_stored_and_exported_only_when_there_is_one()
    {
        await Record("req-1", "first prompt about parsing");
        await Record("req-2", "second prompt about parsing", source: "req-1");
        var (output, error) = (new StringWriter(), new StringWriter());

        Assert.Equal(0, await Commands.ExportAsync([], _stores.AdminFor("owner"), output, error, CancellationToken.None));

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.All(lines, l => Assert.Empty(ContractSchemas.ValidateExport(l)));
        Assert.False(lines[0].GetProperty("answer").TryGetProperty("source_request_id", out _));
        Assert.Equal("req-1", lines[1].GetProperty("answer").GetProperty("source_request_id").GetString());
    }

    [Fact]
    public async Task The_commands_read_and_forget_in_a_version_one_file_before_the_server_has_upgraded_it()
    {
        var file = MakeVersionOneFile(("old-1", "alpha words", "example/app"), ("old-2", "beta words", "example/other"));
        var admin = _stores.AdminFor("owner");
        var (output, error) = (new StringWriter(), new StringWriter());

        Assert.Equal(0, await Commands.ExportAsync([], admin, output, error, CancellationToken.None));
        var records = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.False(r.GetProperty("answer").TryGetProperty("source_request_id", out _)));

        Assert.Equal(0, await Commands.ForgetAsync(["--repository", "example/app", "--confirm"], admin, new StringWriter(), error, CancellationToken.None));
        Assert.Equal("", error.ToString());
        Assert.Single(Db.Requests(file));
        Assert.Equal(1L, Pragma(file, "user_version"));
    }

    [Fact]
    public async Task Reindex_upgrades_an_old_file_and_reports_how_many_rows_it_covers()
    {
        var file = MakeVersionOneFile(("old-1", "migrate the quokkaflux table", "example/app"), ("old-2", "other words", "example/app"));
        var output = new StringWriter();

        var code = await Commands.ReindexAsync([], _stores.AdminFor("owner"), output, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal("Indexed 2 requests.\n", output.ToString());
        Assert.Equal(4L, Pragma(file, "user_version"));
        Assert.Equal(["old-1"], Search("quokkaflux"));
    }

    [Fact]
    public async Task Reindex_repairs_an_index_that_fell_out_of_step_and_does_nothing_without_a_store()
    {
        var none = new StringWriter();
        Assert.Equal(0, await Commands.ReindexAsync([], _stores.AdminFor("owner"), none, new StringWriter(), CancellationToken.None));
        Assert.Equal("Indexed 0 requests.\n", none.ToString());

        await Record("req-1", "findable zanzibar word");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _data.FileFor("owner"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var wipe = connection.CreateCommand();
            wipe.CommandText = "INSERT INTO requests_fts (requests_fts) VALUES ('delete-all')";
            wipe.ExecuteNonQuery();
        }
        Assert.Empty(Search("zanzibar"));

        await Commands.ReindexAsync([], _stores.AdminFor("owner"), new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(["req-1"], Search("zanzibar"));
    }

    [Fact]
    public async Task Reindex_refuses_an_option()
    {
        var error = new StringWriter();

        Assert.Equal(Commands.Usage, await Commands.ReindexAsync(["--all"], _stores.AdminFor("owner"), new StringWriter(), error, CancellationToken.None));
        Assert.Contains(Commands.ReindexUsage, error.ToString(), StringComparison.Ordinal);
    }
}
