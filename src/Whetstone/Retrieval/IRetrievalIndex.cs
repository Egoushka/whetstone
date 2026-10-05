namespace Whetstone.Retrieval;

/// <summary>A stored request that matched a search, with the score it was given and how closely it matched.</summary>
/// <param name="Relevance">BM25, higher is closer; it grows with the number of query words, so it is only comparable for one query.</param>
public sealed record Candidate(string RequestId, string CreatedAt, string Prompt, string? Repository, string? TaskKind, double Score, double Relevance);

/// <summary>What the retriever may read of one user's store (goal 0.3). Like <c>IStore</c>, it reaches that user's file only.</summary>
public interface IRetrievalIndex
{
    /// <summary>Every score feedback has reported, with its task kind: what "went well" is measured against.</summary>
    Task<IReadOnlyList<(string? TaskKind, double Score)>> ScoresAsync(CancellationToken ct);

    /// <summary>
    /// The closest stored requests that have a score and were not model-overridden, best first. Whether a score is high enough is
    /// the caller's to decide.
    /// </summary>
    Task<IReadOnlyList<Candidate>> SearchAsync(IReadOnlyList<string> terms, int limit, CancellationToken ct);
}
