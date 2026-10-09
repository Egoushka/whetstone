using System.Text.Json;

namespace Whetstone.Import;

/// <summary>A prompt a person typed, as a transcript recorded it.</summary>
/// <param name="MessageId">The transcript's id for the message; a resumed session repeats it, so it is how a prompt is told apart.</param>
public sealed record TypedPrompt(string MessageId, DateTimeOffset At, string Text);

/// <summary>One session's typed prompts in order, its working directory, and when anything last happened in it.</summary>
public sealed record TranscriptSession(string SessionId, string WorkingDirectory, IReadOnlyList<TypedPrompt> Prompts, DateTimeOffset LastActivity);

/// <summary>
/// Reads Claude Code session transcripts (one JSON object per line, one file per session) and keeps only what a person typed. The
/// format is the client's own and undocumented, so a line that does not parse or lacks a field is skipped, never an error.
/// </summary>
public static class ClaudeCodeTranscripts
{
    /// <summary>Longest prompt read; a longer one is pasted output, not a request.</summary>
    public const int MaxPromptChars = 500_000;

    /// <summary>Every session under <paramref name="directory"/>, any depth, merged by session id.</summary>
    public static IReadOnlyList<TranscriptSession> Read(string directory)
    {
        var sessions = new Dictionary<string, (string Cwd, Dictionary<string, TypedPrompt> Prompts, DateTimeOffset Last)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!TryParse(line, out var entry))
                    continue;
                var (sessionId, cwd, at, prompt) = entry;
                if (!sessions.TryGetValue(sessionId, out var session))
                    session = (cwd, new Dictionary<string, TypedPrompt>(StringComparer.Ordinal), at);
                if (prompt is not null)
                    session.Prompts.TryAdd(prompt.MessageId, prompt);
                sessions[sessionId] = (session.Cwd, session.Prompts, at > session.Last ? at : session.Last);
            }
        }
        return sessions
            .Where(s => s.Value.Prompts.Count > 0)
            .Select(s => new TranscriptSession(s.Key, s.Value.Cwd, [.. s.Value.Prompts.Values.OrderBy(p => p.At).ThenBy(p => p.MessageId, StringComparer.Ordinal)], s.Value.Last))
            .OrderBy(s => s.Prompts[0].At)
            .ToList();
    }

    /// <summary>Any line with a session, a directory and a time counts as activity; only a typed prompt yields a prompt.</summary>
    internal static bool TryParse(string line, out (string SessionId, string Cwd, DateTimeOffset At, TypedPrompt? Prompt) entry)
    {
        entry = default;
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
        entry = (sessionId, cwd, at, Typed(root, at));
        return true;
    }

    private static TypedPrompt? Typed(JsonElement root, DateTimeOffset at)
    {
        if (String(root, "type") != "user" || True(root, "isMeta") || True(root, "isSidechain") || root.TryGetProperty("toolUseResult", out _))
            return null;
        // Newer transcripts say who started the turn; anything but a person (an agent, a notification, a schedule) is not a prompt.
        if (root.TryGetProperty("turnOrigin", out var origin) && !(origin.ValueKind == JsonValueKind.String && origin.GetString() == "human"))
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
