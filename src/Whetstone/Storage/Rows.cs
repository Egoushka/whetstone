using Whetstone.Contracts;
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

    public required bool Changed { get; init; }

    public string? TemplateId { get; init; }

    public string? TemplateVersion { get; init; }

    public required bool HeldOut { get; init; }

    public required bool Truncated { get; init; }

    /// <summary>The stored request this one's answer drew on (retrieval, goal 0.3); null for a pass-through.</summary>
    public string? SourceRequestId { get; init; }

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
            Changed = response.Changed,
            TemplateId = Clean(response.TemplateId),
            TemplateVersion = Clean(response.TemplateVersion),
            HeldOut = response.HeldOut,
            Truncated = truncated,
            SourceRequestId = response.SourceRequestId,
        };
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

    public static OutcomeRow From(FeedbackRequest request, DateTimeOffset at) => new()
    {
        RequestId = request.RequestId,
        At = at,
        RewriteAccepted = request.Outcome.RewriteAccepted,
        ModelOverridden = request.Outcome.ModelOverridden,
        Score = request.Outcome.Score,
        CostUsd = request.Outcome.CostUsd,
        Model = RequestRow.Clean(request.Outcome.Model),
    };
}
