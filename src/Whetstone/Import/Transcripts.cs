using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whetstone.Import;

/// <summary>A prompt a person typed, as a transcript recorded it.</summary>
/// <param name="MessageId">The transcript's id for the message; a resumed session repeats it, so it is how a prompt is told apart.</param>
public sealed record TypedPrompt(string MessageId, DateTimeOffset At, string Text);

/// <summary>A prompt an agent wrote: for a subagent (<c>Agent</c>) or for a page extraction (<c>WebFetch</c>), as the transcript recorded the call.</summary>
/// <param name="CallId">The transcript's id for the tool call; a resumed session repeats it.</param>
public sealed record AgentCall(
    string CallId, DateTimeOffset At, string Tool, string Prompt, string? Description, string? SubagentType, string? Model, bool Background)
{
    public const string AgentTool = "Agent";

    public const string FetchTool = "WebFetch";

    /// <summary>What the call returned, once the transcript has the result.</summary>
    public CallResult? Result { get; init; }
}

/// <summary>What a tool call's result recorded; a value the transcript does not carry is null.</summary>
public sealed record CallResult(
    bool IsError, string? Status, int? Code, long? DurationMs, long? TokensIn, long? TokensOut, long? CacheReadTokens, long? CacheWriteTokens,
    long? ToolCalls, string? Model);

/// <summary>One session's typed prompts in order, its working directory, and when anything last happened in it.</summary>
/// <param name="ToolPaths">Every path the session's tools opened, edited or searched, or a shell command moved into, as full paths.</param>
public sealed record TranscriptSession(
    string SessionId, string WorkingDirectory, IReadOnlyList<TypedPrompt> Prompts, DateTimeOffset LastActivity, IReadOnlySet<string> ToolPaths)
{
    /// <summary>Prompts the session's agents wrote, in order, each with its result when the transcript has one.</summary>
    public IReadOnlyList<AgentCall> AgentCalls { get; init; } = [];

    /// <summary>Turns an orchestrator sent into the session (turn origin <c>sdk</c>), in order.</summary>
    public IReadOnlyList<TypedPrompt> OrchestratorPrompts { get; init; } = [];

    public TranscriptSession(string sessionId, string workingDirectory, IReadOnlyList<TypedPrompt> prompts, DateTimeOffset lastActivity)
        : this(sessionId, workingDirectory, prompts, lastActivity, new HashSet<string>(StringComparer.Ordinal))
    {
    }
}

/// <summary>
/// Reads Claude Code session transcripts (one JSON object per line, one file per session) and keeps only what a person typed. The
/// format is the client's own and undocumented, so a line that does not parse or lacks a field is skipped, never an error.
/// </summary>
public static partial class ClaudeCodeTranscripts
{
    private static readonly string[] PathInputs = ["file_path", "path", "notebook_path"];

