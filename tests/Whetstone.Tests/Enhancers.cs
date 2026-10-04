using Whetstone.Contracts;

namespace Whetstone.Tests;

/// <summary>Enhancers that misbehave on purpose, to prove the client still gets an answer.</summary>
internal static class Enhancers
{
    /// <summary>Ignores the token and answers after <paramref name="delay"/>: the deadline must not wait for it.</summary>
    public static IEnhancer Slow(TimeSpan delay) => new Scripted(async (r, _) =>
    {
        await Task.Delay(delay, CancellationToken.None);
        return Rewrite(r);
    });

    /// <summary>Blocks its thread before returning a task, as a synchronous enhancer would.</summary>
    public static IEnhancer Blocking(TimeSpan delay) => new Scripted((r, _) =>
    {
        Thread.Sleep(delay);
        return Task.FromResult(Rewrite(r));
    });

    public static IEnhancer Failing() => new Scripted((_, _) => throw new InvalidOperationException("boom"));

    public static IEnhancer Rewriting() => new Scripted((r, _) => Task.FromResult(Rewrite(r)));

    public static EnhanceResponse Rewrite(EnhanceRequest r) =>
        new($"{r.Prompt}, step by step", Changed: true, "tpl-test", "1.0.0", "test rewrite", null, "req-test", HeldOut: false);

    private sealed class Scripted(Func<EnhanceRequest, CancellationToken, Task<EnhanceResponse>> answer) : IEnhancer
    {
        public Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct) => answer(request, ct);
    }
}
