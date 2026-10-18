using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Whetstone.Storage;

/// <summary>
/// The file's tables and how an older file becomes the current one. Version 2 adds <c>source_request_id</c> (which stored request a
/// retrieval drew on) and a full-text index over the prompt. Version 3 adds the run measures <c>feedback</c> can report. Version 4 adds the arm a request was given and the template store. Version 5 adds the session id and the reward measures (docs/specs/2026-10-19-reward-signals-design.md). Version 6 adds the owner's hand label, the template decision log and the replay gym's tables (ADR 0006). Version 7 lets a kind have several challengers. The index holds no text of its own: it points at <c>requests</c>, so a
/// row deleted there is deleted from the index in the same statement, and <c>forget</c> rebuilds it before the file is compacted.
/// </summary>
internal static class StoreSchema
{
    public const int Version = 7;

    /// <summary>The oldest file the commands can still read; the server upgrades it on first use.</summary>
    public const int OldestReadable = 1;

    /// <summary>The columns version 3 added, for a file that predates them.</summary>
    private static readonly (string Column, string Type)[] Measures =
    [
        ("completed", "INTEGER"), ("tokens_in", "INTEGER"), ("tokens_out", "INTEGER"), ("cache_read_tokens", "INTEGER"),
        ("cache_write_tokens", "INTEGER"), ("duration_ms", "INTEGER"), ("tool_calls", "INTEGER"), ("asked_again", "INTEGER"), ("effort", "TEXT"),
    ];

    /// <summary>The columns version 5 added, for a file that predates them.</summary>
    private static readonly (string Column, string Type)[] Rewards =
    [
        ("session_id", "TEXT"), ("commits", "INTEGER"), ("pr_merged", "INTEGER"), ("ci_first_push_green", "INTEGER"), ("reverted", "INTEGER"),
        ("item_closed", "INTEGER"), ("correction_reason", "TEXT"), ("review_findings", "INTEGER"), ("review_findings_severe", "INTEGER"), ("person_ms", "INTEGER"),
    ];

    private const string Requests = """
        CREATE TABLE IF NOT EXISTS requests (
          request_id TEXT PRIMARY KEY,
          created_at TEXT NOT NULL,
          prompt TEXT NOT NULL,
          repository TEXT,
          commit_sha TEXT,
          task_kind TEXT,
          client TEXT,
          changed INTEGER NOT NULL,
          template_id TEXT,
          template_version TEXT,
          held_out INTEGER NOT NULL,
          truncated INTEGER NOT NULL,
          rewrite_accepted INTEGER,
          model_overridden INTEGER,
          score REAL,
          cost_usd REAL,
          model TEXT,
          feedback_at TEXT,
          source_request_id TEXT,
          completed INTEGER,
          tokens_in INTEGER,
          tokens_out INTEGER,
          cache_read_tokens INTEGER,
          cache_write_tokens INTEGER,
          duration_ms INTEGER,
          tool_calls INTEGER,
          asked_again INTEGER,
          effort TEXT,
          arm TEXT,
          session_id TEXT,
          commits INTEGER,
          pr_merged INTEGER,
          ci_first_push_green INTEGER,
          reverted INTEGER,
          item_closed INTEGER,
          correction_reason TEXT,
          review_findings INTEGER,
          review_findings_severe INTEGER,
          person_ms INTEGER,
          hand_label INTEGER
        );
        CREATE INDEX IF NOT EXISTS requests_by_repository ON requests (repository, created_at);
        """;

    // At most one champion per kind, held by the index so no code path can break it; challengers may be several (goal 0.7, version 6). A version's text is never
    // changed; only its role is.
    private const string Templates = """
        CREATE TABLE IF NOT EXISTS templates (
          template_id TEXT NOT NULL,
          version TEXT NOT NULL,
          kind TEXT NOT NULL,
          before_text TEXT NOT NULL,
          after_text TEXT NOT NULL,
          source TEXT NOT NULL,
          role TEXT NOT NULL,
          created_at TEXT NOT NULL,
          PRIMARY KEY (template_id, version, kind)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS templates_one_champion ON templates (kind) WHERE role = 'champion';
        DROP INDEX IF EXISTS templates_one_challenger;
        """;

    // What the owner did to a template and the counts the report showed when it was done.
    private const string Decisions = """
        CREATE TABLE IF NOT EXISTS template_decisions (
          decision_id INTEGER PRIMARY KEY,
          decided_at TEXT NOT NULL,
          kind TEXT NOT NULL,
          template_id TEXT NOT NULL,
          version TEXT NOT NULL,
          action TEXT NOT NULL,
          counts TEXT,
          note TEXT
        );
        """;

