using System.Globalization;
using Whetstone.Contracts;

namespace Whetstone.Outcomes;

/// <summary>A commit in a repository's history: its id and when it was made.</summary>
public sealed record Commit(string Sha, DateTimeOffset At);

/// <summary>A repository's local history, read by the collector. Any failure is null, never an exception.</summary>
public interface ICommitHistory
{
    /// <summary>Where the checkout of a stored repository name is; null when none is found.</summary>
    string? CheckoutOf(string repository);

    /// <summary>The commits the checkout's configured user made on any ref in [since, until); null when git cannot say.</summary>
    IReadOnlyList<Commit>? CommitsBetween(string checkout, DateTimeOffset since, DateTimeOffset until);

    /// <summary>Whether a commit made before <paramref name="until"/> reverts one of the commits; null when git cannot say.</summary>
    bool? Reverted(string checkout, IReadOnlyList<string> shas, DateTimeOffset until);
}

/// <summary>The forge's view of commits, through its command-line client. Null where it cannot say.</summary>
public interface IForge
{
    /// <summary>Whether a pull request holding the commits was merged, and whether CI passed on its first commit; nulls when there is no such pull request.</summary>
    (bool? Merged, bool? FirstPushGreen) PullRequestOf(string checkout, IReadOnlyList<string> shas);
}

/// <summary>What one collection found.</summary>
public sealed record CollectReport(int Requests, int Located, int Measured, int WithCommits, int Merged, int Reverted);

/// <summary>
/// Delayed outcomes for stored requests (docs/specs/2026-10-19-reward-signals-design.md, decision 1): the commits a request's work made,
/// whether one was reverted within 7 days, and, with a forge, whether its pull request merged with CI green on the first push. A request
/// owns the commits its repository's user made between it and the next request of the same session; the last request of a session owns
/// <see cref="LastWindow"/>. Only requests with a session and a repository take part.
/// </summary>
public static class DelayedOutcomes
{
    public static readonly TimeSpan LastWindow = TimeSpan.FromHours(2);

    public static readonly TimeSpan RevertWindow = TimeSpan.FromDays(7);

    public static IReadOnlyList<FeedbackRequest> Collect(
        IReadOnlyList<ExportRecord> records, ICommitHistory history, IForge? forge, DateTimeOffset now, out CollectReport report)
    {
        var found = new List<FeedbackRequest>();
        int requests = 0, located = 0, withCommits = 0, merged = 0, reverted = 0;
        var checkouts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var session in records.Where(r => r.Context is { SessionId: not null, Repository: not null }).GroupBy(r => r.Context.SessionId!, StringComparer.Ordinal))
        {
            var ordered = session.Select(r => (Record: r, At: At(r))).OrderBy(r => r.At).ThenBy(r => r.Record.RequestId, StringComparer.Ordinal).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var (record, at) = ordered[i];
                var until = i + 1 < ordered.Count ? ordered[i + 1].At : at + LastWindow;
                // A session that may still be going on owns more commits later; ask again then.
                if (until > now)
                    continue;
                requests++;
                var repository = record.Context.Repository!;
                if (!checkouts.TryGetValue(repository, out var checkout))
                    checkouts[repository] = checkout = history.CheckoutOf(repository);
                if (checkout is null)
                    continue;
                located++;
                if (history.CommitsBetween(checkout, at, until) is not { } commits)
                    continue;
                var outcome = new FeedbackOutcome(Commits: commits.Count);
                if (commits.Count > 0)
                {
                    withCommits++;
                    var shas = commits.Select(c => c.Sha).ToList();
                    var deadline = commits.Max(c => c.At) + RevertWindow;
                    var revert = history.Reverted(checkout, shas, deadline);
                    // No revert yet means nothing until the 7 days are over: a later run may still find one.
                    outcome = outcome with { Reverted = revert == true || (revert == false && deadline <= now) ? revert : null };
                    if (forge is not null)
                    {
                        var (isMerged, green) = forge.PullRequestOf(checkout, shas);
                        outcome = outcome with { PrMerged = isMerged, CiFirstPushGreen = green };
                    }
                    merged += outcome.PrMerged == true ? 1 : 0;
                    reverted += outcome.Reverted == true ? 1 : 0;
                }
                found.Add(new FeedbackRequest(record.RequestId, outcome));
            }
        }
        report = new CollectReport(requests, located, found.Count, withCommits, merged, reverted);
        return found;
    }

    private static DateTimeOffset At(ExportRecord record) =>
        DateTimeOffset.Parse(record.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
