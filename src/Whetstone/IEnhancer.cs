using Whetstone.Contracts;

namespace Whetstone;

/// <summary>Turns a prompt into a better one. The caller owns the deadline and falls back to a pass-through.</summary>
public interface IEnhancer
{
    Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct);
}
