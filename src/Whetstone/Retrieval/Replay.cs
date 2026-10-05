using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Whetstone.Contracts;

namespace Whetstone.Retrieval;

/// <summary>The best earlier request for one scored request; <paramref name="Match"/> is null when nothing shares a word.</summary>
/// <param name="Score">BM25 relevance, higher is closer; 0 with no match. It grows with the length of the query, so compare it across pairs only roughly.</param>
public sealed record ReplayPair(ExportRecord Query, ExportRecord? Match, double Score);

public sealed record ReplayReport(int Total, int Scored, int Eligible, IReadOnlyList<ReplayPair> Pairs);

/// <summary>
/// For every scored request, the eligible request that ranks first for it by BM25 (SQLite FTS5), so the owner can read real
/// pairs and choose a threshold. Works on an in-memory copy: the store's file is not touched.
/// </summary>
public static partial class Replay
{
    private const int MaxTerms = 64;

    public static ReplayReport Run(IReadOnlyList<ExportRecord> rows)
    {
        var scored = rows.Where(r => r.Outcome?.Score is not null).ToList();
        var eligible = Eligibility.Eligible(rows);
        var byId = eligible.ToDictionary(r => r.RequestId, StringComparer.Ordinal);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE docs USING fts5(prompt, request_id UNINDEXED)";
            create.ExecuteNonQuery();
        }
        foreach (var row in eligible)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO docs (prompt, request_id) VALUES ($prompt, $id)";
            insert.Parameters.AddWithValue("$prompt", Words(row.Prompt));
            insert.Parameters.AddWithValue("$id", row.RequestId);
            insert.ExecuteNonQuery();
        }

        var pairs = scored.Select(query => Best(connection, query, byId)).ToList();
        return new ReplayReport(rows.Count, scored.Count, eligible.Count, pairs);
    }

    private static ReplayPair Best(SqliteConnection connection, ExportRecord query, Dictionary<string, ExportRecord> byId)
    {
        var terms = Terms(query.Prompt);
        if (terms.Length == 0)
            return new ReplayPair(query, null, 0);
        using var command = connection.CreateCommand();
        // A request never matches itself, and an identical prompt adds nothing (decision 7).
        command.CommandText = """
            SELECT request_id, -bm25(docs) FROM docs
            WHERE docs MATCH $match AND request_id <> $id AND prompt <> $words
            ORDER BY bm25(docs) LIMIT 1
            """;
        command.Parameters.AddWithValue("$match", string.Join(" OR ", terms.Select(t => $"\"{t}\"")));
        command.Parameters.AddWithValue("$id", query.RequestId);
        command.Parameters.AddWithValue("$words", Words(query.Prompt));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ReplayPair(query, byId[reader.GetString(0)], reader.GetDouble(1)) : new ReplayPair(query, null, 0);
    }

    /// <summary>The prompt as the words the index sees: lower case, redaction markers removed so they never match each other.</summary>
    private static string Words(string prompt) => string.Join(' ', WordPattern().Matches(Marker().Replace(prompt, " ")).Select(m => m.Value.ToLowerInvariant()));

    private static string[] Terms(string prompt) => [.. Words(prompt).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).Distinct(StringComparer.Ordinal).Take(MaxTerms)];

    [GeneratedRegex(@"\[REDACTED:[a-z-]+\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordPattern();
}
