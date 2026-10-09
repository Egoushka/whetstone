using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Whetstone.Contracts;

namespace Whetstone.Storage;

/// <summary>Reads, counts and deletes in a user's existing file. The server may be writing to it: waits are bounded, not skipped.</summary>
internal sealed class SqliteStoreAdmin(string path) : IStoreAdmin
{
    private const int BusyMilliseconds = 5_000;

    /// <summary>The prefix of every id an import gives a past prompt (Whetstone.Import.Importer).</summary>
    public const string ImportedPrefix = "imp-";

    private const string Where = "WHERE ($repository IS NULL OR repository = $repository) AND ($before IS NULL OR created_at < $before) AND ($text IS NULL OR prompt_matches(prompt))";

    public async Task<int> CountAsync(RowFilter filter, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        var (connection, _) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM requests {Where}";
        Bind(command, filter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<ExportRecord> ExportAsync(RowFilter filter, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(path))
            yield break;
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        await using var command = connection.CreateCommand();
        // A version 1 file has no source column yet; the server adds it the next time it opens the file.
        command.CommandText = $"""
            SELECT request_id, created_at, prompt, truncated, repository, commit_sha, task_kind, client, changed, template_id, template_version, held_out,
                   rewrite_accepted, model_overridden, score, cost_usd, model, feedback_at, {(version >= 2 ? "source_request_id" : "NULL")}
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
                new ExportAnswer(reader.GetInt64(8) != 0, Text(reader, 9), Text(reader, 10), reader.GetInt64(11) != 0, Text(reader, 18)),
                reported is null
                    ? null
                    : new ExportOutcome(Flag(reader, 12), Flag(reader, 13), Number(reader, 14), Number(reader, 15), Text(reader, 16), reported));
        }
    }

    public async Task<int> ForgetAsync(RowFilter filter, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
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
            // The index kept the deleted words until it is rebuilt from what is left; do that before the pages are rewritten.
            if (version >= 2)
            {
                await using var rebuild = connection.CreateCommand();
                rebuild.CommandText = StoreSchema.RebuildIndex;
                await rebuild.ExecuteNonQueryAsync(ct);
            }
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            await vacuum.ExecuteNonQueryAsync(ct);
        }
        return deleted;
    }

    public async Task<string?> FindAsync(string importedId, string client, string prompt, DateTimeOffset at, TimeSpan within, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        var (connection, _) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id FROM requests
            WHERE request_id = $self
               OR (substr(request_id, 1, length($prefix)) <> $prefix
                   AND client = $client AND prompt = $prompt AND created_at >= $from AND created_at <= $to)
            ORDER BY request_id = $self DESC, created_at, request_id LIMIT 1
            """;
        command.Parameters.AddWithValue("$self", importedId);
        command.Parameters.AddWithValue("$prefix", ImportedPrefix);
        command.Parameters.AddWithValue("$client", client);
        command.Parameters.AddWithValue("$prompt", prompt);
        command.Parameters.AddWithValue("$from", (at - within).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", (at + within).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<int> ReindexAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        var (connection, _) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        // Upgrades a version 1 file first, then rebuilds the index from the rows.
        await StoreSchema.EnsureAsync(connection, ct);
        await using var rebuild = connection.CreateCommand();
        rebuild.CommandText = StoreSchema.RebuildIndex;
        await rebuild.ExecuteNonQueryAsync(ct);
        return await CountAsync(RowFilter.Everything, ct);
    }

    private async Task<(SqliteConnection Connection, int Version)> OpenAsync(SqliteOpenMode mode, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            await using var busy = connection.CreateCommand();
            busy.CommandText = $"PRAGMA busy_timeout = {BusyMilliseconds}";
            await busy.ExecuteNonQueryAsync(ct);
            var found = await StoreSchema.VersionAsync(connection, ct);
            if (found < StoreSchema.OldestReadable || found > StoreSchema.Version)
                throw new InvalidOperationException($"the store is schema version {found}; this whetstone reads {StoreSchema.OldestReadable} to {StoreSchema.Version}");
            return (connection, found);
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
        command.Parameters.AddWithValue("$text", filter.Text is null ? DBNull.Value : 1);
        // SQLite has no regular expressions; the filter's own expression runs in .NET, row by row.
        var text = filter.Text;
        command.Connection!.CreateFunction("prompt_matches", (string prompt) => text is not null && text.IsMatch(prompt), isDeterministic: true);
    }

    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static bool? Flag(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i) != 0;

    private static double? Number(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);
}
