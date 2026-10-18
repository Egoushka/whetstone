using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Whetstone.Contracts;
using Whetstone.Retrieval;
using Whetstone.Templates;

namespace Whetstone.Storage;

/// <summary>
/// One SQLite file per user, <c>&lt;data dir&gt;/&lt;user&gt;/whetstone.db</c>, the directory readable by its owner only and the file
/// too (ADR 0003). The user id becomes a path, so it is held to a short safe alphabet.
/// </summary>
public sealed partial class SqliteStores(string dataDirectory) : IStores, IDisposable
{
    public const string DefaultUser = "owner";

    public const string FileName = "whetstone.db";

    private readonly Dictionary<string, SqliteStore> _stores = [];

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,63}\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex UserId();

    public static bool ValidUser(string user) => UserId().IsMatch(user);

    /// <summary>Where a user's file is, without touching the disk.</summary>
    public string PathFor(string user)
    {
        if (!ValidUser(user))
            throw new ArgumentException("a user id is 1 to 64 characters of a-z, 0-9, '_' and '-', starting with a letter or digit", nameof(user));
        return Path.Combine(dataDirectory, user, FileName);
    }

    /// <summary>The commands' view of a user's store (ADR 0003). Reaches only that user's file, and never creates it.</summary>
    public IStoreAdmin AdminFor(string user) => new SqliteStoreAdmin(PathFor(user));

    /// <summary>What the retriever may read of a user's store (goal 0.3); the same file as <see cref="ForUser"/>.</summary>
    public IRetrievalIndex IndexFor(string user) => (SqliteStore)ForUser(user);

    /// <summary>The template store of a user; the same file as <see cref="ForUser"/>.</summary>
    public ITemplates TemplatesFor(string user) => (SqliteStore)ForUser(user);

    public IStore ForUser(string user)
    {
        var path = PathFor(user);
        lock (_stores)
        {
            if (!_stores.TryGetValue(path, out var store))
                _stores[path] = store = new SqliteStore(path);
            return store;
        }
    }

    public void Dispose()
    {
        lock (_stores)
        {
            foreach (var store in _stores.Values)
                store.Dispose();
            _stores.Clear();
        }
    }
}

/// <summary>A user's file, opened per call: one writer at a time, no connection held between requests.</summary>
internal sealed class SqliteStore(string path) : IStore, IRetrievalIndex, ITemplates, IDisposable
{
    private const int BusyMilliseconds = 200;

    private static readonly UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    public long Failures => 0;

    public void Dispose() => _gate.Dispose();

