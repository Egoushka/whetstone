using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Whetstone.Storage;

/// <summary>
/// The file's tables and how an older file becomes the current one. Version 2 adds <c>source_request_id</c> (which stored request a
/// retrieval drew on) and a full-text index over the prompt. Version 3 adds the run measures <c>feedback</c> can report. The index holds no text of its own: it points at <c>requests</c>, so a
/// row deleted there is deleted from the index in the same statement, and <c>forget</c> rebuilds it before the file is compacted.
/// </summary>
internal static class StoreSchema
{
    public const int Version = 3;

    /// <summary>The oldest file the commands can still read; the server upgrades it on first use.</summary>
    public const int OldestReadable = 1;

    /// <summary>The columns version 3 added, for a file that predates them.</summary>
    private static readonly (string Column, string Type)[] Measures =
    [
        ("completed", "INTEGER"), ("tokens_in", "INTEGER"), ("tokens_out", "INTEGER"), ("cache_read_tokens", "INTEGER"),
        ("cache_write_tokens", "INTEGER"), ("duration_ms", "INTEGER"), ("tool_calls", "INTEGER"), ("asked_again", "INTEGER"), ("effort", "TEXT"),
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
          effort TEXT
        );
        CREATE INDEX IF NOT EXISTS requests_by_repository ON requests (repository, created_at);
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
