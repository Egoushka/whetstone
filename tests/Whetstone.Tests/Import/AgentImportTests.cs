using System.Text.Json;
using Whetstone.Contracts;
using Whetstone.Import;
using Whetstone.Kinds;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Redaction;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Import;

/// <summary>Goal 0.4: prompts agents wrote for subagents, page extractions and orchestrators are imported with their run measures and a kind.</summary>
public sealed class AgentImportTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private const string Cwd = "/home/someone/code/example-app";

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;
    private readonly string _sessions;

    public AgentImportTests()
    {
        _stores = new SqliteStores(_data.Path);
        _sessions = Directory.CreateDirectory(Path.Combine(_data.Path, "sessions")).FullName;
    }

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class History : IRepositoryHistory
    {
        public string? RepositoryOf(string workingDirectory) => "example-app";

        public bool CommittedBetween(string workingDirectory, DateTimeOffset since, DateTimeOffset until) => false;
    }

    private static string Call(string session, string callId, int minute, string tool, object input, string cwd = Cwd) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        uuid = $"a-{callId}",
        sessionId = session,
        cwd,
        timestamp = T0.AddMinutes(minute).ToString("O"),
        message = new { role = "assistant", content = new[] { new { type = "tool_use", id = callId, name = tool, input } } },
    });

    private static string Result(string session, string callId, int minute, object? detail, bool isError = false, string cwd = Cwd) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["type"] = "user",
        ["uuid"] = $"r-{callId}",
        ["sessionId"] = session,
        ["cwd"] = cwd,
        ["timestamp"] = T0.AddMinutes(minute).ToString("O"),
        ["toolUseResult"] = detail,
        ["message"] = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = callId, content = "x", is_error = isError } } },
    });

    private static string Orchestrated(string session, string id, int minute, string text, string cwd = Cwd) => JsonSerializer.Serialize(new
    {
        type = "user",
        uuid = id,
        sessionId = session,
        cwd,
        turnOrigin = "sdk",
        timestamp = T0.AddMinutes(minute).ToString("O"),
        message = new { role = "user", content = text },
    });

    private static object Finished(long tokensIn = 2, long cacheWrite = 5714, long cacheRead = 155244, long tokensOut = 2020) => new
    {
        status = "completed",
        agentType = "Explore",
        resolvedModel = "provider/model-a",
        totalDurationMs = 322626,
        totalTokens = tokensIn + cacheWrite + cacheRead + tokensOut,
        totalToolUseCount = 29,
        usage = new { input_tokens = tokensIn, output_tokens = tokensOut, cache_read_input_tokens = cacheRead, cache_creation_input_tokens = cacheWrite },
    };

    private void Write(string file, params string[] lines) =>
        File.WriteAllLines(Path.Combine(Directory.CreateDirectory(Path.Combine(_sessions, "project")).FullName, file), lines);

    private async Task<(int Code, string Out, string Err)> Import(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.ImportAsync(["claude-code", _sessions, .. options], _stores.ForUser("owner"), _stores.AdminFor("owner"), new History(), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private List<Dictionary<string, object?>> Rows() =>
        File.Exists(_data.FileFor("owner")) ? Db.Requests(_data.FileFor("owner")) : [];

    [Fact]
    public async Task A_finished_subagent_run_is_stored_with_its_kind_and_run_measures()
    {
        Write("s1.jsonl",
            Call("s1", "c1", 1, "Agent", new { description = "Map the retry policy", subagent_type = "Explore", prompt = "list the files that define the retry policy" }),
            Result("s1", "c1", 2, Finished()));

        var (code, output, _) = await Import("--confirm");

        Assert.Equal(Commands.Ok, code);
        Assert.Contains("1 prompts written by agents and orchestrators: 1 new", output, StringComparison.Ordinal);
        var row = Assert.Single(Rows());
        Assert.Equal(("list the files that define the retry policy", "claude-code/agent", "agent/explore", "example-app"), ((string)row["prompt"]!, (string)row["client"]!, (string)row["task_kind"]!, (string)row["repository"]!));
        Assert.Equal((1L, 2L, 2020L, 155244L, 5714L), ((long)row["completed"]!, (long)row["tokens_in"]!, (long)row["tokens_out"]!, (long)row["cache_read_tokens"]!, (long)row["cache_write_tokens"]!));
        Assert.Equal((322626L, 29L, "provider/model-a"), ((long)row["duration_ms"]!, (long)row["tool_calls"]!, (string)row["model"]!));
        Assert.Null(row["score"]);
        Assert.Null(row["asked_again"]);
    }

    [Fact]
    public async Task A_background_launch_is_stored_without_measures()
    {
        Write("s1.jsonl",
            Call("s1", "c1", 1, "Agent", new { description = "Review the diff", subagent_type = "general-purpose", run_in_background = true, prompt = "review the open diff for mistakes" }),
            Result("s1", "c1", 1, new { status = "async_launched", isAsync = true, resolvedModel = "provider/model-a", agentId = "a1" }));

        await Import("--confirm");

        var row = Assert.Single(Rows());
        Assert.Equal("agent/general-purpose/review", row["task_kind"]);
        Assert.Equal("provider/model-a", row["model"]);
        Assert.All(Db.MeasureColumns, c => Assert.Null(row[c]));
    }

    [Fact]
    public async Task A_failed_run_and_a_page_fetch_are_measured()
    {
        Write("s1.jsonl",
            Call("s1", "c1", 1, "Agent", new { description = "Find usages", subagent_type = "Explore", prompt = "find usages of the parser" }),
            Result("s1", "c1", 2, "Error: the agent was stopped", isError: true),
            Call("s1", "f1", 3, "WebFetch", new { url = "https://example.test/a", prompt = "what is the default timeout" }),
            Result("s1", "f1", 3, new { bytes = 1200, code = 200, codeText = "OK", durationMs = 5096, result = "x", url = "https://example.test/a" }),
            Call("s1", "f2", 4, "WebFetch", new { url = "https://example.test/b", prompt = "what is the retry limit" }),
            Result("s1", "f2", 4, new { bytes = 10, code = 404, codeText = "Not Found", durationMs = 800, result = "x", url = "https://example.test/b" }),
            Call("s1", "f3", 5, "WebFetch", new { url = "https://example.test/c", prompt = "what is the page size" }),
            Result("s1", "f3", 5, null));

        await Import("--confirm");

        var rows = Rows().ToDictionary(r => (string)r["prompt"]!);
        Assert.Equal(0L, rows["find usages of the parser"]["completed"]);
        Assert.Equal(("claude-code/fetch", "fetch/extract", 1L, 5096L), ((string)rows["what is the default timeout"]["client"]!, (string)rows["what is the default timeout"]["task_kind"]!, (long)rows["what is the default timeout"]["completed"]!, (long)rows["what is the default timeout"]["duration_ms"]!));
        Assert.Equal((0L, 800L), ((long)rows["what is the retry limit"]["completed"]!, (long)rows["what is the retry limit"]["duration_ms"]!));
        Assert.Equal(1L, rows["what is the page size"]["completed"]);
        Assert.Null(rows["what is the page size"]["duration_ms"]);
    }

    [Fact]
    public async Task An_orchestrator_turn_is_stored_as_its_own_kind_and_not_as_a_typed_prompt()
    {
        Write("s1.jsonl", Orchestrated("s1", "o1", 1, "implement the plan in docs/plan.md and open a pull request"));

        var (_, output, _) = await Import("--confirm");

        Assert.Contains("0 typed prompts", output, StringComparison.Ordinal);
        var row = Assert.Single(Rows());
        Assert.Equal(("claude-code/sdk", "orchestrator/turn"), ((string)row["client"]!, (string)row["task_kind"]!));
        Assert.Null(row["score"]);
        Assert.Null(row["feedback_at"]);
    }

    [Theory]
    [InlineData("Explore", "anything", "agent/explore")]
    [InlineData("claude-code-guide", null, "agent/claude-code-guide")]
    [InlineData("general-purpose", "Find the callers of Parse", "agent/general-purpose/explore")]
    [InlineData(null, "Implement the exporter", "agent/general-purpose/implement")]
    [InlineData("general-purpose", "Land docs PRs 12 and 13", "agent/general-purpose/ship")]
    [InlineData("general-purpose", "Something unusual", "agent/general-purpose")]
    [InlineData("", "", "agent/general-purpose")]
    public void An_agent_prompt_is_kinded_by_subagent_type_then_by_the_first_verb_of_its_description(string? type, string? description, string kind) =>
        Assert.Equal(kind, PromptKinds.ForAgent(type, description));

    [Fact]
    public async Task A_session_of_an_excluded_folder_keeps_its_agent_prompts_out_too()
    {
        Write("s1.jsonl",
            Call("s1", "c1", 1, "Agent", new { description = "Find x", prompt = "find x in the private repo" }, cwd: "/home/someone/private/app"),
            Result("s1", "c1", 2, Finished(), cwd: "/home/someone/private/app"));
        Write("s2.jsonl",
            Call("s2", "c2", 1, "Agent", new { description = "Find y", prompt = "find y in the open repo" }),
            Result("s2", "c2", 2, Finished()));

        var (_, output, _) = await Import("--exclude", "/home/someone/private", "--confirm");

        Assert.Contains("1 excluded", output, StringComparison.Ordinal);
        Assert.Equal(["find y in the open repo"], Rows().Select(r => (string)r["prompt"]!));
    }

    [Fact]
    public async Task A_session_whose_tools_touched_an_excluded_folder_or_whose_agent_prompt_matches_the_text_stays_out()
    {
        Write("s1.jsonl",
            Call("s1", "c1", 1, "Agent", new { description = "Read it", prompt = "summarise the file" }),
            Call("s1", "t1", 2, "Read", new { file_path = "/home/someone/private/notes.md", prompt = "x" }));
        Write("s2.jsonl", Call("s2", "c2", 1, "Agent", new { description = "Find z", prompt = "find the quokkaflux table" }));
        Write("s3.jsonl", Call("s3", "c3", 1, "Agent", new { description = "Find w", prompt = "find the ordinary table" }));

        await Import("--exclude", "/home/someone/private", "--exclude-text", "quokkaflux", "--confirm");

        Assert.Equal(["find the ordinary table"], Rows().Select(r => (string)r["prompt"]!));
    }

    [Fact]
    public async Task Importing_twice_changes_nothing_and_a_copy_with_a_result_beats_one_without()
    {
        Write("s1.jsonl", Call("s1", "c1", 1, "Agent", new { description = "Find a", subagent_type = "Explore", prompt = "find a" }));
        Write("s1-resumed.jsonl",
            Call("s1", "c1", 1, "Agent", new { description = "Find a", subagent_type = "Explore", prompt = "find a" }),
            Result("s1", "c1", 2, Finished()));

        await Import("--confirm");
        var first = Rows();
        var (_, output, _) = await Import("--confirm");

        var row = Assert.Single(first);
        Assert.Equal(1L, row["completed"]);
        Assert.Equal(first.Select(r => (string)r["request_id"]!), Rows().Select(r => (string)r["request_id"]!));
        Assert.Contains("0 new", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_a_live_client_already_stored_is_matched_and_only_gains_the_measures()
    {
        var live = new EnhanceResponse("find a", false, null, null, "pass-through", "agent/explore", "live-1", false);
        await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest("find a", new EnhanceContext("example-app", null, "agent/explore", "claude-code/agent")), live, T0.AddMinutes(1)), CancellationToken.None);
        Write("s1.jsonl",
            Call("s1", "c1", 1, "Agent", new { description = "Find a", subagent_type = "Explore", prompt = "find a" }),
            Result("s1", "c1", 2, Finished()));

        var (_, output, _) = await Import("--confirm");

        Assert.Contains("1 already stored by a live client", output, StringComparison.Ordinal);
        var row = Assert.Single(Rows());
        Assert.Equal(("live-1", 1L), ((string)row["request_id"]!, (long)row["completed"]!));
    }

    [Fact]
    public async Task A_secret_in_an_agent_prompt_is_redacted_before_it_is_stored()
    {
        var secret = Corpus.Secrets[0];
        Write("s1.jsonl", Call("s1", "c1", 1, "Agent", new { description = "Use it", prompt = $"call the api with {secret.Text} and report" }));

        await Import("--confirm");

        Assert.DoesNotContain(secret.Value, Db.Everything(_data.FileFor("owner")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_confirm_nothing_is_stored_and_the_counts_include_measures_and_kinds()
    {
        Write("s1.jsonl",
            Call("s1", "c1", 1, "Agent", new { description = "Find a", subagent_type = "Explore", prompt = "find a" }),
            Result("s1", "c1", 2, Finished()),
            Call("s1", "c2", 3, "Agent", new { description = "Find b", subagent_type = "Explore", prompt = "find b" }));

        var (_, output, _) = await Import();

        Assert.Contains("2 prompts written by agents and orchestrators: 2 new", output, StringComparison.Ordinal);
        Assert.Contains("1 with run measures, 2 with a kind other than 'other'", output, StringComparison.Ordinal);
        Assert.Contains("Nothing was stored", output, StringComparison.Ordinal);
        Assert.Empty(Rows());
    }
}
