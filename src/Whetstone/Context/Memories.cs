using System.Security.Cryptography;
using System.Text;

namespace Whetstone.Context;

/// <summary>One recalled memory. <paramref name="DocumentKey"/> names its source document, which outlives a rewritten memory id.</summary>
public sealed record Memory(string Id, string DocumentKey, string Text, double Score);

/// <summary>A memory service the owner runs (goal 0.9). Any failure is an empty list, never an exception the client sees.</summary>
public interface IMemorySource
{
    Task<IReadOnlyList<Memory>> RecallAsync(string query, string? repository, int max, CancellationToken ct);
}

/// <summary>How the memories from one document have done: requests they were shown to, and of those, runs that completed without rework.</summary>
public sealed record MemoryRecord(int Shown, int Helped);

/// <summary>What a request was shown, kept so outcomes can be credited to documents later.</summary>
public sealed record MemoryExposure(string DocumentKey, string MemoryId, string TextHash, int Rank, double Score);

/// <summary>whetstone's own record of which memories helped (the memory service has no place for it): read to re-rank, written per request.</summary>
public interface IMemoryLedger
{
    Task<IReadOnlyDictionary<string, MemoryRecord>> MemoryRecordsAsync(IReadOnlyCollection<string> documentKeys, CancellationToken ct);

    Task RecordExposuresAsync(string requestId, IReadOnlyList<MemoryExposure> shown, CancellationToken ct);
}

/// <summary>
/// The memory slot's ranking (docs/specs/2026-10-19-context-slots-design.md, decision 1): over-fetch, weight each memory by how its
/// document has done, drop documents that never help. A document's weight moves only after <see cref="MinShown"/> exposures, so one
/// lucky or unlucky run does not decide it.
/// </summary>
public static class MemoryRanking
{
    public const int SlotSize = 5;

    public const int MinShown = 5;

    public const int DropAfter = 10;

    public const double NoiseRate = 0.2;

    /// <summary>A document's weight: 1 until it has <see cref="MinShown"/> exposures, then its smoothed help rate against an even prior; 0 when it is noise.</summary>
    public static double Weight(MemoryRecord? record)
    {
        if (record is null || record.Shown < MinShown)
            return 1;
        var rate = (record.Helped + 1.0) / (record.Shown + 2.0);
        return record.Shown >= DropAfter && rate < NoiseRate ? 0 : rate / 0.5;
    }

    public static IReadOnlyList<Memory> Rank(IReadOnlyList<Memory> recalled, IReadOnlyDictionary<string, MemoryRecord> records, int size = SlotSize) => [.. recalled
        .GroupBy(m => m.DocumentKey, StringComparer.Ordinal).Select(g => g.OrderByDescending(m => m.Score).First())
        .Select(m => (Memory: m, Weighted: m.Score * Weight(records.GetValueOrDefault(m.DocumentKey))))
        .Where(x => x.Weighted > 0)
        .OrderByDescending(x => x.Weighted)
        .Take(size)
        .Select(x => x.Memory)];

    /// <summary>
    /// The slot's text, inside a marker a session-retain job can strip, so memories shown to an agent are not stored again as new facts.
    /// </summary>
    public static string Block(IReadOnlyList<Memory> memories) => memories.Count == 0 ? "" :
        "<whetstone_memory>\n" + string.Join("\n", memories.Select(m => "- " + m.Text.ReplaceLineEndings(" ").Trim())) + "\n</whetstone_memory>";

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}
