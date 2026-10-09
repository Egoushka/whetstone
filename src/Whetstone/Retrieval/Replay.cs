using Microsoft.Data.Sqlite;
using Whetstone.Contracts;

namespace Whetstone.Retrieval;

/// <summary>The best earlier request for one scored request; <paramref name="Match"/> is null when nothing shares a word.</summary>
/// <param name="Score">BM25 relevance, higher is closer; 0 with no match. It grows with the length of the query, so compare it across pairs only roughly.</param>
public sealed record ReplayPair(ExportRecord Query, ExportRecord? Match, double Score);

/// <param name="MinPromptChars">The length below which requests were left out on both sides, as the retriever would.</param>
public sealed record ReplayReport(int Total, int Scored, int Eligible, IReadOnlyList<ReplayPair> Pairs, int MinPromptChars = 0);

/// <summary>
/// For every scored request, the eligible request that ranks first for it by BM25 (SQLite FTS5), so the owner can read real
/// pairs and choose a threshold. Works on an in-memory copy: the store's file is not touched.
/// </summary>
public static class Replay
{
    public static ReplayReport Run(IReadOnlyList<ExportRecord> rows, int minPromptChars = 0)
    {
        // The median is taken over every scored request, as the retriever does; the length only decides what takes part.
        var scored = rows.Where(r => r.Outcome?.Score is not null && r.Prompt.Length >= minPromptChars).ToList();
        var eligible = Eligibility.Eligible(rows).Where(r => r.Prompt.Length >= minPromptChars).ToList();
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
            insert.Parameters.AddWithValue("$prompt", Words.Normalise(row.Prompt));
            insert.Parameters.AddWithValue("$id", row.RequestId);
            insert.ExecuteNonQuery();
        }

        var pairs = scored.Select(query => Best(connection, query, byId)).ToList();
        return new ReplayReport(rows.Count, scored.Count, eligible.Count, pairs, minPromptChars);
    }

    private static ReplayPair Best(SqliteConnection connection, ExportRecord query, Dictionary<string, ExportRecord> byId)
    {
        var terms = Words.Terms(query.Prompt);
        if (terms.Length == 0)
            return new ReplayPair(query, null, 0);
        using var command = connection.CreateCommand();
        // A request never matches itself, and an identical prompt adds nothing (decision 7).
        command.CommandText = """
            SELECT request_id, -bm25(docs) FROM docs
            WHERE docs MATCH $match AND request_id <> $id AND prompt <> $words
            ORDER BY bm25(docs) LIMIT 1
            """;
        command.Parameters.AddWithValue("$match", Words.MatchAny(terms));
        command.Parameters.AddWithValue("$id", query.RequestId);
        command.Parameters.AddWithValue("$words", Words.Normalise(query.Prompt));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ReplayPair(query, byId[reader.GetString(0)], reader.GetDouble(1)) : new ReplayPair(query, null, 0);
    }
}
