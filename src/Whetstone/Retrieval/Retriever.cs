using System.Text;
using Whetstone.Contracts;
using Whetstone.Redaction;

namespace Whetstone.Retrieval;

/// <summary>What the retriever needs to be told: how close a match must be before it is used. Read it off <c>whetstone replay</c>.</summary>
public sealed record RetrievalOptions(double MinRelevance);

/// <summary>
/// Goal 0.3: when the prompt resembles an earlier one that went well, add that earlier prompt to the answer; otherwise answer as
/// <paramref name="inner"/> does. Never worse than the inner answer: any failure returns it, and is counted.
/// </summary>
public sealed class Retriever(IEnhancer inner, IRetrievalIndex index, RetrievalOptions options) : IEnhancer
{
    public const string Reason = "retrieval: similar earlier prompt that went well";

    /// <summary>How many closest requests are looked at; the first one that passes the bar and differs from the prompt is used.</summary>
    public const int Candidates = 50;

    public const int MaxQuotedChars = 2_000;

    private long _failures;

    /// <summary>Lookups that failed or ran out of time since the process started.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    public async Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct)
    {
        var answer = await inner.EnhanceAsync(request, ct);
        try
        {
            var found = await FindAsync(request.Prompt, ct);
            return found is null ? answer : answer with { Prompt = Compose(request.Prompt, found), Changed = true, Reason = Reason, SourceRequestId = found.RequestId };
        }
        catch (Exception)
        {
            // Broken or out of time (the server cancels at the deadline): a miss, not an error. The inner answer stands.
            Interlocked.Increment(ref _failures);
            return answer;
        }
    }

    private async Task<Candidate?> FindAsync(string prompt, CancellationToken ct)
    {
        // The new prompt is redacted before it is matched, so a secret in it never reaches a query (decision 9).
        var redacted = Redactor.Redact(prompt).Text;
        var terms = Words.Terms(redacted);
        if (terms.Length == 0)
            return null;
        var bar = Thresholds.From(await index.ScoresAsync(ct));
        if (bar.Empty)
            return null;
        var same = Words.Normalise(redacted);
        foreach (var candidate in await index.SearchAsync(terms, Candidates, ct))
        {
            // Best first, so below the bar nothing later qualifies. An identical prompt adds nothing (decision 7).
            if (candidate.Relevance < options.MinRelevance)
                return null;
            if (bar.Passes(candidate.TaskKind, candidate.Score) && Words.Normalise(candidate.Prompt) != same)
                return candidate;
        }
        return null;
    }

    /// <summary>The original prompt untouched, then the earlier one quoted with where and when it was written (decision 3).</summary>
    internal static string Compose(string prompt, Candidate found)
    {
        var origin = found.Repository is { } repository ? $"{repository}, {found.CreatedAt[..10]}" : found.CreatedAt[..10];
        var quoted = new StringBuilder();
        foreach (var line in Cut(found.Prompt).Split('\n'))
            quoted.Append("> ").Append(line.TrimEnd('\r')).Append('\n');
        return $"{prompt}\n\n---\nA similar earlier prompt that went well ({origin}):\n{quoted.ToString().TrimEnd('\n')}";
    }

    private static string Cut(string text)
    {
        if (text.Length <= MaxQuotedChars)
            return text;
        var keep = MaxQuotedChars;
        // Never end on half of a surrogate pair.
        if (char.IsHighSurrogate(text[keep - 1]))
            keep--;
        return text[..keep] + "…";
    }
}
