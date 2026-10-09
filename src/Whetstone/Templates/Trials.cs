using System.Globalization;
using Whetstone.Contracts;

namespace Whetstone.Templates;

/// <summary>What a run costs, in tokens weighted by what each kind costs the provider; the weights are settings, not facts.</summary>
public sealed record CostWeights(double Input, double CacheWrite, double CacheRead, double Output)
{
    /// <summary>Starting weights (design, "Promotion rule"); update them when the provider's prices change.</summary>
    public static readonly CostWeights Default = new(1, 1.25, 0.1, 5);

    /// <summary>The run's cost, or null when any of the four token counts is missing: a partial cost would compare unlike things.</summary>
    public double? Of(ExportOutcome? outcome) =>
        outcome is { TokensIn: { } input, TokensOut: { } output, CacheReadTokens: { } read, CacheWriteTokens: { } write }
            ? (input * Input) + (write * CacheWrite) + (read * CacheRead) + (output * Output)
            : null;
}

/// <summary>One run of a kind, reduced to what a trial compares.</summary>
public sealed record TrialRun(DateTimeOffset At, string? Arm, bool? Completed, bool? AskedAgain, double? Cost, long? DurationMs, string? Model)
{
    public static TrialRun From(ExportRecord record, CostWeights weights) => new(
        DateTimeOffset.Parse(record.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        record.Answer.Arm ?? (record.Answer.HeldOut ? Arms.HeldOut : null),
        record.Outcome?.Completed, record.Outcome?.AskedAgain, weights.Of(record.Outcome), record.Outcome?.DurationMs, record.Outcome?.Model);
}

/// <summary>What the rule says at one look (design, "Promotion rule").</summary>
/// <param name="Verdict"><c>promote</c>, <c>retire</c> or <c>keep running</c>.</param>
public sealed record LookResult(int Runs, int ChampionRuns, double Z, string Verdict, string Why);

/// <summary>Everything the report says about one kind.</summary>
public sealed record KindReport(
    string Kind, KindTemplates Templates, IReadOnlyDictionary<string, int> Runs, string? Model, IReadOnlyDictionary<string, ArmStats> Counted,
    IReadOnlyList<LookResult> Looks, string? SampleRatio, string? Monitor);

/// <summary>An arm's counted runs: those with a completion, in the model's evidence window.</summary>
public sealed record ArmStats(int Runs, int Completed, double? MedianCost, int Costed, double? MedianDurationMs);

/// <summary>
/// The promotion rule of docs/specs/2026-10-10-templates-everywhere-design.md, as arithmetic: fixed looks, a z test on completion,
/// a non-inferiority gate for a cost-only promotion, a sample-ratio check and a regression monitor. It reads and decides nothing:
/// applying a verdict is the owner's act.
/// </summary>
public static class Trials
{
    /// <summary>Counted challenger runs at which a decision may be taken.</summary>
    public static readonly IReadOnlyList<int> LookRuns = [30, 60, 100, 150, 200];

    /// <summary>The z a difference must pass at any look; with five looks the planned false-promotion rate is about 5%.</summary>
    public const double Z = 2.2;

    /// <summary>How much worse a challenger's completion or re-ask rate may be and still count as no worse.</summary>
    public const double Margin = 0.15;

    /// <summary>Counted runs an arm needs before its cost or re-ask rate is compared.</summary>
    public const int MinCompared = 30;

    private const double ChiSquareCriticalTwo = 13.816; // p = 0.001, 2 degrees of freedom
    private const double ChiSquareCriticalOne = 10.828; // p = 0.001, 1 degree of freedom

    public static KindReport Evaluate(string kind, IReadOnlyList<TrialRun> all, KindTemplates templates)
    {
        var runs = all.GroupBy(r => r.Arm ?? "none").ToDictionary(g => g.Key, g => g.Count());
        var (counted, model) = Window(all);
        var byArm = counted.GroupBy(r => r.Arm ?? "none").ToDictionary(g => g.Key, g => g.OrderBy(r => r.At).ToList());
        List<TrialRun> Of(string arm) => byArm.TryGetValue(arm, out var list) ? list : [];
        var stats = byArm.ToDictionary(g => g.Key, g => Stats(g.Value));
        var looks = new List<LookResult>();
        var challenger = Of(Arms.Challenger);
        var champion = Of(Arms.Champion);
        foreach (var look in LookRuns.Where(l => l <= challenger.Count))
        {
            var cutoff = challenger[look - 1].At;
            looks.Add(Decide(look, [.. challenger.Take(look)], [.. champion.Where(r => r.At <= cutoff)]));
        }
        return new KindReport(kind, templates, runs, model, stats, looks, SampleRatio(runs, templates.Challenger is not null || challenger.Count > 0), Monitor(champion, Of(Arms.HeldOut)));
    }

