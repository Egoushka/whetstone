using Whetstone.Contracts;

namespace Whetstone;

/// <summary>Goal 0.1: every prompt comes back unchanged. Also the answer whenever the real enhancer misses the deadline or fails.</summary>
public sealed class PassThrough : IEnhancer
{
    public const string NothingLearned = "pass-through: nothing learned yet";

    public Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct) =>
        Task.FromResult(Of(request, NothingLearned));

    public static EnhanceResponse Of(EnhanceRequest request, string reason) =>
        new(request.Prompt, Changed: false, TemplateId: null, TemplateVersion: null, reason, TaskKind: null, NewRequestId(), HeldOut: false);

    public static string NewRequestId() => $"req-{Guid.NewGuid():N}";
}
