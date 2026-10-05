using Whetstone.Contracts;

namespace Whetstone.Retrieval;

/// <summary>
/// Which stored requests went well (docs/specs/2026-10-05-it-retrieves-design.md, decision 2): scored, not overridden, and at or
/// above the median score of their task kind. A kind with fewer than <see cref="MinScoredForKind"/> scored requests uses the
/// median of all scored requests, because a median of three says nothing.
/// </summary>
public static class Eligibility
{
    public const int MinScoredForKind = 10;

    public static IReadOnlyList<ExportRecord> Eligible(IReadOnlyList<ExportRecord> rows)
    {
        var scored = rows.Where(r => r.Outcome?.Score is not null).ToList();
        if (scored.Count == 0)
            return [];
        var overall = Median(scored);
        var byKind = scored
            .GroupBy(r => r.Context.TaskKind ?? "", StringComparer.Ordinal)
            .Where(g => g.Count() >= MinScoredForKind)
            .ToDictionary(g => g.Key, Median, StringComparer.Ordinal);
        return scored
            .Where(r => r.Outcome!.ModelOverridden != true)
            .Where(r => r.Outcome!.Score >= byKind.GetValueOrDefault(r.Context.TaskKind ?? "", overall))
            .ToList();
    }

    private static double Median(IEnumerable<ExportRecord> scored)
    {
        var sorted = scored.Select(r => r.Outcome!.Score!.Value).Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