    // The replay gym (ADR 0006): cases point at stored requests and keep a hash, never a copy of the prompt; runs and verdicts never
    // feed a live trial.
    private const string Gym = """
        CREATE TABLE IF NOT EXISTS gym_cases (
          case_id TEXT PRIMARY KEY,
          source_request_id TEXT NOT NULL,
          repository TEXT NOT NULL,
          commit_sha TEXT NOT NULL,
          task_kind TEXT NOT NULL,
          prompt_hash TEXT NOT NULL,
          pinned INTEGER NOT NULL DEFAULT 0,
          selected_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS gym_runs (
          run_id TEXT PRIMARY KEY,
          case_id TEXT NOT NULL,
          arm TEXT NOT NULL,
          template_id TEXT,
          template_version TEXT,
          client TEXT NOT NULL,
          client_run_id TEXT,
          preset TEXT,
          model TEXT,
          effort TEXT,
          completed INTEGER,
          status TEXT,
          tokens_in INTEGER,
          tokens_out INTEGER,
          cache_read_tokens INTEGER,
          cache_write_tokens INTEGER,
          cost_usd REAL,
          duration_ms INTEGER,
          evidence_resolved REAL,
          started_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS gym_runs_by_case ON gym_runs (case_id, arm);
        CREATE TABLE IF NOT EXISTS gym_verdicts (
          case_id TEXT NOT NULL,
          template_id TEXT NOT NULL,
          template_version TEXT NOT NULL,
          judge_model TEXT NOT NULL,
          winner TEXT NOT NULL,
          order_flipped INTEGER NOT NULL,
          judge_cost_usd REAL,
          decided_at TEXT NOT NULL,
          PRIMARY KEY (case_id, template_id, template_version, judge_model)
        );
        """;

    /// <summary>Gym rows whose request is gone; run after a delete so <c>forget</c> leaves none behind (ADR 0006).</summary>
    public const string ForgetOrphanedGymRows = """
        DELETE FROM gym_verdicts WHERE case_id IN (SELECT case_id FROM gym_cases WHERE source_request_id NOT IN (SELECT request_id FROM requests));
        DELETE FROM gym_runs WHERE case_id IN (SELECT case_id FROM gym_cases WHERE source_request_id NOT IN (SELECT request_id FROM requests));
        DELETE FROM gym_cases WHERE source_request_id NOT IN (SELECT request_id FROM requests);
        """;

    // Only an insert and a delete need a trigger: no statement changes a stored prompt. A replaced row (INSERT OR REPLACE) deletes
    // the old one, which fires the delete trigger only while recursive_triggers is on (see Pragmas).
    private const string Index = """
        CREATE VIRTUAL TABLE IF NOT EXISTS requests_fts USING fts5(prompt, content = 'requests', content_rowid = 'rowid');
        CREATE TRIGGER IF NOT EXISTS requests_fts_insert AFTER INSERT ON requests BEGIN
          INSERT INTO requests_fts (rowid, prompt) VALUES (new.rowid, new.prompt);
        END;
        CREATE TRIGGER IF NOT EXISTS requests_fts_delete AFTER DELETE ON requests BEGIN
          INSERT INTO requests_fts (requests_fts, rowid, prompt) VALUES ('delete', old.rowid, old.prompt);
        END;
        """;

    public const string Pragmas = "PRAGMA recursive_triggers = ON";

    public const string RebuildIndex = "INSERT INTO requests_fts (requests_fts) VALUES ('rebuild')";

    public static async Task<int> VersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Creates a new file's tables or upgrades an older one, all or nothing: a failure leaves the old file as it was.</summary>
    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken ct)
    {
        var found = await VersionAsync(connection, ct);
        if (found > Version)
            throw new InvalidOperationException($"the store is schema version {found}; this whetstone knows {Version}");
        if (found == Version)
            return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await Run(connection, transaction, Requests, ct);
        if (found == 1)
            await Run(connection, transaction, "ALTER TABLE requests ADD COLUMN source_request_id TEXT", ct);
        if (found is 1 or 2)
            foreach (var (column, type) in Measures)
                await Run(connection, transaction, $"ALTER TABLE requests ADD COLUMN {column} {type}", ct);
        if (found is 1 or 2 or 3)
            await Run(connection, transaction, "ALTER TABLE requests ADD COLUMN arm TEXT", ct);
        if (found is 1 or 2 or 3 or 4)
            foreach (var (column, type) in Rewards)
                await Run(connection, transaction, $"ALTER TABLE requests ADD COLUMN {column} {type}", ct);
        if (found is >= 1 and <= 5)
            await Run(connection, transaction, "ALTER TABLE requests ADD COLUMN hand_label INTEGER", ct);
        await Run(connection, transaction, "CREATE INDEX IF NOT EXISTS requests_by_session ON requests (session_id, created_at)", ct);
        await Run(connection, transaction, Decisions, ct);
        await Run(connection, transaction, Gym, ct);
        await Run(connection, transaction, Templates, ct);
        await Run(connection, transaction, Index, ct);
        await Run(connection, transaction, RebuildIndex, ct);
        await Run(connection, transaction, $"PRAGMA user_version = {Version}", ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task Run(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
