using System.Globalization;
using System.Text;
using Whetstone.Templates;

namespace Whetstone.Server;

/// <summary>The text of <c>whetstone report</c>: per kind, who got what and what the promotion rule says.</summary>
internal static class ReportText
{
    public static string Format(IReadOnlyList<KindReport> kinds, int untracked, HandLabels? labels = null)
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
                text.Append("  counted runs (").Append(kind.Model is { } model ? $"model {model}" : "no model reported")
                    .Append("; a cost-only decision compares ").Append(kind.CostMeasure).Append("):\n");
                foreach (var (arm, stats) in kind.Counted.OrderBy(a => a.Key, StringComparer.Ordinal))
                    text.Append("    ").Append(arm.Replace('_', ' ')).Append(CultureInfo.InvariantCulture, $": {stats.Runs} runs, completed {Percent(stats.Completed, stats.Runs)}")
                        .Append(stats.MedianCost is { } cost ? $", median cost {cost:0} (of {stats.Costed} runs)" : "")
                        .Append(stats.MedianDurationMs is { } ms ? $", median duration {ms / 1000:0.#} s" : "")
                        .Append(stats.ReworkReported > 0 ? $", rework {Percent(stats.Rework, stats.ReworkReported)}" : "")
                        .Append(stats.MedianPersonMs is { } person ? $", median person time {person / 1000:0.#} s (of {stats.PersonTimed} runs)" : "")
                        .Append(stats.CacheReadShare is { } share ? $", cache reads {100 * share:0}% of prompt tokens" : "").Append('\n');
            }
            AppendVersions(text, kind);
            if (kind.SampleRatio is { } ratio)
                text.Append("  sample ratio: ").Append(ratio).Append('\n');
            if (kind.Cache is { } cache)
                text.Append("  cache: ").Append(cache).Append('\n');
            if (kind.Monitor is { } monitor)
                text.Append("  monitor: ").Append(monitor).Append('\n');
            foreach (var look in kind.Looks)
                text.Append(CultureInfo.InvariantCulture, $"  look at {look.Runs} challenger runs (champion {look.ChampionRuns}): z {look.Z:0.0}, {look.Verdict} ({look.Why})\n");
        }
        if (labels is { } l)
            text.Append(CultureInfo.InvariantCulture, $"\nhand labels: {l.Labelled} labelled, {l.Compared} with a reported completion; the proxy (completed, no rework) agrees on {Percent(l.Agree, l.Compared)}")
                .Append(l.Trusted ? "; enough to trust the proxy.\n" : $"; label at least {HandLabels.Needed} with {HandLabels.Bar * 100:0}% agreement before trusting it (whetstone label).\n");
        text.Append("\nThe report decides nothing: a verdict is applied by the owner.\n");
        return text.ToString();
    }

    private static void AppendVersions(StringBuilder text, KindReport kind)
    {
        if (kind.Versions is not { Count: > 0 } versions)
            return;
        text.Append("  by version (every stored run):\n");
        foreach (var v in versions)
        {
            var name = v.TemplateId is null ? (v.Arm == Arms.HeldOut ? "no template (held out)" : "no template") : $"{v.TemplateId} v{v.TemplateVersion} ({v.Arm})";
            var current = kind.Templates.Champion is { } c && c.Id == v.TemplateId && c.Version == v.TemplateVersion ? ", current champion"
                : kind.Templates.Challenger is { } t && t.Id == v.TemplateId && t.Version == v.TemplateVersion ? ", on trial" : "";
            text.Append("    ").Append(name).Append(current).Append(CultureInfo.InvariantCulture, $": {v.Uses} uses, {v.Reported} with an outcome")
                .Append(v.Reported > 0 ? $", completed {Percent(v.Completed, v.Reported)}" : "")
                .Append(v.AskedAgainReported > 0 ? $", asked again {Percent(v.AskedAgain, v.AskedAgainReported)}" : "")
                .Append(v.ReworkReported > 0 ? $", rework {Percent(v.Rework, v.ReworkReported)}" : "")
                .Append(Median("tokens in", v.MedianTokensIn)).Append(Median("out", v.MedianTokensOut)).Append(Median("cache read", v.MedianCacheReadTokens))
                .Append(v.MedianDurationMs is { } ms ? $", median {ms / 1000:0.#} s" : "")
                .Append(v.MedianCost is { } cost ? $", median cost {cost:0}" : "")
                .Append(v.MedianPersonMs is { } person ? $", median person time {person / 1000:0.#} s" : "").Append('\n');
        }
        if (kind.Templates.Challenger is { } challenger)
        {
            var counted = kind.Counted.TryGetValue(Arms.Challenger, out var arm) ? arm.Runs : 0;
            var next = Trials.LookRuns.FirstOrDefault(l => l > counted);
            text.Append("  trial: ").Append(challenger.Id).Append(" v").Append(challenger.Version).Append(CultureInfo.InvariantCulture, $" has {counted} counted runs; ")
                .Append(next == 0 ? "past the last look" : $"first decision at {next}").Append('\n');
        }
        else
        {
            text.Append("  trial: none running\n");
        }
    }

    private static string Median(string label, double? value) => value is { } v ? string.Create(CultureInfo.InvariantCulture, $", median {label} {v:#,0}") : "";

    private static string Who(KindTemplates templates) =>
        (templates.Champion is { } champion ? $"champion {champion.Id} v{champion.Version}" : "no champion (held out entirely)")
        + (templates.Challenger is { } challenger ? $"; challenger {challenger.Id} v{challenger.Version}" : "; no challenger");

    private static string Percent(int part, int whole) => whole == 0 ? "-" : $"{100.0 * part / whole:0}% ({part}/{whole})";
}
