using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Whetstone.Contracts;

namespace Whetstone.Storage;

/// <summary>Reads, counts and deletes in a user's existing file. The server may be writing to it: waits are bounded, not skipped.</summary>
internal sealed class SqliteStoreAdmin(string path) : IStoreAdmin
{
    private const int SchemaVersion = 1;
    private const int BusyMilliseconds = 5_000;

    private const string Where = "WHERE ($repository IS NULL OR repository = $repository) AND ($before IS NULL OR created_at < $before)";

    public async Task<int> CountAsync(RowFilter filter, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM requests {Where}";
        Bind(command, filter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<ExportRecord> ExportAsync(RowFilter filter, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(path))
            yield break;
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT request_id, created_at, prompt, truncated, repository, commit_sha, task_kind, client, changed, template_id, template_version, held_out,
                   rewrite_accepted, model_overridden, score, cost_usd, model, feedback_at
            FROM requests {Where} ORDER BY created_at, request_id
            """;
        Bind(command, filter);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var reported = reader.IsDBNull(17) ? null : reader.GetString(17);
            yield return new ExportRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0,
                new ExportContext(Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7)),
                new ExportAnswer(reader.GetInt64(8) != 0, Text(reader, 9), Text(reader, 10), reader.GetInt64(11) != 0),
                reported is null
                    ? null
                    : new ExportOutcome(Flag(reader, 12), Flag(reader, 13), Number(reader, 14), Number(reader, 15), Text(reader, 16), reported));
        }
    }

    public async Task<int> ForgetAsync(RowFilter filter, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        // Deleted text is overwritten with zeros, the rebuild below drops the pages, and nothing goes to a temp file.
        foreach (var pragma in new[] { "PRAGMA secure_delete = ON", "PRAGMA temp_store = MEMORY" })
        {
            await using var set = connection.CreateCommand();
            set.CommandText = pragma;
            await set.ExecuteNonQueryAsync(ct);
        }
        int deleted;
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM requests {Where}";
            Bind(delete, filter);
            deleted = await delete.ExecuteNonQueryAsync(ct);
        }
        if (deleted > 0)
        {
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            await vacuum.ExecuteNonQueryAsync(ct);
        }
        return deleted;
    }

    private async Task<SqliteConnection> OpenAsync(SqliteOpenMode mode, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            await using var busy = connection.CreateCommand();
            busy.CommandText = $"PRAGMA busy_timeout = {BusyMilliseconds}";
            await busy.ExecuteNonQueryAsync(ct);
            await using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var found = Convert.ToInt32(await version.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (found != SchemaVersion)
                throw new InvalidOperationException($"the store is schema version {found}; this whetstone reads {SchemaVersion}");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void Bind(SqliteCommand command, RowFilter filter)
    {
        command.Parameters.AddWithValue("$repository", (object?)filter.Repository ?? DBNull.Value);
        command.Parameters.AddWithValue("$before", (object?)filter.BeforeText ?? DBNull.Value);
    }

    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static bool? Flag(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i) != 0;

    private static double? Number(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);
}
