using System.Globalization;
using Whetstone.Contracts;
using Whetstone.Templates;

namespace Whetstone.Gym;

/// <summary>Arms of a replay pair: the stored prompt as written, and the same prompt framed by the challenger.</summary>
public static class GymArms
{
    public const string Control = "control";

    public const string Challenger = "challenger";
}

/// <summary>What the gym asks its client to run: one prompt, pinned to a repository and commit.</summary>
public sealed record ReplayRequest(string CaseId, string Repository, string Commit, string Prompt, string Arm);

/// <summary>What the client reports for one replay. <paramref name="Answer"/> is passed to the judge and never stored.</summary>
public sealed record ReplayResult(
    bool? Completed, string? Status, string? ClientRunId = null, string? Preset = null, string? Model = null, string? Effort = null,
    long? TokensIn = null, long? TokensOut = null, long? CacheReadTokens = null, long? CacheWriteTokens = null, double? CostUsd = null,
    long? DurationMs = null, double? EvidenceResolved = null, string? Answer = null);

/// <summary>A gym client (ADR 0006): runs a prompt at a commit and reports how it went. It runs the agent; whetstone does not.</summary>
public interface IReplayRunner
{
    string Client { get; }

    Task<ReplayResult> RunAsync(ReplayRequest request, CancellationToken ct);
}

/// <summary>A pairwise judge: which answer better does what the original prompt asked. The order it sees is flipped on alternate cases.</summary>
public interface IJudge
{
    string Model { get; }

    /// <summary><c>control</c>, <c>challenger</c> or <c>tie</c>, and what the call cost.</summary>
    Task<(string Winner, double? CostUsd)> JudgeAsync(string prompt, string control, string challenger, bool flipped, CancellationToken ct);
}

/// <summary>One stored replay run.</summary>
public sealed record GymRun(
    string RunId, string CaseId, string Arm, string? TemplateId, string? TemplateVersion, string Client, ReplayResult Result, DateTimeOffset StartedAt);

/// <summary>One stored judge verdict for a case and a challenger version.</summary>
public sealed record GymVerdict(string CaseId, string TemplateId, string TemplateVersion, string JudgeModel, string Winner, bool OrderFlipped, double? CostUsd, DateTimeOffset DecidedAt);

/// <summary>What one <c>gym run</c> did.</summary>
public sealed record RunReport(int Cases, int Ran, int ReusedControls, int Skipped, double Spent, bool BudgetReached);

/// <summary>
/// Runs a challenger against the stored prompts of its kind (docs/specs/2026-10-19-replay-gym-design.md, decision 2): per case a control
/// (reused when one ran in the last <see cref="ControlReuse"/>) and the challenger, in alternating order, one run at a time, until the
/// budget would be passed. A case the challenger already ran on is skipped. Nothing here feeds a live trial.
/// </summary>
public static class GymReplay
{
    public static readonly TimeSpan ControlReuse = TimeSpan.FromDays(7);

    /// <summary>What a run is assumed to cost before the first one reports, so the budget check has a number (the research's $0.045, rounded up).</summary>
    public const double FirstRunEstimate = 0.05;

    public static async Task<(List<GymRun> Runs, List<GymVerdict> Verdicts, RunReport Report)> RunAsync(
        IReadOnlyList<GymCase> cases, IReadOnlyDictionary<string, string> prompts, Template challenger, IReadOnlyList<GymRun> earlier,
        IReplayRunner runner, IJudge? judge, double budgetUsd, int maxCases, Func<DateTimeOffset> now, CancellationToken ct)
    {
        var runs = new List<GymRun>();
        var verdicts = new List<GymVerdict>();
        double spent = 0, estimate = FirstRunEstimate;
        int ran = 0, reused = 0, skipped = 0, done = 0;
        var budgetReached = false;
        foreach (var (c, index) in cases.Where(c => c.TaskKind == challenger.Kind).Select((c, i) => (c, i)))
        {
            if (done == maxCases)
                break;
            if (earlier.Any(r => r.CaseId == c.CaseId && r.Arm == GymArms.Challenger && r.TemplateId == challenger.Id && r.TemplateVersion == challenger.Version)
                || !prompts.TryGetValue(c.SourceRequestId, out var prompt)
                || challenger.Render(prompt, new EnhanceContext(c.Repository, c.Commit, c.TaskKind)) is not { } framed)
            {
                skipped++;
                continue;
            }
            var control = earlier.Concat(runs).Where(r => r.CaseId == c.CaseId && r.Arm == GymArms.Control && r.StartedAt >= now() - ControlReuse)
                .OrderByDescending(r => r.StartedAt).FirstOrDefault();
            var needed = (control is null ? 2 : 1) * estimate;
            if (spent + needed > budgetUsd)
            {
                budgetReached = true;
                break;
            }
            // Alternate which arm runs first, so a drift during the night does not favour one arm.
            var order = index % 2 == 0 ? new[] { GymArms.Control, GymArms.Challenger } : [GymArms.Challenger, GymArms.Control];
            GymRun? fresh = null;
            string? controlAnswer = null, challengerAnswer = null;
            foreach (var arm in order)
            {
                if (arm == GymArms.Control && control is not null)
                {
                    reused++;
                    continue;
                }
                var at = now();
                var result = await runner.RunAsync(new ReplayRequest(c.CaseId, c.Repository, c.Commit, arm == GymArms.Control ? prompt : framed, arm), ct);
                var run = new GymRun(Id(c.CaseId, arm, at), c.CaseId, arm, arm == GymArms.Challenger ? challenger.Id : null,
                    arm == GymArms.Challenger ? challenger.Version : null, runner.Client, result with { Answer = null }, at);
                runs.Add(run);
                ran++;
                spent += result.CostUsd ?? estimate;
                if (result.CostUsd is { } cost)
                    estimate = Math.Max(estimate, cost);
                if (arm == GymArms.Control)
                    (fresh, controlAnswer) = (run, result.Answer);
                else
                    challengerAnswer = result.Answer;
            }
            done++;
            // A reused control's answer was not kept, so only a pair run tonight can be judged.
            if (judge is not null && fresh is not null && controlAnswer is { Length: > 0 } a && challengerAnswer is { Length: > 0 } b)
            {
                var flipped = index % 2 == 1;
                var (winner, cost) = await judge.JudgeAsync(prompt, a, b, flipped, ct);
                spent += cost ?? 0;
                verdicts.Add(new GymVerdict(c.CaseId, challenger.Id, challenger.Version, judge.Model, winner, flipped, cost, now()));
            }
        }
        return (runs, verdicts, new RunReport(cases.Count(c => c.TaskKind == challenger.Kind), ran, reused, skipped, spent, budgetReached));
    }

