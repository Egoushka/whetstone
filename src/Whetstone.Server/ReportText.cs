using System.Globalization;
using System.Text;
using Whetstone.Templates;

namespace Whetstone.Server;

/// <summary>The text of <c>whetstone report</c>: per kind, who got what and what the promotion rule says.</summary>
internal static class ReportText
{
    public static string Format(IReadOnlyList<KindReport> kinds, int untracked)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{kinds.Count} kinds in template trials; {untracked} stored requests are in none.\n");
        foreach (var kind in kinds)
        {
            text.Append('\n').Append(kind.Kind).Append('\n');
            text.Append("  ").Append(Who(kind.Templates)).Append('\n');
            text.Append("  runs by arm: ").Append(string.Join(", ", new[] { Arms.Champion, Arms.Challenger, Arms.HeldOut, "none" }
                .Select(arm => $"{arm.Replace('_', ' ')} {kind.Runs.GetValueOrDefault(arm)}"))).Append('\n');
            if (kind.Counted.Count == 0)
            {
                text.Append("  no run has a reported completion yet\n");
            }
            else
            {
                text.Append("  counted runs (").Append(kind.Model is { } model ? $"model {model}" : "no model reported").Append("):\n");
                foreach (var (arm, stats) in kind.Counted.OrderBy(a => a.Key, StringComparer.Ordinal))
                    text.Append("    ").Append(arm.Replace('_', ' ')).Append(CultureInfo.InvariantCulture, $": {stats.Runs} runs, completed {Percent(stats.Completed, stats.Runs)}")
                        .Append(stats.MedianCost is { } cost ? $", median cost {cost:0} (of {stats.Costed} runs)" : "")
                        .Append(stats.MedianDurationMs is { } ms ? $", median duration {ms / 1000:0.#} s" : "").Append('\n');
            }
            if (kind.SampleRatio is { } ratio)
                text.Append("  sample ratio: ").Append(ratio).Append('\n');
            if (kind.Monitor is { } monitor)
                text.Append("  monitor: ").Append(monitor).Append('\n');
            foreach (var look in kind.Looks)
                text.Append(CultureInfo.InvariantCulture, $"  look at {look.Runs} challenger runs (champion {look.ChampionRuns}): z {look.Z:0.0}, {look.Verdict} ({look.Why})\n");
        }
        text.Append("\nThe report decides nothing: a verdict is applied by the owner.\n");
        return text.ToString();
    }

    private static string Who(KindTemplates templates) =>
        (templates.Champion is { } champion ? $"champion {champion.Id} v{champion.Version}" : "no champion (held out entirely)")
        + (templates.Challenger is { } challenger ? $"; challenger {challenger.Id} v{challenger.Version}" : "; no challenger");

    private static string Percent(int part, int whole) => whole == 0 ? "-" : $"{100.0 * part / whole:0}% ({part}/{whole})";
}
