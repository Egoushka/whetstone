using Microsoft.Data.Sqlite;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>The template store on disk, the arm in the store and the export, and <c>whetstone report</c>.</summary>
public sealed class ReportTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public ReportTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private async Task Record(string id, string kind, string? arm, DateTimeOffset at, bool? completed = null, string? model = "provider/model-a", string? template = null)
    {
        var response = new EnhanceResponse("p", template is not null, template, template is null ? null : "1", "r", kind, id, arm == Arms.HeldOut) { Arm = arm };
        await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest("a prompt", new EnhanceContext(null, null, kind, "claude-code/agent")), response, at), CancellationToken.None);
        if (completed is not null)
            await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(Completed: completed, Model: model, DurationMs: 2000)), at.AddMinutes(1)), CancellationToken.None);
    }

    private async Task<string> Report()
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.ReportAsync([], _stores.AdminFor("owner"), output, error, CancellationToken.None);
        Assert.Equal((Commands.Ok, ""), (code, error.ToString()));
        return output.ToString();
    }

    [Fact]
    public async Task Seeding_stores_each_version_once_and_keeps_the_role_it_has()
    {
        var templates = _stores.TemplatesFor("owner");
        await templates.SeedAsync(BuiltInTemplates.All, TemplateRoles.Champion, CancellationToken.None);
        await templates.SeedAsync(BuiltInTemplates.All, TemplateRoles.Champion, CancellationToken.None);
        var other = new Template("mine", "1", "agent/explore", "", "Another text.", "hand");
        await templates.SeedAsync([other], TemplateRoles.Champion, CancellationToken.None);
        await templates.SeedAsync([other], TemplateRoles.Challenger, CancellationToken.None);

        var explore = await templates.ForKindAsync("agent/explore", CancellationToken.None);

        Assert.Equal(BuiltInTemplates.All.Count, Db.Query(_data.FileFor("owner"), "SELECT 1 FROM templates WHERE role = 'champion'").Count);
        Assert.Equal((BuiltInTemplates.ReadOnlyId, "mine"), (explore.Champion!.Id, explore.Challenger!.Id));
        Assert.Equal(KindTemplates.None, await templates.ForKindAsync("agent/unknown", CancellationToken.None));
    }

    [Fact]
    public async Task A_kind_cannot_have_two_champions_or_two_challengers()
    {
        var templates = _stores.TemplatesFor("owner");
        await templates.SeedAsync([new Template("a", "1", "k", "", "x", "hand"), new Template("b", "1", "k", "", "y", "hand")], TemplateRoles.Champion, CancellationToken.None);
        await templates.SeedAsync([new Template("c", "1", "k", "", "z", "hand"), new Template("d", "1", "k", "", "w", "hand")], TemplateRoles.Challenger, CancellationToken.None);

        var found = await templates.ForKindAsync("k", CancellationToken.None);

        Assert.Equal(("a", "c"), (found.Champion!.Id, found.Challenger!.Id));
    }

    [Fact]
    public async Task The_arm_is_stored_with_the_request_and_appears_in_the_export()
    {
        await Record("req-1", "agent/explore", Arms.Champion, At, template: "agent-brief-readonly");
        await Record("req-2", "agent/explore", Arms.HeldOut, At.AddMinutes(1));
        await Record("req-3", "agent/explore", null, At.AddMinutes(2));

        var rows = Db.Requests(_data.FileFor("owner"));
        var export = new List<ExportRecord>();
        await foreach (var record in _stores.AdminFor("owner").ExportAsync(RowFilter.Everything, CancellationToken.None))
            export.Add(record);

        Assert.Equal(["champion", "held_out", null], rows.Select(r => r["arm"]));
        Assert.Equal(["champion", "held_out", null], export.Select(r => r.Answer.Arm));
        Assert.All(export, r => Assert.Empty(ContractSchemas.ValidateExport(System.Text.Json.JsonSerializer.SerializeToElement(r, ContractJson.Options))));
    }

    [Fact]
    public async Task A_version_three_file_gains_the_arm_column_and_the_template_table_and_keeps_its_rows()
    {
        var file = _data.FileFor("owner");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE requests (
                  request_id TEXT PRIMARY KEY, created_at TEXT NOT NULL, prompt TEXT NOT NULL, repository TEXT, commit_sha TEXT, task_kind TEXT,
                  client TEXT, changed INTEGER NOT NULL, template_id TEXT, template_version TEXT, held_out INTEGER NOT NULL, truncated INTEGER NOT NULL,
                  rewrite_accepted INTEGER, model_overridden INTEGER, score REAL, cost_usd REAL, model TEXT, feedback_at TEXT, source_request_id TEXT,
                  completed INTEGER, tokens_in INTEGER, tokens_out INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER, duration_ms INTEGER,
                  tool_calls INTEGER, asked_again INTEGER, effort TEXT
                );
                INSERT INTO requests (request_id, created_at, prompt, changed, held_out, truncated) VALUES ('old-1', '2026-10-01T09:00:00.0000000Z', 'keep me', 0, 0, 0);
                PRAGMA user_version = 3;
                """;
            create.ExecuteNonQuery();
        }

        await Record("new-1", "agent/explore", Arms.Champion, At);

        Assert.Equal(4L, Db.Query(file, "PRAGMA user_version")[0].Values.First());
        Assert.Equal(["new-1", "old-1"], Db.Requests(file).Select(r => (string)r["request_id"]!).Order(StringComparer.Ordinal));
        Assert.Single(Db.Query(file, "SELECT name FROM sqlite_master WHERE name = 'templates'"));
    }

    [Fact]
    public async Task Report_prints_each_kind_with_its_arms_completion_and_the_split_check()
    {
        await _stores.TemplatesFor("owner").SeedAsync(BuiltInTemplates.All, TemplateRoles.Champion, CancellationToken.None);
        for (var i = 0; i < 40; i++)
            await Record($"c{i}", "agent/explore", Arms.Champion, At.AddMinutes(i), completed: i % 4 != 0, template: "agent-brief-readonly");
        for (var i = 0; i < 4; i++)
            await Record($"h{i}", "agent/explore", Arms.HeldOut, At.AddMinutes(100 + i), completed: true);
        await Record("x1", "other", null, At);

        var text = await Report();

        Assert.Contains($"{BuiltInTemplates.All.Count} kinds in template trials; 1 stored requests are in none.", text, StringComparison.Ordinal);
        Assert.Contains("agent/explore", text, StringComparison.Ordinal);
        Assert.Contains("champion agent-brief-readonly v1; no challenger", text, StringComparison.Ordinal);
        Assert.Contains("runs by arm: champion 40, challenger 0, held out 4, none 0", text, StringComparison.Ordinal);
        Assert.Contains("champion: 40 runs, completed 75% (30/40)", text, StringComparison.Ordinal);
        Assert.Contains("held out: 4 runs, completed 100% (4/4)", text, StringComparison.Ordinal);
        Assert.Contains("sample ratio: ok", text, StringComparison.Ordinal);
        Assert.Contains("The report decides nothing", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_on_an_empty_or_missing_store_says_so_and_creates_nothing()
    {
        var text = await Report();

        Assert.StartsWith("0 kinds in template trials; 0 stored requests are in none.", text, StringComparison.Ordinal);
        Assert.False(File.Exists(_data.FileFor("owner")));
    }

    [Fact]
    public async Task Report_takes_no_options()
    {
        var (output, error) = (new StringWriter(), new StringWriter());

        var code = await Commands.ReportAsync(["--all"], _stores.AdminFor("owner"), output, error, CancellationToken.None);

        Assert.Equal(Commands.Usage, code);
        Assert.Contains(Commands.ReportUsage, error.ToString(), StringComparison.Ordinal);
    }
}
