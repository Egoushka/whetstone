using System.Globalization;
using System.Text;
using Whetstone.Contracts;

namespace Whetstone.Templates;

/// <summary>
/// The weekly report for the person (docs/specs/2026-10-19-person-facing-design.md, decision 3): per ISO week, typed requests, the
/// person's time and where it went by kind, and rework by reason. It reads only.
/// </summary>
public static class PersonWeeks
{
    public static string Format(IReadOnlyList<ExportRecord> records, CostWeights weights, int weeks)
    {
        var typed = records.Where(r => r.Context.TaskKind?.StartsWith("typed/", StringComparison.Ordinal) == true || r.Context.Client == "claude-code")
            .Select(r => (Record: r, At: DateTimeOffset.Parse(r.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)))
            .GroupBy(x => (ISOWeek.GetYear(x.At.UtcDateTime), ISOWeek.GetWeekOfYear(x.At.UtcDateTime)))
            .OrderByDescending(g => g.Key).Take(weeks).ToList();
        if (typed.Count == 0)
            return "No typed requests yet.\n";
        var text = new StringBuilder();
        foreach (var week in typed)
        {
            var runs = week.Select(x => (x.Record, Run: TrialRun.From(x.Record, weights))).ToList();
            var timed = runs.Where(x => x.Run.PersonMs is not null).ToList();
            var minutes = timed.Sum(x => x.Run.PersonMs!.Value) / 60000.0;
            text.Append(CultureInfo.InvariantCulture, $"{week.Key.Item1}-W{week.Key.Item2:00}: {runs.Count} typed requests");
            text.Append(timed.Count > 0 ? FormattableString.Invariant($", your time {minutes:0} min over {timed.Count} of them (median {Trials.Median([.. timed.Select(x => (double)x.Run.PersonMs!.Value)])!.Value / 1000:0} s)") : ", no person time reported");
            text.Append('\n');
            foreach (var kind in timed.GroupBy(x => x.Record.Context.TaskKind ?? "no kind").Select(g => (Kind: g.Key, Minutes: g.Sum(x => x.Run.PersonMs!.Value) / 60000.0)).OrderByDescending(k => k.Minutes).Take(3))
                text.Append(CultureInfo.InvariantCulture, $"  {kind.Kind}: {kind.Minutes:0} min\n");
            var reworked = runs.Where(x => x.Run.Rework == true).ToList();
            if (reworked.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $"  rework {reworked.Count}: ")
                    .Append(string.Join(", ", reworked.GroupBy(x => x.Run.Reason ?? "no reason").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")))
                    .Append('\n');
        }
        return text.ToString();
    }
}
