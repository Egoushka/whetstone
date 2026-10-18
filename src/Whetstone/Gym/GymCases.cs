using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Whetstone.Contracts;
using Whetstone.Templates;

namespace Whetstone.Gym;

/// <summary>A stored request chosen for replay: it points at the request and keeps a hash of the prompt, never a copy (ADR 0006).</summary>
public sealed record GymCase(string CaseId, string SourceRequestId, string Repository, string Commit, string TaskKind, string PromptHash, bool Pinned, DateTimeOffset SelectedAt);

/// <summary>Why candidates were left out, for the command to print.</summary>
public sealed record SelectReport(int OfKind, int NoCommit, int LiveState, int Redacted, int AlreadyCases, int Chosen);

/// <summary>
/// Which stored requests the gym may replay (docs/specs/2026-10-19-replay-gym-design.md, decision 1): a read-only kind, a repository and
/// a commit to pin the run to, no prompt about live state (CI, pull requests, deploys, "today"), which means something else later, and
/// no redaction marker, since the worker would see the marker instead of what was asked.
/// </summary>
public static partial class GymCases
{
    [GeneratedRegex(@"\b(ci|pipeline|pull requests?|prs?|merged?|deploy(ed|ment)?|today|yesterday|right now|currently running|status of|logs?|alerts?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveState();

    public static bool Replayable(string kind) => BuiltInTemplates.ReadOnlyKinds.Contains(kind, StringComparer.Ordinal);

    public static IReadOnlyList<GymCase> Select(
        IReadOnlyList<ExportRecord> records, string kind, int limit, IReadOnlySet<string> existing, DateTimeOffset now, out SelectReport report)
    {
        int noCommit = 0, live = 0, redacted = 0, already = 0;
        var ofKind = records.Where(r => r.Context.TaskKind == kind).OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal).ToList();
        var chosen = new List<GymCase>();
        foreach (var record in ofKind)
        {
            if (chosen.Count == limit)
                break;
            if (record.Context is not { Repository: { } repository, Commit: { } commit })
                noCommit++;
            else if (LiveState().IsMatch(record.Prompt))
                live++;
            else if (record.Prompt.Contains("[REDACTED:", StringComparison.Ordinal))
                redacted++;
            else if (existing.Contains(record.RequestId))
                already++;
            else
                chosen.Add(new GymCase(Id("case:" + record.RequestId), record.RequestId, repository, commit, kind, Id(record.Prompt), false, now));
        }
        report = new SelectReport(ofKind.Count, noCommit, live, redacted, already, chosen.Count);
        return chosen;
    }

    private static string Id(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}
