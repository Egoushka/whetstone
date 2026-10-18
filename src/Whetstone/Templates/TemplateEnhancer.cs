using Whetstone.Contracts;
using Whetstone.Kinds;

namespace Whetstone.Templates;

/// <summary>
/// Design decisions 3 and 4: a request of a kind with a champion gets that template, a challenger or nothing, by the arm its id
/// hashes to. A request with no kind, or of kind <c>other</c>, is not templated and is not an arm. A kind with no champion is
/// held out entirely: answered as <paramref name="inner"/> does, and recorded as the baseline. Any failure returns the inner answer.
/// </summary>
public sealed class TemplateEnhancer(IEnhancer inner, ITemplates templates) : IEnhancer
{
    private long _failures;

    /// <summary>The enhancer this one adds to, so a health report can still reach it.</summary>
    public IEnhancer Inner => inner;

    /// <summary>Lookups that failed or ran out of time since the process started.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>The kind the client sent, or, for a typed Claude Code prompt that came with none, the one its opening words give; null when not templated.</summary>
    private static string? KindOf(EnhanceRequest request)
    {
        var kind = request.Context?.TaskKind is { Length: > 0 } sent ? sent
            : request.Context?.Client == PromptKinds.TypedClient ? PromptKinds.ForTyped(request.Prompt) : null;
        return kind is null or PromptKinds.Other ? null : kind;
    }

    public async Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct)
    {
        var answer = await inner.EnhanceAsync(request, ct);
        if (KindOf(request) is not { } kind)
            return answer;
        try
        {
            var found = await templates.ForKindAsync(kind, ct);
            var arm = found.Champion is null ? Arms.HeldOut : Arms.Assign(kind, answer.RequestId, found.Challenger is not null);
            var chosen = arm switch { Arms.Champion => found.Champion, Arms.Challenger => found.Challenger, _ => null };
            var held = answer with { HeldOut = arm == Arms.HeldOut, TaskKind = kind, Arm = arm };
            if (chosen is null || chosen.Render(answer.Prompt, request.Context) is not { } prompt)
                return held;
            return held with
            {
                Prompt = prompt,
                Changed = prompt != answer.Prompt,
                TemplateId = chosen.Id,
                TemplateVersion = chosen.Version,
                Reason = $"template: {chosen.Id} v{chosen.Version} ({arm})",
            };
        }
        catch (Exception)
        {
            // Broken or out of time (the server cancels at the deadline): the inner answer, not an arm, since none was served.
            Interlocked.Increment(ref _failures);
            return answer;
        }
    }
}
