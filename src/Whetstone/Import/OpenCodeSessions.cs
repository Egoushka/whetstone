using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Whetstone.Import;

/// <summary>
/// Reads OpenCode's session database (<c>opencode.db</c>: tables <c>session</c>, <c>message</c> and <c>part</c>, the last two with
/// a JSON <c>data</c> column) and keeps what a person typed, as <see cref="ClaudeCodeTranscripts"/> does for Claude Code (evidence
/// volume, decision 7). The format is the client's own and undocumented, so a row that does not parse or lacks a field is skipped, never
/// an error. Sessions a session started (subagents, with a <c>parent_id</c>) hold prompts an agent wrote and are not read.
/// </summary>
public static class OpenCodeSessions
{
    public const string DatabaseName = "opencode.db";

    /// <summary>Message ids are prefixed so they never collide with another client's in the store's derived ids.</summary>
    public const string Prefix = "opencode:";

    /// <summary>The database a path names: the file itself, or a folder holding <see cref="DatabaseName"/> (OpenCode's data folder).</summary>
    public static string? Locate(string path) =>
        File.Exists(path) ? path : Directory.Exists(path) && File.Exists(Path.Combine(path, DatabaseName)) ? Path.Combine(path, DatabaseName) : null;

    public static IReadOnlyList<TranscriptSession> Read(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        var sessions = new List<TranscriptSession>();
        foreach (var (id, directory, updated) in Sessions(connection))
        {
            var prompts = Prompts(connection, id);
            if (prompts.Count == 0)
                continue;
            var last = prompts.Max(p => p.At);
            sessions.Add(new TranscriptSession(Prefix + id, directory, prompts, At(updated) > last ? At(updated) : last, ToolPaths(connection, id, directory)));
        }
        return [.. sessions.OrderBy(s => s.Prompts[0].At)];
    }

    private static List<(string Id, string Directory, long Updated)> Sessions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, directory, time_updated FROM session WHERE parent_id IS NULL ORDER BY time_created, id";
        using var reader = command.ExecuteReader();
        var found = new List<(string, string, long)>();
        while (reader.Read())
            found.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return found;
    }

    private static List<TypedPrompt> Prompts(SqliteConnection connection, string session)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, time_created, data FROM message WHERE session_id = $session ORDER BY time_created, id";
        command.Parameters.AddWithValue("$session", session);
        using var reader = command.ExecuteReader();
        var users = new List<(string Id, long At)>();
        while (reader.Read())
            if (Json(reader.GetString(2)) is { } data && String(data, "role") == "user")
                users.Add((reader.GetString(0), reader.GetInt64(1)));
        var found = new List<TypedPrompt>();
        foreach (var (id, at) in users)
            if (Text(connection, id) is { } text)
                found.Add(new TypedPrompt(Prefix + id, At(at), text));
        return found;
    }

    /// <summary>The message's own text parts joined, or null for text the client generated (a command, a synthetic or ignored part) or none.</summary>
    private static string? Text(SqliteConnection connection, string message)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM part WHERE message_id = $message ORDER BY time_created, id";
        command.Parameters.AddWithValue("$message", message);
        using var reader = command.ExecuteReader();
        var pieces = new List<string>();
        while (reader.Read())
        {
            if (Json(reader.GetString(0)) is not { } part || String(part, "type") != "text" || True(part, "synthetic") || True(part, "ignored"))
                continue;
            if (String(part, "text") is { } text && !string.IsNullOrWhiteSpace(text))
                pieces.Add(text);
        }
        var all = string.Join("\n\n", pieces);
        if (string.IsNullOrWhiteSpace(all) || all.Length > ClaudeCodeTranscripts.MaxPromptChars)
            return null;
        var start = all.TrimStart();
        return start.StartsWith('/') || start.StartsWith('<') || start.StartsWith('[') ? null : all;
    }

    /// <summary>Every path the session's tools were given, as full paths: how an exclusion by folder reaches a session that started elsewhere.</summary>
    private static HashSet<string> ToolPaths(SqliteConnection connection, string session, string directory)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM part WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", session);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Json(reader.GetString(0)) is not { } part || String(part, "type") != "tool"
                || !part.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object
                || !state.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var name in new[] { "filePath", "file_path", "path", "workdir", "cwd" })
                if (String(input, name) is { Length: > 0 } path)
                    paths.Add(ClaudeCodeTranscripts.Full(path, directory));
        }
        return paths;
    }

    private static DateTimeOffset At(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    private static JsonElement? Json(string text)
    {
        try
        {
            var root = JsonDocument.Parse(text).RootElement;
            return root.ValueKind == JsonValueKind.Object ? root : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool True(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
