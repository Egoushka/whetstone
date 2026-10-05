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
        var bar = Thresholds.From(scored.Select(r => (r.Context.TaskKind, r.Outcome!.Score!.Value)));
        return scored
            .Where(r => r.Outcome!.ModelOverridden != true)
            .Where(r => bar.Passes(r.Context.TaskKind, r.Outcome!.Score!.Value))
            .ToList();
    }
}

/// <summary>The score a request must reach to count as having gone well, per task kind.</summary>
public sealed class Thresholds
{
    private readonly double _overall;
    private readonly Dictionary<string, double> _byKind;

    private Thresholds(double overall, Dictionary<string, double> byKind)
    {
        _overall = overall;
        _byKind = byKind;
    }

    /// <summary>No scored request at all: nothing passes.</summary>
    public bool Empty => double.IsNaN(_overall);

    public static Thresholds From(IEnumerable<(string? TaskKind, double Score)> scored)
    {
        var all = scored.ToList();
        if (all.Count == 0)
            return new Thresholds(double.NaN, []);
        var byKind = all
            .GroupBy(r => r.TaskKind ?? "", StringComparer.Ordinal)
            .Where(g => g.Count() >= Eligibility.MinScoredForKind)
            .ToDictionary(g => g.Key, g => Median(g.Select(r => r.Score)), StringComparer.Ordinal);
        return new Thresholds(Median(all.Select(r => r.Score)), byKind);
    }

    public bool Passes(string? taskKind, double score) => !Empty && score >= _byKind.GetValueOrDefault(taskKind ?? "", _overall);

    private static double Median(IEnumerable<double> scores)
    {
        var sorted = scores.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
