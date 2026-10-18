using System.Globalization;
using Whetstone.Contracts;

namespace Whetstone.Templates;

/// <summary>What a run costs, in tokens weighted by what each kind costs the provider; the weights are settings, not facts.</summary>
public sealed record CostWeights(double Input, double CacheWrite, double CacheRead, double Output)
{
    /// <summary>Starting weights (design, "Promotion rule"); update them when the provider's prices change.</summary>
    public static readonly CostWeights Default = new(1, 1.25, 0.1, 5);

    /// <summary>
    /// Weights from a setting such as <c>WHETSTONE_COST_WEIGHTS</c>: four numbers of 0 or more, input, cache write, cache read and
    /// output, separated by commas. Empty means <see cref="Default"/>; anything else that does not read is null.
    /// </summary>
    public static CostWeights? Parse(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return Default;
        var parts = setting.Split(',', StringSplitOptions.TrimEntries);
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0 || !double.IsFinite(values[i]))
                return null;
        return values.Length == 4 ? new CostWeights(values[0], values[1], values[2], values[3]) : null;
    }

    /// <summary>The run's cost, or null when any of the four token counts is missing: a partial cost would compare unlike things.</summary>
    public double? Of(ExportOutcome? outcome) =>
        outcome is { TokensIn: { } input, TokensOut: { } output, CacheReadTokens: { } read, CacheWriteTokens: { } write }
            ? (input * Input) + (write * CacheWrite) + (read * CacheRead) + (output * Output)
            : null;
}

/// <summary>The settings a report reads: <c>WHETSTONE_COST_WEIGHTS</c> and <c>WHETSTONE_PERSON_TIME_KINDS</c> (comma-separated kind prefixes).</summary>
public sealed record TrialSettings(CostWeights Weights, IReadOnlyList<string> PersonTimeKinds)
{
    public static readonly TrialSettings Default = new(CostWeights.Default, CostMeasure.PersonTimeKinds);

    /// <summary>The settings, or null with the reason when one does not read.</summary>
    public static TrialSettings? From(Func<string, string?> setting, out string? problem)
    {
        problem = null;
        if (CostWeights.Parse(setting("WHETSTONE_COST_WEIGHTS")) is not { } weights)
        {
            problem = "WHETSTONE_COST_WEIGHTS is four numbers of 0 or more, input, cache write, cache read and output, such as 1,1.25,0.1,5";
            return null;
        }
        var kinds = setting("WHETSTONE_PERSON_TIME_KINDS") is { } named
            ? named.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [.. CostMeasure.PersonTimeKinds];
        return new TrialSettings(weights, kinds);
    }
}

/// <summary>
/// How the proxy "completed, and no rework" agrees with the owner's hand labels (design, "Validity first"): decisions are applied by
/// hand until at least <see cref="Needed"/> runs are labelled and the proxy agrees with at least <see cref="Bar"/> of them.
/// </summary>
public sealed record HandLabels(int Labelled, int Compared, int Agree)
{
    public const int Needed = 30;

    public const double Bar = 0.75;

    public static HandLabels Of(IEnumerable<ExportRecord> records, CostWeights weights)
    {
        var labelled = records.Where(r => r.HandLabel is not null).ToList();
        var compared = labelled.Select(r => (Label: r.HandLabel!.Value, Run: TrialRun.From(r, weights))).Where(x => x.Run.Completed is not null).ToList();
        return new HandLabels(labelled.Count, compared.Count, compared.Count(x => x.Label == (x.Run.Completed == true && x.Run.Rework != true)));
    }

    public bool Trusted => Compared >= Needed && Agree >= Bar * Compared;
}

