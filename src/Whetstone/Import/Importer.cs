using System.Security.Cryptography;
using System.Text;
using Whetstone.Contracts;
using Whetstone.Kinds;
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
/// searched or moved into one, and those with a prompt, an agent's prompt or an orchestrator's turn matching <paramref name="Text"/>.
/// </summary>
public sealed record ImportExclusions(IReadOnlyList<string> Directories, System.Text.RegularExpressions.Regex? Text = null)
{
    public static readonly ImportExclusions None = new([]);

    public bool Exclude(TranscriptSession session) =>
        Directories.Any(prefix => Importer.Under(session.WorkingDirectory, prefix) || session.ToolPaths.Any(path => Importer.Under(path, prefix)))
        || (Text is { } text && (session.Prompts.Any(p => text.IsMatch(p.Text))
            || session.AgentCalls.Any(c => text.IsMatch(c.Prompt) || (c.Description is { } d && text.IsMatch(d)))
            || session.OrchestratorPrompts.Any(p => text.IsMatch(p.Text))));
}

/// <summary>Counts for one import; with <c>Written</c> false nothing was stored.</summary>
/// <param name="Matched">Prompts a live client had already stored; the rest that are not <paramref name="New"/> were imported before.</param>
/// <param name="Agents">The prompts agents and orchestrators wrote, counted apart from the typed ones.</param>
public sealed record ImportReport(int Sessions, int ExcludedSessions, int Prompts, int New, int Matched, bool Written, AgentTally? Agents = null)
{
    public AgentTally Agent => Agents ?? new AgentTally(0, 0, 0, 0, 0);
}

/// <summary>Counts for the agent-written prompts of an import. <c>Measured</c> are those with at least one run measure, <c>Other</c> those of kind <c>other</c>.</summary>
public sealed record AgentTally(int Prompts, int New, int Matched, int Measured, int Other);

/// <summary>An agent-written prompt ready to store: who wrote it, its kind, and what the run reported.</summary>
internal sealed record AgentItem(string Key, string Id, DateTimeOffset At, string Client, string Kind, string Prompt, string? Repository, FeedbackOutcome? Outcome);

/// <summary>
/// Stores the prompts of past sessions as requests, each with an implicit score (docs/specs/2026-10-09-it-imports-design.md).
/// Every row goes through <see cref="RequestRow.From"/>, so it is redacted like a live one. A prompt a live client already sent is
/// recognised and only scored; an imported prompt keeps an id derived from its message, so importing twice changes nothing.
/// </summary>
public static class Importer
{
    public const string Client = "claude-code";

    public const string AgentClient = "claude-code/agent";

    public const string FetchClient = "claude-code/fetch";

    public const string OrchestratorClient = "claude-code/sdk";

    /// <summary>How far apart a transcript's time and a live client's stored time may be and still be the same prompt.</summary>
    public static readonly TimeSpan SameMoment = TimeSpan.FromMinutes(2);