    /// <summary>
    /// The runs that count together: those with a completion, since the model last changed. A run without a model id is its own
    /// group and is not mixed with the rest (the design's "evidence restarts when the model changes").
    /// </summary>
    internal static (List<TrialRun> Runs, string? Model) Window(IReadOnlyList<TrialRun> all)
    {
        var measured = all.Where(r => r.Completed is not null).OrderBy(r => r.At).ToList();
        if (measured.Count == 0)
            return ([], null);
        var model = measured[^1].Model;
        var start = measured.Count;
        while (start > 0 && measured[start - 1].Model == model)
            start--;
        return (measured[start..], model);
    }

    private static ArmStats Stats(List<TrialRun> runs)
    {
        var costs = runs.Where(r => r.Cost is not null).Select(r => r.Cost!.Value).ToList();
        var durations = runs.Where(r => r.DurationMs is not null).Select(r => (double)r.DurationMs!.Value).ToList();
        return new ArmStats(runs.Count, runs.Count(r => r.Completed == true), Median(costs), costs.Count, Median(durations));
    }

    private static LookResult Decide(int look, List<TrialRun> challenger, List<TrialRun> champion)
    {
        var z = Difference(Wins(challenger), challenger.Count, Wins(champion), champion.Count);
        if (champion.Count < MinCompared)
            return new LookResult(look, champion.Count, z, "keep running", $"the champion has {champion.Count} counted runs, fewer than {MinCompared}");
        if (z > Z)
            return new LookResult(look, champion.Count, z, "promote", "completion is higher");
        if (z < -Z)
            return new LookResult(look, champion.Count, z, "retire", "completion is lower");
        var cost = CostOnly(challenger, champion);
        if (cost.Holds)
            return new LookResult(look, champion.Count, z, "promote", cost.Why);
        return look == LookRuns[^1]
            ? new LookResult(look, champion.Count, z, "retire", $"no difference found; {cost.Why}")
            : new LookResult(look, champion.Count, z, "keep running", cost.Why);
    }

    private static (bool Holds, string Why) CostOnly(List<TrialRun> challenger, List<TrialRun> champion)
    {
        var tc = challenger.Where(r => r.Cost is not null).Select(r => r.Cost!.Value).ToList();
        var cc = champion.Where(r => r.Cost is not null).Select(r => r.Cost!.Value).ToList();
        if (tc.Count < MinCompared || cc.Count < MinCompared)
            return (false, $"cost is reported for {tc.Count} challenger and {cc.Count} champion runs, fewer than {MinCompared}");
        var ratio = Median(tc)!.Value / Median(cc)!.Value;
        var (low, high) = BootstrapRatio(tc, cc);
        if (!(ratio <= 0.9 && high < 1))
            return (false, $"median cost ratio {ratio.ToString("0.00", CultureInfo.InvariantCulture)} (interval {low.ToString("0.00", CultureInfo.InvariantCulture)} to {high.ToString("0.00", CultureInfo.InvariantCulture)}) is not at least 10% lower");
        var pT = (double)Wins(challenger) / challenger.Count;
        var pC = (double)Wins(champion) / champion.Count;
        var se = Math.Sqrt((pT * (1 - pT) / challenger.Count) + (pC * (1 - pC) / champion.Count));
        if (!(pT - pC - (Z * se) > -Margin))
            return (false, "cost is lower but completion is not shown to be no worse by 15 points");
        var at = challenger.Where(r => r.AskedAgain is not null).ToList();
        var ac = champion.Where(r => r.AskedAgain is not null).ToList();
        if (at.Count < MinCompared || ac.Count < MinCompared)
            return (false, "cost is lower but asked_again is not reported for enough runs to show no more re-asks");
        var qT = (double)at.Count(r => r.AskedAgain == true) / at.Count;
        var qC = (double)ac.Count(r => r.AskedAgain == true) / ac.Count;
        var qse = Math.Sqrt((qT * (1 - qT) / at.Count) + (qC * (1 - qC) / ac.Count));
        return qT - qC + (Z * qse) < Margin ? (true, "cost is at least 10% lower with completion and re-asks no worse") : (false, "cost is lower but re-asks are not shown to be no more by 15 points");
    }