    public Task RecordAsync(RequestRow row, CancellationToken ct) => UseAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO requests
              (request_id, created_at, prompt, repository, commit_sha, task_kind, client, changed, template_id, template_version, held_out, truncated, source_request_id, arm, session_id)
            VALUES ($id, $at, $prompt, $repository, $commit, $kind, $client, $changed, $tid, $tversion, $held, $cut, $source, $arm, $session)
            """;
        command.Parameters.AddWithValue("$id", row.RequestId);
        command.Parameters.AddWithValue("$at", Text(row.CreatedAt));
        command.Parameters.AddWithValue("$prompt", row.Prompt);
        command.Parameters.AddWithValue("$repository", (object?)row.Repository ?? DBNull.Value);
        command.Parameters.AddWithValue("$commit", (object?)row.Commit ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (object?)row.TaskKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$client", (object?)row.Client ?? DBNull.Value);
        command.Parameters.AddWithValue("$changed", row.Changed ? 1 : 0);
        command.Parameters.AddWithValue("$tid", (object?)row.TemplateId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tversion", (object?)row.TemplateVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$held", row.HeldOut ? 1 : 0);
        command.Parameters.AddWithValue("$cut", row.Truncated ? 1 : 0);
        command.Parameters.AddWithValue("$source", (object?)row.SourceRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$arm", (object?)row.Arm ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)row.SessionId ?? DBNull.Value);
        var stored = await command.ExecuteNonQueryAsync(ct);
        if (row.Corrects is null)
            return stored;
        // A reason or asked_again the client already reported for the previous request wins over the rules.
        await using var correct = connection.CreateCommand();
        correct.CommandText = """
            UPDATE requests SET correction_reason = COALESCE(correction_reason, $reason), asked_again = COALESCE(asked_again, 1),
              feedback_at = COALESCE(feedback_at, $at)
            WHERE request_id = (
              SELECT request_id FROM requests WHERE session_id = $session AND created_at < $at AND request_id <> $id
              ORDER BY created_at DESC, request_id DESC LIMIT 1)
            """;
        correct.Parameters.AddWithValue("$reason", row.Corrects);
        correct.Parameters.AddWithValue("$session", row.SessionId!);
        correct.Parameters.AddWithValue("$at", Text(row.CreatedAt));
        correct.Parameters.AddWithValue("$id", row.RequestId);
        return stored + await correct.ExecuteNonQueryAsync(ct);
    }, ct);

    public async Task<bool> RecordOutcomeAsync(OutcomeRow outcome, CancellationToken ct) => await UseAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE requests SET rewrite_accepted = COALESCE($accepted, rewrite_accepted), model_overridden = COALESCE($overridden, model_overridden),
              score = COALESCE($score, score), cost_usd = COALESCE($cost, cost_usd), model = COALESCE($model, model), feedback_at = $at,
              completed = COALESCE($completed, completed), tokens_in = COALESCE($tin, tokens_in), tokens_out = COALESCE($tout, tokens_out),
              cache_read_tokens = COALESCE($cread, cache_read_tokens), cache_write_tokens = COALESCE($cwrite, cache_write_tokens),
              duration_ms = COALESCE($duration, duration_ms), tool_calls = COALESCE($tools, tool_calls),
              asked_again = COALESCE($asked, asked_again), effort = COALESCE($effort, effort),
              commits = COALESCE($commits, commits), pr_merged = COALESCE($merged, pr_merged), ci_first_push_green = COALESCE($green, ci_first_push_green),
              reverted = COALESCE($reverted, reverted), item_closed = COALESCE($closed, item_closed), correction_reason = COALESCE($reason, correction_reason),
              review_findings = COALESCE($findings, review_findings), review_findings_severe = COALESCE($severe, review_findings_severe),
              person_ms = COALESCE($person, person_ms), rewritten_prompt = COALESCE($rewrite, rewritten_prompt)
            WHERE request_id = $id
            """;
        command.Parameters.AddWithValue("$id", outcome.RequestId);
        command.Parameters.AddWithValue("$at", Text(outcome.At));
        command.Parameters.AddWithValue("$accepted", outcome.RewriteAccepted is { } a ? a ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$overridden", outcome.ModelOverridden is { } o ? o ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$score", (object?)outcome.Score ?? DBNull.Value);
        command.Parameters.AddWithValue("$cost", outcome.CostUsd is { } c ? (double)c : DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)outcome.Model ?? DBNull.Value);
        // A later report adds what it has and never erases what an earlier report gave: delayed outcomes arrive in a report of their own.
        command.Parameters.AddWithValue("$completed", outcome.Completed is { } d ? d ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$tin", (object?)outcome.TokensIn ?? DBNull.Value);
        command.Parameters.AddWithValue("$tout", (object?)outcome.TokensOut ?? DBNull.Value);
        command.Parameters.AddWithValue("$cread", (object?)outcome.CacheReadTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$cwrite", (object?)outcome.CacheWriteTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)outcome.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$tools", (object?)outcome.ToolCalls ?? DBNull.Value);
        command.Parameters.AddWithValue("$asked", outcome.AskedAgain is { } k ? k ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$effort", (object?)outcome.Effort ?? DBNull.Value);
        command.Parameters.AddWithValue("$commits", (object?)outcome.Commits ?? DBNull.Value);
        command.Parameters.AddWithValue("$merged", Flag(outcome.PrMerged));
        command.Parameters.AddWithValue("$green", Flag(outcome.CiFirstPushGreen));
        command.Parameters.AddWithValue("$reverted", Flag(outcome.Reverted));
        command.Parameters.AddWithValue("$closed", Flag(outcome.ItemClosed));
        command.Parameters.AddWithValue("$reason", (object?)outcome.CorrectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$findings", (object?)outcome.ReviewFindings ?? DBNull.Value);
        command.Parameters.AddWithValue("$severe", (object?)outcome.ReviewFindingsSevere ?? DBNull.Value);
        command.Parameters.AddWithValue("$person", (object?)outcome.PersonMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$rewrite", (object?)outcome.RewrittenPrompt ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }, ct) > 0;

    public async Task<IReadOnlyList<(string? TaskKind, double Score)>> ScoresAsync(CancellationToken ct) => await UseAsync<IReadOnlyList<(string?, double)>>(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_kind, score FROM requests WHERE score IS NOT NULL";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var scores = new List<(string?, double)>();
        while (await reader.ReadAsync(ct))
            scores.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetDouble(1)));
        return scores;
    }, ct);

    public async Task<IReadOnlyList<Candidate>> SearchAsync(IReadOnlyList<string> terms, int limit, CancellationToken ct) => await UseAsync<IReadOnlyList<Candidate>>(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.request_id, r.created_at, r.prompt, r.repository, r.task_kind, r.score, -bm25(requests_fts)
            FROM requests_fts JOIN requests r ON r.rowid = requests_fts.rowid
            WHERE requests_fts MATCH $match AND r.score IS NOT NULL AND (r.model_overridden IS NULL OR r.model_overridden = 0)
            ORDER BY bm25(requests_fts) LIMIT $limit
            """;
        command.Parameters.AddWithValue("$match", Words.MatchAny(terms));
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = new List<Candidate>();
        while (await reader.ReadAsync(ct))
            found.Add(new Candidate(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDouble(5), reader.GetDouble(6)));
        return found;
    }, ct);

    public async Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct) => await UseAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT template_id, version, kind, before_text, after_text, source, role, created_at FROM templates WHERE kind = $kind AND role IN ('champion', 'challenger')";
        command.Parameters.AddWithValue("$kind", kind);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Template? champion = null;
        var challengers = new List<(Template Template, string Order)>();
        while (await reader.ReadAsync(ct))
        {
            var template = new Template(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
            if (reader.GetString(6) == TemplateRoles.Champion)
                champion = template;
            else
                challengers.Add((template, reader.GetString(7)));
        }
        return new KindTemplates(champion, [.. challengers.OrderBy(c => c.Order, StringComparer.Ordinal).ThenBy(c => c.Template.Id, StringComparer.Ordinal).Select(c => c.Template)]);
    }, ct);

    public async Task<IReadOnlyDictionary<string, Tally>> TalliesAsync(string kind, CancellationToken ct) => await UseAsync<IReadOnlyDictionary<string, Tally>>(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT template_id, template_version, completed, asked_again, correction_reason FROM requests
            WHERE task_kind = $kind AND arm IN ('champion', 'challenger') AND template_id IS NOT NULL AND completed IS NOT NULL
            """;
        command.Parameters.AddWithValue("$kind", kind);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var reason = reader.IsDBNull(4) ? null : reader.GetString(4);
            var rework = reason is not null ? CorrectionReasons.IsRework(reason) : !reader.IsDBNull(3) && reader.GetInt64(3) != 0;
            var key = Allocation.Key(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            var tally = tallies.GetValueOrDefault(key);
            tallies[key] = reader.GetInt64(2) != 0 && !rework ? tally with { Wins = tally.Wins + 1 } : tally with { Losses = tally.Losses + 1 };
        }
        return tallies;
    }, ct);

    public async Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct) => await UseAsync(async connection =>
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var template in templates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // OR IGNORE: a version already stored keeps its text and role, and a kind that has a champion keeps it. A seeded challenger
            // is added only to a kind with none, so a release's built-in one does not join a challenger the owner is already trying.
            command.CommandText = """
                INSERT OR IGNORE INTO templates (template_id, version, kind, before_text, after_text, source, role, created_at)
                SELECT $id, $version, $kind, $before, $after, $source, $role, $at
                WHERE $role <> 'challenger' OR NOT EXISTS (SELECT 1 FROM templates WHERE kind = $kind AND role = 'challenger')
                """;
            command.Parameters.AddWithValue("$id", template.Id);
            command.Parameters.AddWithValue("$version", template.Version);
            command.Parameters.AddWithValue("$kind", template.Kind);
            command.Parameters.AddWithValue("$before", template.Before);
            command.Parameters.AddWithValue("$after", template.After);
            command.Parameters.AddWithValue("$source", template.Source);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$at", Text(TimeProvider.System.GetUtcNow()));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return 0;
    }, ct);

    private static object Flag(bool? value) => value is { } v ? v ? 1 : 0 : DBNull.Value;

    private static string Text(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="work"/> on an open connection, alone: the file has one writer at a time.</summary>
    private async Task<T> UseAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_ready)
                CreateFile();
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            await using (var busy = connection.CreateCommand())
            {
                busy.CommandText = $"PRAGMA busy_timeout = {BusyMilliseconds}; {StoreSchema.Pragmas}";
                await busy.ExecuteNonQueryAsync(ct);
            }
            if (!_ready)
            {
                await StoreSchema.EnsureAsync(connection, ct);
                _ready = true;
            }
            return await work(connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The directory and the file exist, readable by the owner only, before SQLite opens them.</summary>
    private void CreateFile()
    {
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
            // No Unix modes there; the user profile's own access rules apply.
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, OwnerDirectory);
        if (File.Exists(path))
            return;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerFile;
        using var created = new FileStream(path, options);
    }
}
