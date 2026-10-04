namespace Whetstone.Storage;

/// <summary>
/// Makes any <see cref="IStore"/> safe to call from a request (ADR 0003): a failing, blocked or late store never changes an
/// answer. Every failure is counted and handed to <paramref name="onFailure"/> as an exception type, never a message (it may
/// quote a prompt). Only the caller's own cancellation escapes; <paramref name="ct"/> is the time the call may take.
/// </summary>
public sealed class GuardedStore(IStore inner, Action<string>? onFailure = null) : IStore
{
    private long _failures;

    public long Failures => Interlocked.Read(ref _failures);

    public Task RecordAsync(RequestRow row, CancellationToken ct) =>
        Guard(token => inner.RecordAsync(row, token), ct);

    public async Task<bool> RecordOutcomeAsync(OutcomeRow outcome, CancellationToken ct)
    {
        var found = false;
        await Guard(async token => found = await inner.RecordOutcomeAsync(outcome, token), ct);
        return found;
    }

    private async Task Guard(Func<CancellationToken, Task> call, CancellationToken ct)
    {
        // Task.Run: a store that throws or blocks before its first await must not hold the answer either.
        var work = Task.Run(() => call(ct), CancellationToken.None);
        try
        {
            await Task.WhenAny(work, Task.Delay(Timeout.Infinite, ct));
        }
        catch (OperationCanceledException)
        {
            // Out of time; handled below.
        }
        if (!work.IsCompleted)
        {
            // Observe a late failure so it never surfaces as an unobserved task exception.
            _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            Failed("timeout");
            return;
        }
        try
        {
            await work;
        }
        catch (Exception e)
        {
            Failed(e.GetType().Name);
        }
    }

    private void Failed(string reason)
    {
        Interlocked.Increment(ref _failures);
        onFailure?.Invoke(reason);
    }
}
