namespace Whetstone.Storage;

/// <summary>Keeps nothing: whetstone as it was before goal 0.2, and for tests that do not look at storage.</summary>
public sealed class NullStore : IStore
{
    public static readonly NullStore Instance = new();

    public long Failures => 0;

    public Task RecordAsync(RequestRow row, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> RecordOutcomeAsync(OutcomeRow outcome, CancellationToken ct) => Task.FromResult(false);
}
