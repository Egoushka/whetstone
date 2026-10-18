using System.Text.Json.Serialization;

namespace Whetstone.Contracts;

/// <summary>The <c>feedback</c> request (schemas/feedback/v1). Its response is empty.</summary>
public sealed record FeedbackRequest(string RequestId, FeedbackOutcome Outcome);

/// <summary>Why the next prompt corrected a run (<see cref="FeedbackOutcome.CorrectionReason"/>).</summary>
public static class CorrectionReasons
{
    public static readonly IReadOnlyList<string> All = ["wrong_target", "incomplete", "wrong_approach", "misread", "too_much", "style", "new_info", "stop", "other"];

    /// <summary>A correction that blames the prompt or the run; <c>new_info</c>, <c>stop</c> and <c>other</c> do not.</summary>
    public static bool IsRework(string? reason) => reason is "wrong_target" or "incomplete" or "wrong_approach" or "misread" or "too_much" or "style";
}

/// <summary>What happened after an <c>enhance</c> call; null where the client does not know.</summary>
/// <remarks>
/// The run measures (<c>Completed</c> on) describe the run the prompt started: a subagent, a page extraction, a session.
/// A measure the client does not have is null, never zero; tokens are counted as the provider bills them, so cache reads and
/// writes are apart from input.
/// The reward measures (<c>Commits</c> on, docs/specs/2026-10-19-reward-signals-design.md) often arrive later, in another report for
/// the same request: delayed outcomes, the reason the next prompt corrected the run, review findings on the produced change and the
/// person's time.
/// </remarks>
public sealed record FeedbackOutcome(
    bool? RewriteAccepted = null,
    bool? ModelOverridden = null,
    double? Score = null,
    decimal? CostUsd = null,
    string? Model = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Completed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokensIn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokensOut = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheReadTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheWriteTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ToolCalls = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AskedAgain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Effort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Commits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PrMerged = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? CiFirstPushGreen = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Reverted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ItemClosed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrectionReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ReviewFindings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ReviewFindingsSevere = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PersonMs = null);
