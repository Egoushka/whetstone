using System.Globalization;
using System.Text.Json;
using Whetstone.Contracts;
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

    public const string ExportUsage = "usage: whetstone export [--repository R] [--before DATE]";

    public const string ForgetUsage = "usage: whetstone forget (--all | --repository R [--before DATE] | --before DATE [--repository R]) [--confirm]";

    /// <summary>One <c>export/v1</c> record per line on <paramref name="output"/>, oldest first. Nothing else goes there.</summary>
    public static async Task<int> ExportAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(options, ["--repository", "--before"], [], out var parsed, out var problem) || !Filter(parsed!, out var filter, out problem))
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

    /// <summary>Counts what matches and stops, until <c>--confirm</c> is given; then deletes it and reports how many went.</summary>
    public static async Task<int> ForgetAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(options, ["--repository", "--before"], ["--all", "--confirm"], out var parsed, out var problem))
            return Refuse(error, problem!, ForgetUsage);
        var all = parsed!.ContainsKey("--all");
        if (all && (parsed.ContainsKey("--repository") || parsed.ContainsKey("--before")))
            return Refuse(error, "--all cannot be combined with a filter", ForgetUsage);
        if (!all && !parsed.ContainsKey("--repository") && !parsed.ContainsKey("--before"))
            return Refuse(error, "say what to forget: --all, --repository or --before", ForgetUsage);
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
        filter = new RowFilter(parsed.GetValueOrDefault("--repository"), before);
        return true;
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
