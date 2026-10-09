using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whetstone.Contracts;
using Whetstone.Import;
using Whetstone.Retrieval;
using Whetstone.Storage;

namespace Whetstone.Server;

/// <summary>
/// <c>whetstone export</c> and <c>whetstone forget</c> (ADR 0003): what a person does to their own store from a terminal. They are
/// commands, not MCP tools, so no client and no agent can call them. Options are strict: an unknown or repeated option is a usage
/// error, never a wider selection, because the second command deletes.
/// </summary>
public static class Commands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;

    public const string ExportUsage = "usage: whetstone export [--repository R] [--before DATE] [--text REGEX]";

    public const string ForgetUsage = "usage: whetstone forget (--all | [--repository R] [--before DATE] [--text REGEX] [--imported]) [--confirm]";

    /// <summary>One <c>export/v1</c> record per line on <paramref name="output"/>, oldest first. Nothing else goes there.</summary>
    public static async Task<int> ExportAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(options, ["--repository", "--before", "--text"], [], out var parsed, out var problem) || !Filter(parsed!, out var filter, out problem))
            return Refuse(error, problem!, ExportUsage);
        try
        {
            await foreach (var record in admin.ExportAsync(filter!, ct))
            {
                await output.WriteAsync(JsonSerializer.Serialize(record, ContractJson.Options));
                await output.WriteAsync('\n');
            }
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "export", e);
        }
    }

    public const string ReindexUsage = "usage: whetstone reindex";

    /// <summary>Upgrades the store file if it is an older version and rebuilds its search index from the stored requests.</summary>
    public static async Task<int> ReindexAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (options.Count > 0)
            return Refuse(error, $"unknown option '{options[0]}'", ReindexUsage);
        try
        {
            var covered = await admin.ReindexAsync(ct);
            await output.WriteAsync($"Indexed {covered} {Noun(covered)}.\n");
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "reindex", e);
        }
    }

    public const string ReplayUsage = "usage: whetstone replay [--min-chars N]";

    /// <summary>
    /// Reads your store and prints, for each scored request, the earlier request that best matches it and the match's score, then how
    /// many requests are eligible to be retrieved. Changes nothing and is not an answer to any client: it is how the owner picks a
    /// threshold for retrieval (docs/specs/2026-10-05-it-retrieves-design.md).
    /// </summary>
    public static async Task<int> ReplayAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(options, ["--min-chars"], [], out var parsed, out var problem))
            return Refuse(error, problem!, ReplayUsage);
        var minChars = 0;
        if (parsed!.TryGetValue("--min-chars", out var text) && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out minChars)))
            return Refuse(error, "--min-chars takes a whole number of characters", ReplayUsage);
        try
        {
            var rows = new List<ExportRecord>();
            await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
                rows.Add(record);
            var report = Replay.Run(rows, minChars);
            await output.WriteAsync(ReplayText.Format(report));
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "replay", e);
        }
    }

    public const string ImportUsage = "usage: whetstone import claude-code DIR [--exclude DIR] [--exclude-text REGEX] [--confirm]";

    /// <summary>
    /// Reads past Claude Code sessions under DIR and stores what the person typed, each prompt with an implicit score
    /// (docs/specs/2026-10-09-it-imports-design.md). Counts and stops until <c>--confirm</c> is given, like <c>forget</c>.
    /// </summary>
    public static async Task<int> ImportAsync(
        IReadOnlyList<string> options, IStore store, IStoreAdmin admin, IRepositoryHistory history, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (options.Count < 2 || options[0] != "claude-code" || options[1].StartsWith("--", StringComparison.Ordinal))
            return Refuse(error, "say which client's sessions and where: claude-code DIR", ImportUsage);
        var directory = options[1];
        if (!Parse([.. options.Skip(2)], ["--exclude", "--exclude-text"], ["--confirm"], out var parsed, out var problem))
            return Refuse(error, problem!, ImportUsage);
        Regex? excludedText = null;
        if (parsed!.TryGetValue("--exclude-text", out var pattern) && !TryRegex(pattern, out excludedText, out problem))
            return Refuse(error, problem!.Replace("--text", "--exclude-text", StringComparison.Ordinal), ImportUsage);
        if (!Directory.Exists(directory))
            return Refuse(error, $"no directory '{directory}'", ImportUsage);
        IReadOnlyList<string> excluded = parsed.TryGetValue("--exclude", out var exclude) ? [exclude] : [];
        var write = parsed.ContainsKey("--confirm");
        try
        {
            var sessions = ClaudeCodeTranscripts.Read(directory);
            var report = await Importer.RunAsync(sessions, new ImportExclusions(excluded, excludedText), history, store, admin, write, DateTimeOffset.UtcNow, ct);
            await output.WriteAsync(
                $"{report.Sessions} sessions read, {report.ExcludedSessions} excluded; {report.Prompts} typed prompts: "
                + $"{report.New} new, {report.Matched} already stored by a live client, {report.Prompts - report.New - report.Matched} imported before.\n");
            await output.WriteAsync(write
                ? $"Stored {report.New} and scored {report.Prompts}.\n"
                : "Nothing was stored. Add --confirm to store and score them.\n");
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            return Fail(error, "import", e);
        }
    }

    /// <summary>Counts what matches and stops, until <c>--confirm</c> is given; then deletes it and reports how many went.</summary>
    public static async Task<int> ForgetAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(options, ["--repository", "--before", "--text"], ["--all", "--confirm", "--imported"], out var parsed, out var problem))
            return Refuse(error, problem!, ForgetUsage);
        var all = parsed!.ContainsKey("--all");
        var filtered = parsed.ContainsKey("--repository") || parsed.ContainsKey("--before") || parsed.ContainsKey("--text") || parsed.ContainsKey("--imported");
        if (all && filtered)
            return Refuse(error, "--all cannot be combined with a filter", ForgetUsage);
        if (!all && !filtered)
            return Refuse(error, "say what to forget: --all, --repository, --before, --text or --imported", ForgetUsage);
        if (!Filter(parsed, out var filter, out problem))
            return Refuse(error, problem!, ForgetUsage);
        try
        {
            if (!parsed.ContainsKey("--confirm"))
            {
                var matching = await admin.CountAsync(filter!, ct);
                await output.WriteAsync($"{matching} {Noun(matching)} match. Nothing was deleted. Run `whetstone export` first if you want a copy, then add --confirm to delete.\n");
                return Ok;
            }
            var deleted = await admin.ForgetAsync(filter!, ct);
            await output.WriteAsync($"Deleted {deleted} {Noun(deleted)}.\n");
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "forget", e);
        }
    }

    private static string Noun(int n) => n == 1 ? "request" : "requests";

    private static int Refuse(TextWriter error, string problem, string usage)
    {
        error.Write($"whetstone: {problem}\n{usage}\n");
        return Usage;
    }

    // Our own messages and SQLite's name the fault, not the data; the exception type goes first for the log reader.
    private static int Fail(TextWriter error, string command, Exception e)
    {
        error.Write($"whetstone: {command} failed ({e.GetType().Name}): {e.Message}\n");
        return Failed;
    }

    private static bool Filter(Dictionary<string, string> parsed, out RowFilter? filter, out string? problem)
    {
        filter = null;
        problem = null;
        DateTimeOffset? before = null;
        if (parsed.TryGetValue("--before", out var text))
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                problem = "--before takes a date or time such as 2026-10-01 or 2026-10-01T12:00:00Z (UTC when no offset is given)";
                return false;
            }
            before = when;
        }
        Regex? matching = null;
        if (parsed.TryGetValue("--text", out var pattern) && !TryRegex(pattern, out matching, out problem))
            return false;
        filter = new RowFilter(parsed.GetValueOrDefault("--repository"), before, matching, parsed.ContainsKey("--imported"));
        return true;
    }

    /// <summary>Case-insensitive and linear-time, so a pattern cannot hang a command on a long prompt.</summary>
    private static bool TryRegex(string pattern, out Regex? regex, out string? problem)
    {
        regex = null;
        problem = null;
        if (pattern.Length == 0)
        {
            problem = "--text needs a non-empty expression";
            return false;
        }
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            problem = $"--text is not an expression this whetstone can run: {e.Message}";
            return false;
        }
    }

    /// <summary>Strict: every token is a known option, each at most once, a valued option has a value, a flag has none.</summary>
    private static bool Parse(IReadOnlyList<string> options, string[] valued, string[] flags, out Dictionary<string, string>? parsed, out string? problem)
    {
        parsed = [];
        problem = null;
        for (var i = 0; i < options.Count; i++)
        {
            var name = options[i];
            if (!valued.Contains(name, StringComparer.Ordinal) && !flags.Contains(name, StringComparer.Ordinal))
            {
                problem = $"unknown option '{name}'";
                return false;
            }
            if (parsed.ContainsKey(name))
            {
                problem = $"{name} given twice";
                return false;
            }
            if (flags.Contains(name, StringComparer.Ordinal))
            {
                parsed[name] = "";
                continue;
            }
            if (i + 1 >= options.Count || options[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                problem = $"{name} needs a value";
                return false;
            }
            parsed[name] = options[++i];
        }
        return true;
    }
}
