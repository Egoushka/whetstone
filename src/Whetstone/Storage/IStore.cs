namespace Whetstone.Storage;

/// <summary>One user's memory of what they asked and what came of it (ADR 0003). Reaches only that user's file.</summary>
public interface IStore
{
    /// <summary>Calls the store failed or ran out of time on, since the process started; 0 for a store that cannot fail.</summary>
    long Failures { get; }

    Task RecordAsync(RequestRow row, CancellationToken ct);

    /// <summary>Fills the outcome of the row with that request id; true when there was one. The last report for a request wins.</summary>
    Task<bool> RecordOutcomeAsync(OutcomeRow outcome, CancellationToken ct);
}

/// <summary>Where each user's store is. The only way code reaches a store, so a request can never name another user's.</summary>
public interface IStores
{
    IStore ForUser(string user);
}
