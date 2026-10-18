using System.Text.Json;
using Microsoft.Data.Sqlite;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;

namespace Whetstone.Tests.Storage;

/// <summary>The owner's hand labels and the schema 6 tables: labels reach the export and the report, and forget leaves no gym row behind.</summary>
public sealed class HandLabelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public HandLabelTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private async Task Seed(string id, int minute, bool? completed, bool? asked = null, string kind = "agent/explore")
    {
        var answer = new EnhanceResponse("p", false, null, null, "", kind, id, false);
        await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest($"prompt {id}", new EnhanceContext("example/app", null, kind, "test")), answer, T0.AddMinutes(minute)), CancellationToken.None);
        if (completed is not null)
            await _stores.ForUser("owner").RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(Completed: completed, AskedAgain: asked)), T0.AddMinutes(minute + 1)), CancellationToken.None);
    }

    private async Task<(int Code, string Out, string Err)> Label(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.LabelAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private async Task<List<string>> Export()
    {
        var output = new StringWriter();
        Assert.Equal(Commands.Ok, await Commands.ExportAsync([], _stores.AdminFor("owner"), output, new StringWriter(), CancellationToken.None));
        return [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    [Fact]
    public async Task A_label_reaches_the_export_and_clear_removes_it()
    {
        await Seed("r1", 0, completed: true);

        Assert.Equal(Commands.Ok, (await Label("set", "r1", "yes")).Code);
        var line = Assert.Single(await Export());
        Assert.Empty(ContractSchemas.ValidateExport(JsonDocument.Parse(line).RootElement));
        Assert.True(JsonSerializer.Deserialize<ExportRecord>(line, ContractJson.Options)!.HandLabel);

        Assert.Equal(Commands.Ok, (await Label("set", "r1", "clear")).Code);
        Assert.DoesNotContain("hand_label", Assert.Single(await Export()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("set", "nope", "yes")]
    [InlineData("set", "r1", "maybe")]
    [InlineData("list", "--limit", "0")]
    [InlineData("rate")]
    public async Task A_label_command_refuses_what_it_cannot_do(params string[] options)
    {
        await Seed("r1", 0, completed: true);

        var (code, _, error) = await Label(options);

        Assert.NotEqual(Commands.Ok, code);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task List_shows_unlabelled_runs_with_a_completion_newest_first()
    {
        await Seed("r1", 0, completed: true);
        await Seed("r2", 5, completed: false, asked: true);
        await Seed("r3", 10, completed: null);
        await Seed("r4", 15, completed: true);
        await Label("set", "r4", "no");

        var lines = (await Label("list")).Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["r2", "r1"], lines.Select(l => l.Split("  ")[0]));
        Assert.Contains("completed no, rework yes", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_says_how_often_the_proxy_agrees_with_the_labels()
    {
        await Seed("r1", 0, completed: true, asked: false);
        await Seed("r2", 5, completed: true, asked: true);
        await Label("set", "r1", "yes");
        await Label("set", "r2", "yes");
        var output = new StringWriter();

        await Commands.ReportAsync([], _stores.AdminFor("owner"), output, new StringWriter(), CancellationToken.None);

        Assert.Contains("hand labels: 2 labelled, 2 with a reported completion; the proxy (completed, no rework) agrees on 50% (1/2); label at least 30", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forget_removes_the_gym_rows_of_the_requests_it_deletes()
    {
        await Seed("r1", 0, completed: true);
        await Seed("r2", 5, completed: true);
        var file = _data.FileFor("owner");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO gym_cases (case_id, source_request_id, repository, commit_sha, task_kind, prompt_hash, selected_at) VALUES ('c1', 'r1', 'example/app', 'abc', 'agent/explore', 'h1', 't'), ('c2', 'r2', 'example/app', 'abc', 'agent/explore', 'h2', 't');
                INSERT INTO gym_runs (run_id, case_id, arm, client, started_at) VALUES ('g1', 'c1', 'control', 'chargehand', 't'), ('g2', 'c2', 'control', 'chargehand', 't');
                INSERT INTO gym_verdicts (case_id, template_id, template_version, judge_model, winner, order_flipped, decided_at) VALUES ('c1', 'x', '1', 'j', 'tie', 0, 't');
                """;
            insert.ExecuteNonQuery();
        }

        var deleted = await _stores.AdminFor("owner").ForgetAsync(new RowFilter(Text: new System.Text.RegularExpressions.Regex("r1")), CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(["c2"], Db.Query(file, "SELECT case_id FROM gym_cases").Select(r => (string)r["case_id"]!));
        Assert.Equal(["g2"], Db.Query(file, "SELECT run_id FROM gym_runs").Select(r => (string)r["run_id"]!));
        Assert.Empty(Db.Query(file, "SELECT * FROM gym_verdicts"));
    }
}