    // A shell command that moves into a folder or runs git in one: `cd D`, `pushd D`, `git -C D`.
    [GeneratedRegex(@"(?:^|[;&|(]\s*)(?:cd|pushd)\s+[""']?([^\s""';&|)]+)|\bgit\s+-C\s+[""']?([^\s""';&|)]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Moves();

    /// <summary>Longest prompt read; a longer one is pasted output, not a request.</summary>
    public const int MaxPromptChars = 500_000;

    /// <summary>Every session under <paramref name="directory"/>, any depth, merged by session id.</summary>
    public static IReadOnlyList<TranscriptSession> Read(string directory)
    {
        var sessions = new Dictionary<string, SessionBuilder>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!TryParse(line, out var entry))
                    continue;
                if (!sessions.TryGetValue(entry.SessionId, out var session))
                    sessions[entry.SessionId] = session = new SessionBuilder(entry.Cwd, entry.At);
                session.Add(entry);
            }
        }
        return [.. sessions
            .Select(s => s.Value.Build(s.Key))
            .Where(s => s.Prompts.Count > 0 || s.AgentCalls.Count > 0 || s.OrchestratorPrompts.Count > 0)
            .OrderBy(s => s.Prompts.Count > 0 ? s.Prompts[0].At : s.AgentCalls.Count > 0 ? s.AgentCalls[0].At : s.OrchestratorPrompts[0].At)];
    }

    /// <summary>One session across its files: a resumed or forked copy repeats lines, so each prompt and call is kept once, by id.</summary>
    private sealed class SessionBuilder(string cwd, DateTimeOffset first)
    {
        private readonly Dictionary<string, TypedPrompt> _prompts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypedPrompt> _orchestrated = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AgentCall> _calls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CallResult> _results = new(StringComparer.Ordinal);
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
        private DateTimeOffset _last = first;

        public void Add(Entry entry)
        {
            if (entry.Prompt is not null)
                _prompts.TryAdd(entry.Prompt.MessageId, entry.Prompt);
            if (entry.Orchestrated is not null)
                _orchestrated.TryAdd(entry.Orchestrated.MessageId, entry.Orchestrated);
            foreach (var call in entry.Calls)
                _calls.TryAdd(call.CallId, call);
            foreach (var (id, result) in entry.Results)
                _results.TryAdd(id, result);
            _paths.UnionWith(entry.Paths);
            if (entry.At > _last)
                _last = entry.At;
        }

        public TranscriptSession Build(string sessionId) =>
            new(sessionId, cwd, [.. _prompts.Values.OrderBy(p => p.At).ThenBy(p => p.MessageId, StringComparer.Ordinal)], _last, _paths)
            {
                AgentCalls = [.. _calls.Values.OrderBy(c => c.At).ThenBy(c => c.CallId, StringComparer.Ordinal).Select(c => _results.TryGetValue(c.CallId, out var r) ? c with { Result = r } : c)],
                OrchestratorPrompts = [.. _orchestrated.Values.OrderBy(p => p.At).ThenBy(p => p.MessageId, StringComparer.Ordinal)],
            };
    }

    /// <summary>One transcript line, reduced to what the import uses.</summary>
    internal sealed record Entry(
        string SessionId, string Cwd, DateTimeOffset At, TypedPrompt? Prompt, IReadOnlyList<string> Paths, TypedPrompt? Orchestrated,
        IReadOnlyList<AgentCall> Calls, IReadOnlyList<(string CallId, CallResult Result)> Results);

    /// <summary>
    /// Any line with a session, a directory and a time counts as activity; only a typed prompt yields a prompt, an orchestrator's turn
    /// an orchestrated one, and only an assistant line with tool calls yields paths and agent calls.
    /// </summary>
    internal static bool TryParse(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Entry? entry)
    {
        entry = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }
        using var _ = document;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || String(root, "sessionId") is not { Length: > 0 } sessionId
            || String(root, "cwd") is not { Length: > 0 } cwd
            || !DateTimeOffset.TryParse(String(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture, out var at))
            return false;
        entry = new Entry(sessionId, cwd, at, Typed(root, at, "human"), ToolPathsOf(root, cwd), Typed(root, at, "sdk"), CallsOf(root, at), ResultsOf(root));
        return true;
    }

    private static List<string> ToolPathsOf(JsonElement root, string cwd)
    {
        var found = new List<string>();
        if (String(root, "type") != "assistant" || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return found;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || String(part, "type") != "tool_use" || !part.TryGetProperty("input", out var input)
                || input.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var name in PathInputs)
                if (String(input, name) is { Length: > 0 } path)
                    found.Add(Full(path, cwd));
            if (String(input, "command") is { Length: > 0 } command)
                foreach (Match move in Moves().Matches(command))
                    found.Add(Full(move.Groups[1].Success ? move.Groups[1].Value : move.Groups[2].Value, cwd));
        }
        return found;
    }

    private static List<AgentCall> CallsOf(JsonElement root, DateTimeOffset at)
    {
        var found = new List<AgentCall>();
        if (String(root, "type") != "assistant" || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return found;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || String(part, "type") != "tool_use" || String(part, "id") is not { Length: > 0 } id
                || !part.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object
                || String(input, "prompt") is not { } prompt || string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptChars)
                continue;
            var tool = String(part, "name") switch { "Agent" or "Task" => AgentCall.AgentTool, "WebFetch" => AgentCall.FetchTool, _ => null };
            if (tool is not null)
                found.Add(new AgentCall(id, at, tool, prompt, String(input, "description"), String(input, "subagent_type"), String(input, "model"), True(input, "run_in_background")));
        }
        return found;
    }

    /// <summary>The results a user line carries for earlier tool calls; the structured <c>toolUseResult</c> beside them holds the measures.</summary>
    private static List<(string, CallResult)> ResultsOf(JsonElement root)
    {
        var found = new List<(string, CallResult)>();
        if (String(root, "type") != "user" || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return found;
        root.TryGetProperty("toolUseResult", out var detail);
        var structured = detail.ValueKind == JsonValueKind.Object;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || String(part, "type") != "tool_result" || String(part, "tool_use_id") is not { Length: > 0 } id)
                continue;
            var usage = structured && detail.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            found.Add((id, new CallResult(
                True(part, "is_error"), structured ? String(detail, "status") : null,
                structured ? Whole(detail, "code") is { } code ? (int)code : null : null,
                structured ? Whole(detail, "totalDurationMs") ?? Whole(detail, "durationMs") : null,
                Whole(usage, "input_tokens"), Whole(usage, "output_tokens"), Whole(usage, "cache_read_input_tokens"), Whole(usage, "cache_creation_input_tokens"),
                structured ? Whole(detail, "totalToolUseCount") : null, structured ? String(detail, "resolvedModel") : null)));
        }
        return found;
    }

    private static long? Whole(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0 ? n : null;

    /// <summary>A path as written in a tool call, made full: <c>~</c> and <c>$HOME</c> expanded, a relative one taken from the session's directory.</summary>
    internal static string Full(string path, string cwd)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            path = home + path[1..];
        else if (path.StartsWith("$HOME", StringComparison.Ordinal))
            path = home + path["$HOME".Length..];
        try
        {
            return Path.GetFullPath(path, cwd);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static TypedPrompt? Typed(JsonElement root, DateTimeOffset at, string wanted)
    {
        if (String(root, "type") != "user" || True(root, "isMeta") || True(root, "isSidechain") || root.TryGetProperty("toolUseResult", out _))
            return null;
        // Newer transcripts say who started the turn; anything but a person (an agent, a notification, a schedule) is not a prompt.
        var origin = root.TryGetProperty("turnOrigin", out var turn) && turn.ValueKind == JsonValueKind.String ? turn.GetString() : null;
        if (wanted == "human" ? root.TryGetProperty("turnOrigin", out _) && origin != "human" : origin != wanted)
            return null;
        if (String(root, "promptSource") == "system")
            return null;
        if (String(root, "uuid") is not { Length: > 0 } id || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;
        var text = Text(message);
        if (text is null || string.IsNullOrWhiteSpace(text) || text.Length > MaxPromptChars)
            return null;
        // Commands and text the client generated (tags, interruption notes, system notices).
        var start = text.TrimStart();
        if (start.StartsWith('/') || start.StartsWith('<') || start.StartsWith('['))
            return null;
        return new TypedPrompt(id, at, text);
    }

    private static string? Text(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return null;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;
        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            var kind = part.ValueKind == JsonValueKind.Object ? String(part, "type") : null;
            if (kind == "tool_result")
                return null;
            if (kind == "text" && String(part, "text") is { } text)
                parts.Add(text);
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool True(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
