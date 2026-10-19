using Whetstone.Contracts;
using Whetstone.Kinds;
using Whetstone.Redaction;

namespace Whetstone.Storage;

/// <summary>
/// One <c>enhance</c> call as stored (docs/privacy.md). The only way to make one is <see cref="From"/>, which redacts every
/// free-text field and cuts the prompt, so a store cannot be handed text that skipped the redactor (ADR 0003). The answer's
/// rewrite is not kept: it is derived, and rebuilt when retrieval needs it.
/// </summary>
public sealed record RequestRow
{
    /// <summary>Longest stored prompt, in characters, after redaction.</summary>
    public const int MaxPromptChars = 32_000;

    public const string CutMarker = "\n[CUT]";

    private RequestRow()
    {
    }

    public required string RequestId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The prompt as sent, redacted, cut at <see cref="MaxPromptChars"/>.</summary>
    public required string Prompt { get; init; }

    public string? Repository { get; init; }

    public string? Commit { get; init; }

    public string? TaskKind { get; init; }

    public string? Client { get; init; }

    /// <summary>The client's opaque session id; requests that share it share a session.</summary>
    public string? SessionId { get; init; }

    /// <summary>What the client held out for the session (an instruction rule or a skill version), when it ran an ablation.</summary>
    public string? Ablation { get; init; }

    public required bool Changed { get; init; }

    public string? TemplateId { get; init; }

    public string? TemplateVersion { get; init; }

    public required bool HeldOut { get; init; }

    public required bool Truncated { get; init; }

    /// <summary>The stored request this one's answer drew on (retrieval, goal 0.3); null for a pass-through.</summary>
    public string? SourceRequestId { get; init; }

    /// <summary>Which arm the answer was (champion, challenger, held_out); null when no template trial applied.</summary>
    public string? Arm { get; init; }

    /// <summary>The traits drawn, as a JSON object of trait id to on or off; null when the kind has none.</summary>
    public string? Traits { get; init; }

    /// <summary>
    /// Why this prompt corrects the session's previous request (<see cref="Corrections"/>); null when it does not, or when the client
    /// sent no session id. Kept on the previous row, not this one.
    /// </summary>
    public string? Corrects { get; init; }

    public static RequestRow From(EnhanceRequest request, EnhanceResponse response, DateTimeOffset at)
    {
        // Redact the whole prompt first and cut after: a cut first could leave half a secret that no pattern matches.
        var prompt = Redactor.Redact(request.Prompt).Text;
        var truncated = prompt.Length > MaxPromptChars;
        if (truncated)
            prompt = Cut(prompt);
        var context = request.Context;
        return new RequestRow
        {
            RequestId = response.RequestId,
            CreatedAt = at,
            Prompt = prompt,
            Repository = Clean(context?.Repository),
            Commit = Clean(context?.Commit),
            TaskKind = Clean(context?.TaskKind ?? response.TaskKind),
            Client = Clean(context?.Client),
            SessionId = Clean(context?.SessionId),
            Ablation = Clean(context?.Ablation),
            Changed = response.Changed,
            TemplateId = Clean(response.TemplateId),
            TemplateVersion = Clean(response.TemplateVersion),
            HeldOut = response.HeldOut,
            Truncated = truncated,
            SourceRequestId = response.SourceRequestId,
            Arm = response.Arm,
            Traits = response.Traits is { Count: > 0 } traits ? System.Text.Json.JsonSerializer.Serialize(traits) : null,
            Corrects = context?.SessionId is null ? null : Corrections.Reason(request.Prompt),
        };
    }

    /// <summary>Redacts the whole text first and cuts after, as for a prompt.</summary>
    internal static string CutRedacted(string text)
    {
        var redacted = Redactor.Redact(text).Text;
        return redacted.Length > MaxPromptChars ? Cut(redacted) : redacted;
    }

    private static string Cut(string prompt)
    {
        var keep = MaxPromptChars - CutMarker.Length;
        // Never end on half of a surrogate pair.
        if (char.IsHighSurrogate(prompt[keep - 1]))
            keep--;
        return prompt[..keep] + CutMarker;
    }

    internal static string? Clean(string? value) => value is null ? null : Redactor.Redact(value).Text;
}

/// <summary>What <c>feedback</c> reported for a request, redacted like a row.</summary>
public sealed record OutcomeRow
{
    private OutcomeRow()
    {
    }

    public required string RequestId { get; init; }

    public required DateTimeOffset At { get; init; }

    public bool? RewriteAccepted { get; init; }

    public bool? ModelOverridden { get; init; }

    public double? Score { get; init; }

    public decimal? CostUsd { get; init; }

    public string? Model { get; init; }

    public bool? Completed { get; init; }

    public long? TokensIn { get; init; }

    public long? TokensOut { get; init; }

    public long? CacheReadTokens { get; init; }

    public long? CacheWriteTokens { get; init; }

    public long? DurationMs { get; init; }

    public long? ToolCalls { get; init; }

    public bool? AskedAgain { get; init; }

    public string? Effort { get; init; }

    public long? Commits { get; init; }

    public bool? PrMerged { get; init; }

    public bool? CiFirstPushGreen { get; init; }

    public bool? Reverted { get; init; }

    public bool? ItemClosed { get; init; }

    public string? CorrectionReason { get; init; }

    public long? ReviewFindings { get; init; }

    public long? ReviewFindingsSevere { get; init; }

    public long? PersonMs { get; init; }

    /// <summary>The client's model rewrite, redacted and cut like the prompt it pairs with.</summary>
    public string? RewrittenPrompt { get; init; }

    public static OutcomeRow From(FeedbackRequest request, DateTimeOffset at) => new()
    {
        RequestId = request.RequestId,
        At = at,
        RewriteAccepted = request.Outcome.RewriteAccepted,
        ModelOverridden = request.Outcome.ModelOverridden,
        Score = request.Outcome.Score,
        CostUsd = request.Outcome.CostUsd,
        Model = RequestRow.Clean(request.Outcome.Model),
        Completed = request.Outcome.Completed,
        TokensIn = request.Outcome.TokensIn,
        TokensOut = request.Outcome.TokensOut,
        CacheReadTokens = request.Outcome.CacheReadTokens,
        CacheWriteTokens = request.Outcome.CacheWriteTokens,
        DurationMs = request.Outcome.DurationMs,
        ToolCalls = request.Outcome.ToolCalls,
        AskedAgain = request.Outcome.AskedAgain,
        Effort = RequestRow.Clean(request.Outcome.Effort),
        Commits = request.Outcome.Commits,
        PrMerged = request.Outcome.PrMerged,
        CiFirstPushGreen = request.Outcome.CiFirstPushGreen,
        Reverted = request.Outcome.Reverted,
        ItemClosed = request.Outcome.ItemClosed,
        CorrectionReason = request.Outcome.CorrectionReason,
        ReviewFindings = request.Outcome.ReviewFindings,
        ReviewFindingsSevere = request.Outcome.ReviewFindingsSevere,
        PersonMs = request.Outcome.PersonMs,
        RewrittenPrompt = request.Outcome.RewrittenPrompt is { } rewrite ? RequestRow.CutRedacted(rewrite) : null,
    };
}
