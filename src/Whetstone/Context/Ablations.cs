using Whetstone.Contracts;
using Whetstone.Templates;

namespace Whetstone.Context;

/// <summary>One ablation's record against sessions that held nothing out.</summary>
public sealed record AblationLine(string Ablation, int Sessions, int Runs, int Completed, int ReworkReported, int Rework, bool RemovalCandidate);

/// <summary>
/// Instruction-rule and skill ablations (docs/specs/2026-10-19-context-slots-design.md, decisions 3 and 4): the client holds one rule
/// or skill version out for a share of sessions and says which in <c>context.ablation</c>; whetstone compares the outcomes. A rule
/// whose absence changes completion and rework by less than <see cref="NoEffect"/> after <see cref="Sessions"/> sessions is a removal
/// candidate. The owner decides; whetstone changes no instruction file.
/// </summary>
public static class Ablations
{
    public const int Sessions = 100;

    public const double NoEffect = 0.05;

    public static (AblationLine Baseline, IReadOnlyList<AblationLine> Lines) Of(IReadOnlyList<ExportRecord> records, CostWeights weights)
    {
        var inSessions = records.Where(r => r.Context.SessionId is not null).ToList();
        AblationLine Line(string name, IEnumerable<ExportRecord> group, AblationLine? baseline)
        {
            var list = group.ToList();
            var runs = list.Select(r => TrialRun.From(r, weights)).Where(r => r.Completed is not null).ToList();
            var rework = runs.Where(r => r.Rework is not null).ToList();
            var line = new AblationLine(name, list.Select(r => r.Context.SessionId).Distinct(StringComparer.Ordinal).Count(), runs.Count,
                runs.Count(r => r.Completed == true), rework.Count, rework.Count(r => r.Rework == true), false);
            return baseline is null || line.Sessions < Sessions || line.Runs == 0 || baseline.Runs == 0 ? line : line with
            {
                RemovalCandidate = Math.Abs(Rate(line.Completed, line.Runs) - Rate(baseline.Completed, baseline.Runs)) < NoEffect
                    && Math.Abs(Rate(line.Rework, line.ReworkReported) - Rate(baseline.Rework, baseline.ReworkReported)) < NoEffect,
            };
        }
        var baseline = Line("nothing held out", inSessions.Where(r => r.Context.Ablation is null), null);
        return (baseline, [.. inSessions.Where(r => r.Context.Ablation is not null).GroupBy(r => r.Context.Ablation!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Line(g.Key, g, baseline))]);
    }

    private static double Rate(int part, int whole) => whole == 0 ? 0 : (double)part / whole;
}
