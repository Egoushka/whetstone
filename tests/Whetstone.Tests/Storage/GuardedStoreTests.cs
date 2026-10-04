using System.Diagnostics;
using Whetstone.Contracts;
using Whetstone.Storage;

namespace Whetstone.Tests.Storage;

/// <summary>A failing, blocked or late store never changes an answer; it is counted, and only its exception type is reported.</summary>
public class GuardedStoreTests
{
    private static readonly RequestRow Row = RequestRow.From(
        new EnhanceRequest("a prompt the store must not echo"), new EnhanceResponse("p", false, null, null, "", null, "req-1", false), DateTimeOffset.UnixEpoch);

    private sealed class Scripted(Func<CancellationToken, Task> record) : IStore
    {
        public long Failures => 0;

        public Task RecordAsync(RequestRow row, CancellationToken ct) => record(ct);

        public Task<bool> RecordOutcomeAsync(OutcomeRow outcome, CancellationToken ct) => record(ct).ContinueWith(_ => true, TaskScheduler.Default);
    }

    [Fact]
    public async Task A_working_store_counts_no_failure()
    {
        var guarded = new GuardedStore(new Scripted(_ => Task.CompletedTask));

        await guarded.RecordAsync(Row, CancellationToken.None);

        Assert.Equal(0, guarded.Failures);
    }

    [Fact]
    public async Task A_failing_store_is_counted_and_only_its_type_is_reported()
    {
        var reported = new List<string>();
        var guarded = new GuardedStore(new Scripted(_ => throw new InvalidOperationException("cannot store 'a prompt the store must not echo'")), reported.Add);

        await guarded.RecordAsync(Row, CancellationToken.None);

        Assert.Equal(1, guarded.Failures);
        Assert.Equal([nameof(InvalidOperationException)], reported);
    }

    [Fact]
    public async Task A_blocked_store_is_cut_off_at_the_budget()
    {
        var reported = new List<string>();
        var guarded = new GuardedStore(new Scripted(_ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }), reported.Add);
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var clock = Stopwatch.StartNew();

        await guarded.RecordAsync(Row, budget.Token);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(1, guarded.Failures);
        Assert.Equal(["timeout"], reported);
    }

    [Fact]
    public async Task A_late_failure_after_the_budget_is_observed()
    {
        var guarded = new GuardedStore(new Scripted(async _ =>
        {
            await Task.Delay(300, CancellationToken.None);
            throw new IOException("late");
        }));
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await guarded.RecordAsync(Row, budget.Token);
        await Task.Delay(600);

        Assert.Equal(1, guarded.Failures);
    }
}
