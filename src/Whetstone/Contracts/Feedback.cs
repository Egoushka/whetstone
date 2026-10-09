using System.Text.Json.Serialization;

namespace Whetstone.Contracts;

/// <summary>The <c>feedback</c> request (schemas/feedback/v1). Its response is empty.</summary>
public sealed record FeedbackRequest(string RequestId, FeedbackOutcome Outcome);

/// <summary>What happened after an <c>enhance</c> call; null where the client does not know.</summary>
/// <remarks>
/// The run measures (<c>Completed</c> on) describe the run the prompt started: a subagent, a page extraction, a session.
/// A measure the client does not have is null, never zero; tokens are counted as the provider bills them, so cache reads and
/// writes are apart from input.
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Effort = null);
