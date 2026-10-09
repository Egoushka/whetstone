using System.Security.Cryptography;
using System.Text;
using Whetstone.Contracts;
using Whetstone.Storage;

namespace Whetstone.Import;

/// <summary>What the person's repositories say about a session; the import asks, git answers (or nothing does, in tests).</summary>
public interface IRepositoryHistory
{
    /// <summary>The repository's name as a live client sends it (the main checkout's folder name); null outside a repository.</summary>
    string? RepositoryOf(string workingDirectory);

    /// <summary>Whether the repository's configured user committed, on any ref, between the two instants.</summary>
    bool CommittedBetween(string workingDirectory, DateTimeOffset since, DateTimeOffset until);
}

/// <summary>
/// Sessions an import leaves out: those that started under any of <paramref name="Directories"/> or whose tools opened, edited,
/// searched or moved into one, and those with a prompt matching <paramref name="Text"/>.
/// </summary>
public sealed record ImportExclusions(IReadOnlyList<string> Directories, System.Text.RegularExpressions.Regex? Text = null)
{
    public static readonly ImportExclusions None = new([]);

    public bool Exclude(TranscriptSession session) =>
        Directories.Any(prefix => Importer.Under(session.WorkingDirectory, prefix) || session.ToolPaths.Any(path => Importer.Under(path, prefix)))
        || (Text is { } text && session.Prompts.Any(p => text.IsMatch(p.Text)));
}

/// <summary>Counts for one import; with <c>Written</c> false nothing was stored.</summary>
/// <param name="Matched">Prompts a live client had already stored; the rest that are not <paramref name="New"/> were imported before.</param>
public sealed record ImportReport(int Sessions, int ExcludedSessions, int Prompts, int New, int Matched, bool Written);

/// <summary>
/// Stores the prompts of past sessions as requests, each with an implicit score (docs/specs/2026-10-09-it-imports-design.md).
/// Every row goes through <see cref="RequestRow.From"/>, so it is redacted like a live one. A prompt a live client already sent is
/// recognised and only scored; an imported prompt keeps an id derived from its message, so importing twice changes nothing.
/// </summary>
public static class Importer
{
    public const string Client = "claude-code";

    /// <summary>How far apart a transcript's time and a live client's stored time may be and still be the same prompt.</summary>
    public static readonly TimeSpan SameMoment = TimeSpan.FromMinutes(2);

    /// <summary>Commits this long after a session's last activity still count toward it (a push after the last reply).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    public static async Task<ImportReport> RunAsync(
        IReadOnlyList<TranscriptSession> sessions, ImportExclusions excluded, IRepositoryHistory history,
        IStore store, IStoreAdmin admin, bool write, DateTimeOffset now, CancellationToken ct)
    {
        var chosen = Choose(sessions, excluded, history, out var kept, out var skipped);
        int created = 0, matched = 0;
        foreach (var (prompt, context, score) in chosen)
        {
            var row = RequestRow.From(new EnhanceRequest(prompt.Text, context),
                new EnhanceResponse(prompt.Text, false, null, null, "imported", null, IdFor(prompt), false), prompt.At);
            var existing = await admin.FindAsync(row.RequestId, Client, row.Prompt, prompt.At, SameMoment, ct);
            if (existing is null)
                created++;
            else if (existing != row.RequestId)
                matched++;
            if (!write)
                continue;
            if (existing is null)
                await store.RecordAsync(row, ct);
            var outcome = new FeedbackRequest(existing ?? row.RequestId, new FeedbackOutcome(Score: score));
            await store.RecordOutcomeAsync(OutcomeRow.From(outcome, now), ct);
        }
        return new ImportReport(kept, skipped, chosen.Count, created, matched, write);
    }

    /// <summary>
    /// One entry per message. A resumed or forked session repeats earlier messages under the same id; a copy followed by a prompt
    /// is judged by that prompt, so it wins over a copy that ended its session. A message seen in an excluded session is dropped
    /// everywhere: a session left out for its folder or its text keeps every copy of its messages out.
    /// </summary>
    private static List<(TypedPrompt Prompt, EnhanceContext Context, double Score)> Choose(
        IReadOnlyList<TranscriptSession> sessions, ImportExclusions excluded, IRepositoryHistory history, out int kept, out int skipped)
    {
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var best = new Dictionary<string, (TypedPrompt Prompt, EnhanceContext Context, double Score, bool Last)>(StringComparer.Ordinal);
        kept = skipped = 0;
        foreach (var session in sessions)
        {
            if (excluded.Exclude(session))
            {
                skipped++;
                dropped.UnionWith(session.Prompts.Select(p => p.MessageId));
                continue;
            }
            kept++;
            var committed = history.CommittedBetween(session.WorkingDirectory, session.Prompts[0].At, session.LastActivity + Grace);
            var scores = ImplicitOutcome.Scores([.. session.Prompts.Select(p => p.Text)], committed);
            var context = new EnhanceContext(Repository: history.RepositoryOf(session.WorkingDirectory), Client: Client);
            for (var i = 0; i < session.Prompts.Count; i++)
            {
                var prompt = session.Prompts[i];
                var last = i == session.Prompts.Count - 1;
                if (!best.TryGetValue(prompt.MessageId, out var seen) || (seen.Last && !last))
                    best[prompt.MessageId] = (prompt, context, scores[i], last);
            }
        }
        return [.. best.Values.Where(b => !dropped.Contains(b.Prompt.MessageId)).OrderBy(b => b.Prompt.At).Select(b => (b.Prompt, b.Context, b.Score))];
    }

    /// <summary>A stable id from the transcript's message id, which is never stored itself.</summary>
    public static string IdFor(TypedPrompt prompt) =>
        SqliteStoreAdmin.ImportedPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt.MessageId)))[..32];

    /// <summary>Whether a directory is the prefix or inside it, compared as full paths, segment by segment.</summary>
    public static bool Under(string directory, string prefix)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix));
        return dir.Equals(root, StringComparison.Ordinal) || dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
