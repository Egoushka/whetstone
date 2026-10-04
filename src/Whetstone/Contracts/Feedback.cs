namespace Whetstone.Contracts;

/// <summary>The <c>feedback</c> request (schemas/feedback/v1). Its response is empty.</summary>
public sealed record FeedbackRequest(string RequestId, FeedbackOutcome Outcome);

/// <summary>What happened after an <c>enhance</c> call; null where the client does not know.</summary>
public sealed record FeedbackOutcome(
    bool? RewriteAccepted = null,
    bool? ModelOverridden = null,
    double? Score = null,
    decimal? CostUsd = null,
    string? Model = null);