    /// <summary>Commits this long after a session's last activity still count toward it (a push after the last reply).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    public static async Task<ImportReport> RunAsync(
        IReadOnlyList<TranscriptSession> sessions, ImportExclusions excluded, IRepositoryHistory history,
        IStore store, IStoreAdmin admin, bool write, DateTimeOffset now, CancellationToken ct)
    {
        var (chosen, agentItems) = Choose(sessions, excluded, history, out var kept, out var skipped);
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
        return new ImportReport(kept, skipped, chosen.Count, created, matched, write, await RunAgentsAsync(agentItems, store, admin, write, now, ct));
    }

    private static async Task<AgentTally> RunAgentsAsync(
        List<AgentItem> items, IStore store, IStoreAdmin admin, bool write, DateTimeOffset now, CancellationToken ct)
    {
        int created = 0, matched = 0, measured = 0, other = 0;
        foreach (var item in items)
        {
            var row = RequestRow.From(new EnhanceRequest(item.Prompt, new EnhanceContext(item.Repository, null, item.Kind, item.Client)),
                new EnhanceResponse(item.Prompt, false, null, null, "imported", null, item.Id, false), item.At);
            var existing = await admin.FindAsync(row.RequestId, item.Client, row.Prompt, item.At, SameMoment, ct);
            if (existing is null)
                created++;
            else if (existing != row.RequestId)
                matched++;
            if (item.Outcome is { } o && (o.Completed is not null || o.TokensIn is not null || o.TokensOut is not null || o.DurationMs is not null || o.ToolCalls is not null))
                measured++;
            if (item.Kind == PromptKinds.Other)
                other++;
            if (!write)
                continue;
            if (existing is null)
                await store.RecordAsync(row, ct);
            if (item.Outcome is not null)
                await store.RecordOutcomeAsync(OutcomeRow.From(new FeedbackRequest(existing ?? row.RequestId, item.Outcome), now), ct);
        }
        return new AgentTally(items.Count, created, matched, measured, other);
    }

    /// <summary>
    /// One entry per message. A resumed or forked session repeats earlier messages under the same id; a copy followed by a prompt
    /// is judged by that prompt, so it wins over a copy that ended its session. A message seen in an excluded session is dropped
    /// everywhere: a session left out for its folder or its text keeps every copy of its messages out. Agent calls and
    /// orchestrator turns follow the same two rules, keyed by the call's or the message's id.
    /// </summary>
    private static (List<(TypedPrompt Prompt, EnhanceContext Context, double Score)> Typed, List<AgentItem> Agents) Choose(
        IReadOnlyList<TranscriptSession> sessions, ImportExclusions excluded, IRepositoryHistory history, out int kept, out int skipped)
    {
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var best = new Dictionary<string, (TypedPrompt Prompt, EnhanceContext Context, double Score, bool Last)>(StringComparer.Ordinal);
        var agents = new Dictionary<string, AgentItem>(StringComparer.Ordinal);
        kept = skipped = 0;
        foreach (var session in sessions)
        {
            if (excluded.Exclude(session))
            {
                skipped++;
                dropped.UnionWith(session.Prompts.Select(p => p.MessageId));
                dropped.UnionWith(session.AgentCalls.Select(c => c.CallId));
                dropped.UnionWith(session.OrchestratorPrompts.Select(p => p.MessageId));
                continue;
            }
            kept++;
            var repository = history.RepositoryOf(session.WorkingDirectory);
            if (session.Prompts.Count > 0)
            {
                var committed = history.CommittedBetween(session.WorkingDirectory, session.Prompts[0].At, session.LastActivity + Grace);
                var scores = ImplicitOutcome.Scores([.. session.Prompts.Select(p => p.Text)], committed);
                var context = new EnhanceContext(Repository: repository, Client: Client);
                for (var i = 0; i < session.Prompts.Count; i++)
                {
                    var prompt = session.Prompts[i];
                    var last = i == session.Prompts.Count - 1;
                    if (!best.TryGetValue(prompt.MessageId, out var seen) || (seen.Last && !last))
                        best[prompt.MessageId] = (prompt, context, scores[i], last);
                }
            }
            foreach (var call in session.AgentCalls)
                Keep(agents, AgentItemOf(call, repository));
            foreach (var turn in session.OrchestratorPrompts)
                Keep(agents, new AgentItem(turn.MessageId, IdFor(turn), turn.At, OrchestratorClient, PromptKinds.Orchestrator, turn.Text, repository, null));
        }
        return ([.. best.Values.Where(b => !dropped.Contains(b.Prompt.MessageId)).OrderBy(b => b.Prompt.At).Select(b => (b.Prompt, b.Context, b.Score))],
            [.. agents.Values.Where(a => !dropped.Contains(a.Key)).OrderBy(a => a.At).ThenBy(a => a.Id, StringComparer.Ordinal)]);
    }

    /// <summary>A copy of a call that carries its result replaces one that does not; otherwise the first copy stays.</summary>
    private static void Keep(Dictionary<string, AgentItem> items, AgentItem item)
    {
        if (!items.TryGetValue(item.Key, out var seen) || (seen.Outcome is null && item.Outcome is not null))
            items[item.Key] = item;
    }

    private static AgentItem AgentItemOf(AgentCall call, string? repository)
    {
        var id = SqliteStoreAdmin.ImportedPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("call:" + call.CallId)))[..32];
        return call.Tool == AgentCall.FetchTool
            ? new AgentItem(call.CallId, id, call.At, FetchClient, PromptKinds.Fetch, call.Prompt, repository, FetchOutcome(call))
            : new AgentItem(call.CallId, id, call.At, AgentClient, PromptKinds.ForAgent(call.SubagentType, call.Description), call.Prompt, repository, AgentOutcome(call));
    }

    /// <summary>
    /// What a subagent run reported. A background launch carries no measures; the run's own end is in a later notification the
    /// import does not read. The token counts are those of the run's final request, as the client records them.
    /// </summary>
    internal static FeedbackOutcome? AgentOutcome(AgentCall call)
    {
        if (call.Result is not { } r)
            return null;
        bool? completed = r.IsError ? false : r.Status switch
        {
            null => true,
            "completed" => true,
            "failed" or "error" or "cancelled" or "canceled" or "killed" or "aborted" or "interrupted" => false,
            _ => null,
        };
        return Measured(new FeedbackOutcome(
            Completed: completed, TokensIn: r.TokensIn, TokensOut: r.TokensOut, CacheReadTokens: r.CacheReadTokens, CacheWriteTokens: r.CacheWriteTokens,
            DurationMs: r.DurationMs, ToolCalls: r.ToolCalls, Model: r.Model ?? call.Model));
    }

    internal static FeedbackOutcome? FetchOutcome(AgentCall call) =>
        call.Result is { } r ? Measured(new FeedbackOutcome(Completed: !r.IsError && r.Code is null or < 400, DurationMs: r.DurationMs)) : null;

    /// <summary>The outcome, or null when it says nothing, so an unmeasured run stays unmeasured. A model alone is kept: it says which model ran.</summary>
    private static FeedbackOutcome? Measured(FeedbackOutcome outcome) =>
        outcome with { } == new FeedbackOutcome() ? null : outcome;

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