    private static string Id(string caseId, string arm, DateTimeOffset at) =>
        $"{caseId[..12]}-{arm}-{at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The screen's verdict for one challenger version over its pairs.</summary>
public sealed record ScreenReport(
    int Pairs, int ControlCompleted, int ChallengerCompleted, double? MedianCostRatio, int CostPairs, int Wins, int Losses, int Ties, string Verdict, string Why);

/// <summary>
/// The screen (decision 2): a challenger passes when, over at least <see cref="MinPairs"/> pairs on the same model, it completes at least as
/// often as the control, its median paired cost ratio is at most <see cref="MaxCostRatio"/>, and the judge prefers it in at least
/// <see cref="MinJudgeShare"/> of the pairs it decided. A pass means only that it may enter a live trial.
/// </summary>
public static class Screen
{
    public const int MinPairs = 8;

    public const double MaxCostRatio = 1.10;

    public const double MinJudgeShare = 0.55;

    public static ScreenReport Of(IReadOnlyList<GymRun> runs, IReadOnlyList<GymVerdict> verdicts, string templateId, string version)
    {
        var pairs = new List<(GymRun Control, GymRun Challenger)>();
        foreach (var challenger in runs.Where(r => r.Arm == GymArms.Challenger && r.TemplateId == templateId && r.TemplateVersion == version && r.Result.Completed is not null))
        {
            // The control nearest in time on the same model: a pair on two models compares the models, not the prompts.
            var control = runs.Where(r => r.CaseId == challenger.CaseId && r.Arm == GymArms.Control && r.Result.Completed is not null && r.Result.Model == challenger.Result.Model)
                .OrderBy(r => Math.Abs((r.StartedAt - challenger.StartedAt).Ticks)).FirstOrDefault();
            if (control is not null)
                pairs.Add((control, challenger));
        }
        var ratios = pairs.Where(p => p.Control.Result.CostUsd is > 0 && p.Challenger.Result.CostUsd is not null)
            .Select(p => p.Challenger.Result.CostUsd!.Value / p.Control.Result.CostUsd!.Value).ToList();
        var ratio = Trials.Median(ratios);
        var mine = verdicts.Where(v => v.TemplateId == templateId && v.TemplateVersion == version).ToList();
        int wins = mine.Count(v => v.Winner == GymArms.Challenger), losses = mine.Count(v => v.Winner == GymArms.Control), ties = mine.Count - wins - losses;
        int controlDone = pairs.Count(p => p.Control.Result.Completed == true), challengerDone = pairs.Count(p => p.Challenger.Result.Completed == true);
        (string Verdict, string Why) Decide()
        {
            if (pairs.Count < MinPairs)
                return ("not enough pairs", $"{pairs.Count} of {MinPairs} pairs on one model");
            if (challengerDone < controlDone)
                return ("screen fail", $"completes less often ({challengerDone} against {controlDone})");
            if (ratios.Count < MinPairs)
                return ("not enough pairs", $"cost is reported for {ratios.Count} of {MinPairs} pairs");
            if (ratio > MaxCostRatio)
                return ("screen fail", $"median cost ratio {ratio.Value.ToString("0.00", CultureInfo.InvariantCulture)} is above {MaxCostRatio.ToString("0.00", CultureInfo.InvariantCulture)}");
            if (wins + losses == 0)
                return ("not enough pairs", "the judge decided no pair");
            if (wins < MinJudgeShare * (wins + losses))
                return ("screen fail", $"the judge preferred it in {wins} of {wins + losses} decided pairs");
            return ("screen pass", "completes as often, costs no more than 10% extra, and the judge prefers it; it may enter a live trial");
        }
        var (verdict, why) = Decide();
        return new ScreenReport(pairs.Count, controlDone, challengerDone, ratio, ratios.Count, wins, losses, ties, verdict, why);
    }
}
