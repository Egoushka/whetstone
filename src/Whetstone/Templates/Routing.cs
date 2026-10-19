using System.Globalization;
using Whetstone.Contracts;

namespace Whetstone.Templates;

/// <summary>One model (or effort) of one kind: its counted runs and what they show.</summary>
public sealed record RouteLine(string Kind, string Value, int Runs, int Completed, int ReworkReported, int Rework, double? MedianCost, bool Best, bool CheapestEnough);

/// <summary>
/// Routing evidence (docs/specs/2026-10-19-levels-and-routing-design.md, decisions 2 and 3): per kind, how each model or effort did,
/// and the cheapest one whose completion is shown no worse than the best one's by more than <see cref="Trials.Margin"/>, each with at
/// least <see cref="Trials.MinCompared"/> counted runs. It suggests; the owner changes routing elsewhere, and the client decides.
/// </summary>
public static class Routing
{
    public static IReadOnlyList<RouteLine> Of(IReadOnlyList<ExportRecord> records, CostWeights weights, Func<ExportRecord, string?> by)
    {
        var lines = new List<RouteLine>();
        var measured = records.Where(r => r.Context.TaskKind is not null && r.Outcome?.Completed is not null && by(r) is not null);
        foreach (var kind in measured.GroupBy(r => r.Context.TaskKind!.Split('@')[0], StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var groups = kind.GroupBy(r => by(r)!, StringComparer.Ordinal).Select(g =>
            {
                var runs = g.Select(r => TrialRun.From(r, weights)).ToList();
                var rework = runs.Where(r => r.Rework is not null).ToList();
                return new RouteLine(kind.Key, g.Key, runs.Count, runs.Count(r => r.Completed == true), rework.Count, rework.Count(r => r.Rework == true),
                    Trials.Median([.. runs.Where(r => r.Cost is not null).Select(r => r.Cost!.Value)]), false, false);
            }).ToList();
            var eligible = groups.Where(g => g.Runs >= Trials.MinCompared).ToList();
            var best = eligible.OrderByDescending(g => (double)g.Completed / g.Runs).FirstOrDefault();
            var cheapest = best is null ? null : eligible
                .Where(g => g.MedianCost is not null && NoWorse(g, best))
                .OrderBy(g => g.MedianCost).FirstOrDefault();
            lines.AddRange(groups.OrderByDescending(g => g.Runs).Select(g => g with { Best = g == best, CheapestEnough = g == cheapest && g != best }));
        }
        return lines;
    }

    /// <summary>The lower bound of the completion difference, at the rule's z, is above minus the margin.</summary>
    private static bool NoWorse(RouteLine candidate, RouteLine best)
    {
        var p = (double)candidate.Completed / candidate.Runs;
        var q = (double)best.Completed / best.Runs;
        var se = Math.Sqrt((p * (1 - p) / candidate.Runs) + (q * (1 - q) / best.Runs));
        return p - q - (Trials.Z * se) > -Trials.Margin;
    }

    public static string Format(IReadOnlyList<RouteLine> lines, string what)
    {
        if (lines.Count == 0)
            return $"No kind has runs with a completion and a {what} yet.\n";
        var text = new System.Text.StringBuilder();
        foreach (var kind in lines.GroupBy(l => l.Kind, StringComparer.Ordinal))
        {
            text.Append(kind.Key).Append('\n');
            foreach (var l in kind)
                text.Append(CultureInfo.InvariantCulture, $"  {l.Value}: {l.Runs} runs, completed {100.0 * l.Completed / l.Runs:0}% ({l.Completed}/{l.Runs})")
                    .Append(l.ReworkReported > 0 ? FormattableString.Invariant($", rework {100.0 * l.Rework / l.ReworkReported:0}%") : "")
                    .Append(l.MedianCost is { } c ? FormattableString.Invariant($", median cost {c:0}") : "")
                    .Append(l.Best ? "; best completion" : "")
                    .Append(l.CheapestEnough ? "; cheapest shown no worse than the best: a routing candidate" : "").Append('\n');
        }
        text.Append(CultureInfo.InvariantCulture, $"Suggestions only: routing changes by the owner's hand, outside whetstone. Each {what} needs {Trials.MinCompared} counted runs to be compared.\n");
        return text.ToString();
    }
}