    /// <summary>The champion against the held-out arm: a drop in completion, or a cost 10% worse with the interval above 1, is a regression.</summary>
    private static string? Monitor(List<TrialRun> champion, List<TrialRun> held)
    {
        if (champion.Count < MinCompared || held.Count < MinCompared)
            return null;
        var z = Difference(Wins(champion), champion.Count, Wins(held), held.Count);
        if (z < -Z)
            return $"champion completion is below the held-out arm (z {z.ToString("0.0", CultureInfo.InvariantCulture)}): demote";
        var cc = champion.Where(r => r.Cost is not null).Select(r => r.Cost!.Value).ToList();
        var hc = held.Where(r => r.Cost is not null).Select(r => r.Cost!.Value).ToList();
        if (cc.Count >= MinCompared && hc.Count >= MinCompared && Median(cc)! / Median(hc)! >= 1.1 && BootstrapRatio(cc, hc).Low > 1)
            return "champion cost is at least 10% above the held-out arm: demote";
        return $"no sign of harm against the held-out arm (completion z {z.ToString("0.0", CultureInfo.InvariantCulture)})";
    }

    /// <summary>Chi-square of the arm counts against the planned split; a failure means the assignment cannot be trusted.</summary>
    private static string? SampleRatio(IReadOnlyDictionary<string, int> runs, bool challenger)
    {
        var (c, t, h) = (runs.GetValueOrDefault(Arms.Champion), runs.GetValueOrDefault(Arms.Challenger), runs.GetValueOrDefault(Arms.HeldOut));
        var total = c + t + h;
        if (total < MinCompared)
            return null;
        var expected = challenger ? new[] { 0.6, 0.3, 0.1 } : new[] { 0.9, 0.0, 0.1 };
        var observed = new double[] { c, t, h };
        double chi = 0;
        var categories = 0;
        for (var i = 0; i < 3; i++)
        {
            if (expected[i] == 0)
                continue;
            categories++;
            var e = expected[i] * total;
            chi += (observed[i] - e) * (observed[i] - e) / e;
        }
        var critical = categories == 3 ? ChiSquareCriticalTwo : ChiSquareCriticalOne;
        var text = chi.ToString("0.0", CultureInfo.InvariantCulture);
        return chi > critical ? $"FAILED (chi-square {text}): the arm counts do not match the planned split; take no decision until the cause is found" : $"ok (chi-square {text})";
    }

    private static int Wins(List<TrialRun> runs) => runs.Count(r => r.Completed == true);

    /// <summary>z of the first group's completion rate over the second's; 0 when both rates are 0 or 1.</summary>
    internal static double Difference(int successes, int n, int baselineSuccesses, int baselineN)
    {
        if (n == 0 || baselineN == 0)
            return 0;
        var pooled = (double)(successes + baselineSuccesses) / (n + baselineN);
        var se = Math.Sqrt(pooled * (1 - pooled) * ((1.0 / n) + (1.0 / baselineN)));
        return se == 0 ? 0 : (((double)successes / n) - ((double)baselineSuccesses / baselineN)) / se;
    }

    internal static double? Median(List<double> values)
    {
        if (values.Count == 0)
            return null;
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The interval of the median ratio at the same level as <see cref="Z"/> (1.39% in each tail); a fixed seed, so a report is repeatable.</summary>
    internal static (double Low, double High) BootstrapRatio(List<double> numerator, List<double> denominator)
    {
        const int Resamples = 2000;
        var random = new Random(20261010);
        var ratios = new double[Resamples];
        for (var i = 0; i < Resamples; i++)
            ratios[i] = ResampledMedian(numerator, random) / ResampledMedian(denominator, random);
        Array.Sort(ratios);
        return (ratios[(int)(Resamples * 0.0139)], ratios[Math.Min(Resamples - 1, (int)(Resamples * 0.9861))]);
    }

    private static double ResampledMedian(List<double> values, Random random)
    {
        var sample = new double[values.Count];
        for (var i = 0; i < sample.Length; i++)
            sample[i] = values[random.Next(values.Count)];
        Array.Sort(sample);
        var mid = sample.Length / 2;
        return sample.Length % 2 == 1 ? sample[mid] : (sample[mid - 1] + sample[mid]) / 2;
    }
}
