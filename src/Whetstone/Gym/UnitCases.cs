using System.Globalization;
using Whetstone.Templates;

namespace Whetstone.Gym;

/// <summary>What a unit test of a template found against its baseline.</summary>
public sealed record UnitTestReport(int Cases, int BaselineCompleted, int Completed, double? BaselineMedianCost, double? MedianCost, bool Passed, string Why);

/// <summary>One kind's line in the harness comparison: control runs of one gym client.</summary>
public sealed record HarnessLine(string Kind, string Client, int Runs, int Completed, double? MedianCost, double? MedianDurationMs);

/// <summary>
/// Five pinned replay cases per champion (docs/specs/2026-10-19-replay-gym-design.md, decisions 3 to 5): picked where the template's
/// run and its control both completed, replayed when the template or the model changes, and failed when completion drops or cost
/// rises more than <see cref="CostMargin"/> against the template's own earlier runs on the same cases.
/// </summary>
public static class UnitCases
{
    public const int PerTemplate = 5;

    public const double CostMargin = 0.15;

    /// <summary>The arm a unit-test replay is stored under: it is neither a control nor a trial challenger.</summary>
    public const string Arm = "unit";

    /// <summary>The cases to pin for a template: newest first, where its run and the control on the same case and model both completed.</summary>
    public static IReadOnlyList<string> Pick(IReadOnlyList<GymRun> runs, Template template, int count = PerTemplate) => [.. runs
        .Where(r => r.TemplateId == template.Id && r.TemplateVersion == template.Version && r.Result.Completed == true)
        .Where(r => runs.Any(c => c.CaseId == r.CaseId && c.Arm == GymArms.Control && c.Result.Completed == true && c.Result.Model == r.Result.Model))
        .OrderByDescending(r => r.StartedAt)
        .Select(r => r.CaseId)
        .Distinct(StringComparer.Ordinal)
        .Take(count)];

    /// <summary>Compares a template's newest runs on its pinned cases with its runs there before them.</summary>
    public static UnitTestReport Test(IReadOnlyList<GymRun> baseline, IReadOnlyList<GymRun> fresh)
    {
        var baseCost = Trials.Median([.. baseline.Where(r => r.Result.CostUsd is not null).Select(r => r.Result.CostUsd!.Value)]);
        var cost = Trials.Median([.. fresh.Where(r => r.Result.CostUsd is not null).Select(r => r.Result.CostUsd!.Value)]);
        int baseDone = baseline.Count(r => r.Result.Completed == true), done = fresh.Count(r => r.Result.Completed == true);
        if (baseline.Count == 0)
            return new UnitTestReport(fresh.Count, 0, done, null, cost, false, "no earlier run on these cases to compare with");
        if (done < baseDone)
            return new UnitTestReport(fresh.Count, baseDone, done, baseCost, cost, false, $"completed {done} of {fresh.Count}, before {baseDone}");
        if (baseCost is > 0 && cost is { } c && c > baseCost.Value * (1 + CostMargin))
            return new UnitTestReport(fresh.Count, baseDone, done, baseCost, cost, false,
                $"median cost {c.ToString("0.000", CultureInfo.InvariantCulture)} is more than 15% above {baseCost.Value.ToString("0.000", CultureInfo.InvariantCulture)}");
        return new UnitTestReport(fresh.Count, baseDone, done, baseCost, cost, true, "completes as often and costs no more than 15% extra");
    }

    /// <summary>
    /// The drift canary: champions whose kind's live runs now report a model their unit-case runs never used. Each needs
    /// <c>whetstone gym test</c> before its record on the new model is trusted.
    /// </summary>
    public static IReadOnlyList<(Template Champion, string? LiveModel, string? TestedModel)> Drifted(
        IReadOnlyList<Template> champions, IReadOnlyDictionary<string, string?> liveModelByKind, IReadOnlyList<GymRun> runs, IReadOnlySet<string> pinnedCases) => [.. champions
        .Where(t => liveModelByKind.TryGetValue(t.Kind, out var live) && live is not null)
        .Select(t => (Champion: t, Live: liveModelByKind[t.Kind], Tested: runs
            .Where(r => r.TemplateId == t.Id && r.TemplateVersion == t.Version && pinnedCases.Contains(r.CaseId))
            .OrderByDescending(r => r.StartedAt).Select(r => r.Result.Model).FirstOrDefault()))
        .Where(x => x.Tested is null || x.Tested != x.Live)];

    /// <summary>Control runs per kind and gym client: the same stored prompts through different harnesses. whetstone chooses nothing from it.</summary>
    public static IReadOnlyList<HarnessLine> Harnesses(IReadOnlyList<GymRun> runs, IReadOnlyList<GymCase> cases)
    {
        var kindOf = cases.ToDictionary(c => c.CaseId, c => c.TaskKind, StringComparer.Ordinal);
        return [.. runs
            .Where(r => r.Arm == GymArms.Control && kindOf.ContainsKey(r.CaseId))
            .GroupBy(r => (Kind: kindOf[r.CaseId], r.Client))
            .Select(g => new HarnessLine(g.Key.Kind, g.Key.Client, g.Count(), g.Count(r => r.Result.Completed == true),
                Trials.Median([.. g.Where(r => r.Result.CostUsd is not null).Select(r => r.Result.CostUsd!.Value)]),
                Trials.Median([.. g.Where(r => r.Result.DurationMs is not null).Select(r => (double)r.Result.DurationMs!.Value)])))
            .OrderBy(l => l.Kind, StringComparer.Ordinal).ThenBy(l => l.Client, StringComparer.Ordinal)];
    }
}
