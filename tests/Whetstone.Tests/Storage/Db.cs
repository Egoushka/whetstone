using Microsoft.Data.Sqlite;

namespace Whetstone.Tests.Storage;

/// <summary>A throwaway data directory, and a way to look into a store's file without going through the store.</summary>
internal sealed class TempData : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("whetstone-test-").FullName;

    public string FileFor(string user) => System.IO.Path.Combine(Path, user, "whetstone.db");

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class Db
{
    public static List<Dictionary<string, object?>> Query(string file, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(i => reader.GetName(i), i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        return rows;
    }

    public static List<Dictionary<string, object?>> Requests(string file) => Query(file, "SELECT * FROM requests ORDER BY created_at, request_id");

    /// <summary>Every byte the store left on disk for this file, so a test can look for text that must not be there.</summary>
    public static string Everything(string file)
    {
        var bytes = File.ReadAllBytes(file);
        foreach (var sibling in new[] { file + "-journal", file + "-wal" }.Where(File.Exists))
            bytes = [.. bytes, .. File.ReadAllBytes(sibling)];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