/// <summary>One run of a kind, reduced to what a trial compares.</summary>
/// <param name="Reason">Why the next prompt corrected the run, when it did; it decides <see cref="Rework"/>.</param>
/// <param name="ReviewFindings">Problems a reviewer found in the produced change.</param>
public sealed record TrialRun(
    DateTimeOffset At, string? Arm, bool? Completed, bool? AskedAgain, double? Cost, long? DurationMs, string? Model,
    string? TemplateId = null, string? TemplateVersion = null, long? TokensIn = null, long? TokensOut = null, long? CacheReadTokens = null,
    string? Reason = null, long? PersonMs = null, long? ReviewFindings = null, bool? Reverted = null, bool? CiFirstPushGreen = null,
    long? CacheWriteTokens = null)
{
    /// <summary>
    /// A correction that blames the prompt or the run (reward design, decision 2). With no reason reported, <c>asked_again</c> stands
    /// in for it, so a client that reports only that is judged as before.
    /// </summary>
    public bool? Rework => Reason is not null ? CorrectionReasons.IsRework(Reason) : AskedAgain;

    public static TrialRun From(ExportRecord record, CostWeights weights) => new(
        DateTimeOffset.Parse(record.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        record.Answer.Arm ?? (record.Answer.HeldOut ? Arms.HeldOut : null),
        record.Outcome?.Completed, record.Outcome?.AskedAgain, weights.Of(record.Outcome), record.Outcome?.DurationMs, record.Outcome?.Model,
        record.Answer.TemplateId, record.Answer.TemplateVersion, record.Outcome?.TokensIn, record.Outcome?.TokensOut, record.Outcome?.CacheReadTokens,
        record.Outcome?.CorrectionReason, record.Outcome?.PersonMs, record.Outcome?.ReviewFindings, record.Outcome?.Reverted, record.Outcome?.CiFirstPushGreen,
        record.Outcome?.CacheWriteTokens);
}

/// <summary>
/// What a kind's cost-only promotion compares (reward design, decision 6): the person's time for typed kinds, where about nine tokens
/// in ten are cache reads and token cost barely moves, and the weighted token cost for the rest.
/// </summary>
public sealed record CostMeasure(string Name, Func<TrialRun, double?> Of)
{
    public static readonly CostMeasure RunCost = new("run cost", r => r.Cost);

    public static readonly CostMeasure PersonTime = new("person time", r => r.PersonMs);

    /// <summary>Kinds whose name starts with one of these prefixes are judged on the person's time, unless a setting names others.</summary>
    public static readonly IReadOnlyList<string> PersonTimeKinds = ["typed/"];

    public static CostMeasure For(string kind, IReadOnlyList<string>? personTimeKinds = null) =>
        (personTimeKinds ?? PersonTimeKinds).Any(p => kind.StartsWith(p, StringComparison.Ordinal)) ? PersonTime : RunCost;
}

/// <summary>
/// What one template version (or the absence of one) did for a kind, over every stored run, not only the model window the trial
/// counts: <paramref name="Uses"/> served, <paramref name="Reported"/> of them with a completion, and the medians of what was reported.
/// </summary>
public sealed record VersionStats(
    string? TemplateId, string? TemplateVersion, string Arm, int Uses, int Reported, int Completed, int AskedAgainReported, int AskedAgain,
    double? MedianTokensIn, double? MedianTokensOut, double? MedianCacheReadTokens, double? MedianDurationMs, double? MedianCost, DateTimeOffset LastUsed,
    int ReworkReported = 0, int Rework = 0, double? MedianPersonMs = null);

/// <summary>What the rule says at one look (design, "Promotion rule").</summary>
/// <param name="Verdict"><c>promote</c>, <c>retire</c> or <c>keep running</c>.</param>
public sealed record LookResult(int Runs, int ChampionRuns, double Z, string Verdict, string Why);

/// <summary>Everything the report says about one kind.</summary>
public sealed record KindReport(
    string Kind, KindTemplates Templates, IReadOnlyDictionary<string, int> Runs, string? Model, IReadOnlyDictionary<string, ArmStats> Counted,
    IReadOnlyList<LookResult> Looks, string? SampleRatio, string? Monitor, IReadOnlyList<VersionStats>? Versions = null, string CostMeasure = "run cost",
    string? Cache = null);

/// <summary>An arm's counted runs: those with a completion, in the model's evidence window.</summary>
public sealed record ArmStats(
    int Runs, int Completed, double? MedianCost, int Costed, double? MedianDurationMs,
    int ReworkReported = 0, int Rework = 0, double? MedianPersonMs = null, int PersonTimed = 0, double? CacheReadShare = null, int CacheMeasured = 0);

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

    /// <summary>How many points lower an arm's cache-read share may be than the arm it is compared with before the report flags it.</summary>
    public const double CacheShareDrop = 0.10;

    public static KindReport Evaluate(string kind, IReadOnlyList<TrialRun> all, KindTemplates templates, IReadOnlyList<string>? personTimeKinds = null)
    {
        var measure = CostMeasure.For(kind, personTimeKinds);
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
            looks.Add(Decide(look, [.. challenger.Take(look)], [.. champion.Where(r => r.At <= cutoff)], measure));
        }
        return new KindReport(kind, templates, runs, model, stats, looks, SampleRatio(runs, templates.Challenger is not null || challenger.Count > 0),
            Monitor(champion, Of(Arms.HeldOut), measure), PerVersion(all), measure.Name, Cache(stats));
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

    /// <summary>One row per template version and arm that served a run; the runs with no template last, newest use first within each.</summary>
    internal static List<VersionStats> PerVersion(IReadOnlyList<TrialRun> all) => [.. all
        .GroupBy(r => (r.TemplateId, r.TemplateVersion, Arm: r.Arm ?? "none"))
        .Select(g =>
        {
            double? MedianOf(Func<TrialRun, long?> pick) => Median([.. g.Select(pick).Where(v => v is not null).Select(v => (double)v!.Value)]);
            var asked = g.Where(r => r.AskedAgain is not null).ToList();
            var rework = g.Where(r => r.Rework is not null).ToList();
            return new VersionStats(
                g.Key.TemplateId, g.Key.TemplateVersion, g.Key.Arm, g.Count(), g.Count(r => r.Completed is not null), g.Count(r => r.Completed == true),
                asked.Count, asked.Count(r => r.AskedAgain == true), MedianOf(r => r.TokensIn), MedianOf(r => r.TokensOut), MedianOf(r => r.CacheReadTokens),
                MedianOf(r => r.DurationMs), Median([.. g.Where(r => r.Cost is not null).Select(r => r.Cost!.Value)]), g.Max(r => r.At),
                rework.Count, rework.Count(r => r.Rework == true), MedianOf(r => r.PersonMs));
        })
        .OrderBy(v => v.TemplateId is null ? 1 : 0).ThenByDescending(v => v.LastUsed)];

    private static ArmStats Stats(List<TrialRun> runs)
    {
        var costs = runs.Where(r => r.Cost is not null).Select(r => r.Cost!.Value).ToList();
        var durations = runs.Where(r => r.DurationMs is not null).Select(r => (double)r.DurationMs!.Value).ToList();
        var person = runs.Where(r => r.PersonMs is not null).Select(r => (double)r.PersonMs!.Value).ToList();
        var rework = runs.Where(r => r.Rework is not null).ToList();
        var cached = runs.Where(r => r is { TokensIn: not null, CacheReadTokens: not null, CacheWriteTokens: not null }).ToList();
        var read = cached.Sum(r => (double)r.CacheReadTokens!.Value);
        var prompt = cached.Sum(r => (double)(r.TokensIn!.Value + r.CacheReadTokens!.Value + r.CacheWriteTokens!.Value));
        return new ArmStats(runs.Count, runs.Count(r => r.Completed == true), Median(costs), costs.Count, Median(durations),
            rework.Count, rework.Count(r => r.Rework == true), Median(person), person.Count, prompt > 0 ? read / prompt : null, cached.Count);
    }

    /// <summary>
    /// The cache-read share of prompt tokens (reward design, decision 5): a template whose text breaks the cached prefix costs more even
    /// when its token counts look equal. Flags the challenger against the champion and the champion against the held-out arm, each
    /// once both have <see cref="MinCompared"/> runs with cache measures.
    /// </summary>
    private static string? Cache(IReadOnlyDictionary<string, ArmStats> stats)
    {
        foreach (var (arm, baseline) in new[] { (Arms.Challenger, Arms.Champion), (Arms.Champion, Arms.HeldOut) })
        {
            if (stats.GetValueOrDefault(arm) is not { CacheReadShare: { } share, CacheMeasured: >= MinCompared }
                || stats.GetValueOrDefault(baseline) is not { CacheReadShare: { } against, CacheMeasured: >= MinCompared })
                continue;
            if (against - share > CacheShareDrop)
                return $"the {arm.Replace('_', ' ')} arm reads {Share(share)} of prompt tokens from the cache against {Share(against)} for the {baseline.Replace('_', ' ')} arm: its text may break the cached prefix";
        }
        return null;
    }

    private static LookResult Decide(int look, List<TrialRun> challenger, List<TrialRun> champion, CostMeasure measure)
    {
        var z = Difference(Wins(challenger), challenger.Count, Wins(champion), champion.Count);
        if (champion.Count < MinCompared)
            return new LookResult(look, champion.Count, z, "keep running", $"the champion has {champion.Count} counted runs, fewer than {MinCompared}");
        if (z < -Z)
            return new LookResult(look, champion.Count, z, "retire", "completion is lower");
        var guard = Guardrails(challenger, champion);
        if (z > Z)
            return guard is null
                ? new LookResult(look, champion.Count, z, "promote", "completion is higher")
                : new LookResult(look, champion.Count, z, "keep running", $"completion is higher but {guard}");
        var cost = CostOnly(challenger, champion, measure);
        if (cost.Holds && guard is not null)
            cost = (false, $"{cost.Why}, but {guard}");
        if (cost.Holds)
            return new LookResult(look, champion.Count, z, "promote", cost.Why);
        return look == LookRuns[^1]
            ? new LookResult(look, champion.Count, z, "retire", $"no difference found; {cost.Why}")
            : new LookResult(look, champion.Count, z, "keep running", cost.Why);
    }

    private static (bool Holds, string Why) CostOnly(List<TrialRun> challenger, List<TrialRun> champion, CostMeasure measure)
    {
        var name = measure.Name;
        var tc = Values(challenger, measure.Of);
        var cc = Values(champion, measure.Of);
        if (tc.Count < MinCompared || cc.Count < MinCompared)
            return (false, $"{name} is reported for {tc.Count} challenger and {cc.Count} champion runs, fewer than {MinCompared}");
        var ratio = Median(tc)!.Value / Median(cc)!.Value;
        var (low, high) = BootstrapRatio(tc, cc);
        if (!(ratio <= 0.9 && high < 1))
            return (false, $"median {name} ratio {ratio.ToString("0.00", CultureInfo.InvariantCulture)} (interval {low.ToString("0.00", CultureInfo.InvariantCulture)} to {high.ToString("0.00", CultureInfo.InvariantCulture)}) is not at least 10% lower");
        var pT = (double)Wins(challenger) / challenger.Count;
        var pC = (double)Wins(champion) / champion.Count;
        var se = Math.Sqrt((pT * (1 - pT) / challenger.Count) + (pC * (1 - pC) / champion.Count));
        if (!(pT - pC - (Z * se) > -Margin))
            return (false, $"{name} is lower but completion is not shown to be no worse by 15 points");
        var at = challenger.Where(r => r.Rework is not null).ToList();
        var ac = champion.Where(r => r.Rework is not null).ToList();
        if (at.Count < MinCompared || ac.Count < MinCompared)
            return (false, $"{name} is lower but rework is not reported for enough runs to show no more of it");
        var qT = (double)at.Count(r => r.Rework == true) / at.Count;
        var qC = (double)ac.Count(r => r.Rework == true) / ac.Count;
        var qse = Math.Sqrt((qT * (1 - qT) / at.Count) + (qC * (1 - qC) / ac.Count));
        return qT - qC + (Z * qse) < Margin ? (true, $"{name} is at least 10% lower with completion and rework no worse") : (false, $"{name} is lower but rework is not shown to be no more by 15 points");
    }

    /// <summary>
    /// The guardrails a promotion must pass (reward design, decision 6): the share of runs with review findings, with a revert within 7
    /// days, and with a red first CI run. Each is compared only when both arms report it for <see cref="MinCompared"/> runs; it blocks
    /// when the challenger's share is worse by more than <see cref="Margin"/>, or worse at <see cref="Z"/>. Null when none blocks.
    /// </summary>
    private static string? Guardrails(List<TrialRun> challenger, List<TrialRun> champion)
    {
        foreach (var (name, bad) in new (string, Func<TrialRun, bool?>)[]
        {
            ("runs with review findings", r => r.ReviewFindings is { } f ? f > 0 : null),
            ("reverted runs", r => r.Reverted),
            ("runs whose first CI run failed", r => r.CiFirstPushGreen is { } g ? !g : null),
        })
        {
            var t = challenger.Select(bad).Where(b => b is not null).ToList();
            var c = champion.Select(bad).Where(b => b is not null).ToList();
            if (t.Count < MinCompared || c.Count < MinCompared)
                continue;
            var pT = (double)t.Count(b => b == true) / t.Count;
            var pC = (double)c.Count(b => b == true) / c.Count;
            if (pT - pC > Margin || Difference(t.Count(b => b == true), t.Count, c.Count(b => b == true), c.Count) > Z)
                return $"the share of {name} is worse ({Share(pT)} against {Share(pC)})";
        }
        return null;
    }

    private static string Share(double p) => (100 * p).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static List<double> Values(List<TrialRun> runs, Func<TrialRun, double?> of) => [.. runs.Select(of).Where(v => v is not null).Select(v => v!.Value)];

    /// <summary>The champion against the held-out arm: a drop in completion, or a cost 10% worse with the interval above 1, is a regression.</summary>
    private static string? Monitor(List<TrialRun> champion, List<TrialRun> held, CostMeasure measure)
    {
        if (champion.Count < MinCompared || held.Count < MinCompared)
            return null;
        var z = Difference(Wins(champion), champion.Count, Wins(held), held.Count);
        if (z < -Z)
            return $"champion completion is below the held-out arm (z {z.ToString("0.0", CultureInfo.InvariantCulture)}): demote";
        var cc = Values(champion, measure.Of);
        var hc = Values(held, measure.Of);
        if (cc.Count >= MinCompared && hc.Count >= MinCompared && Median(cc)! / Median(hc)! >= 1.1 && BootstrapRatio(cc, hc).Low > 1)
            return $"champion {measure.Name} is at least 10% above the held-out arm: demote";
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
