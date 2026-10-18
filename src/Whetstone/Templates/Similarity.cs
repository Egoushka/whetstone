using Whetstone.Retrieval;

namespace Whetstone.Templates;

/// <summary>
/// The negative library's near-duplicate check (docs/specs/2026-10-19-evidence-volume-design.md, decision 5): a proposed template is
/// compared with the retired texts of its kind by BM25 over the same words retrieval searches on (<see cref="Words"/>), so a rewording
/// of a text that already lost is dropped before it costs a trial.
/// </summary>
public static class Similarity
{
    /// <summary>A proposal at or above this closeness to a retired text is too close to be tried again.</summary>
    public const double TooClose = 0.8;

    private const double K1 = 1.2;

    private const double B = 0.75;

    /// <summary>The retired text a proposal is closest to, and how close (0 to 1), or null when the kind has none.</summary>
    public static (Template Retired, double Closeness)? Closest(Template proposal, IEnumerable<Template> retired)
    {
        var others = retired.Where(t => t.Kind == proposal.Kind).ToList();
        if (others.Count == 0)
            return null;
        var p = Tokens(proposal);
        var docs = others.Select(Tokens).ToList();
        var corpus = docs;
        var best = (Template: others[0], Closeness: -1.0);
        for (var i = 0; i < others.Count; i++)
        {
            // Both directions must hold: a long proposal that merely contains a retired text is not a rewording of it.
            var closeness = Math.Min(Score(p, docs[i], corpus) / Score(docs[i], docs[i], corpus), Score(docs[i], p, corpus) / Score(p, p, corpus));
            if (closeness > best.Closeness)
                best = (others[i], closeness);
        }
        return double.IsFinite(best.Closeness) ? (best.Template, Math.Clamp(best.Closeness, 0, 1)) : null;
    }

    private static string[] Tokens(Template template) =>
        [.. Words.Normalise(template.Before + "\n" + template.After).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= Words.MinLength)];

    /// <summary>BM25 of <paramref name="doc"/> for the distinct words of <paramref name="query"/>, with the idf of Lucene (never negative) over the retired texts.</summary>
    private static double Score(string[] query, string[] doc, List<string[]> corpus)
    {
        if (doc.Length == 0)
            return 0;
        var average = corpus.Average(d => d.Length);
        var frequencies = doc.GroupBy(w => w, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        double score = 0;
        foreach (var term in query.Distinct(StringComparer.Ordinal))
        {
            if (!frequencies.TryGetValue(term, out var tf))
                continue;
            // A word in none of the retired texts weighs as one in a single text, so a changed word costs what a kept one is worth.
            var n = Math.Max(1, corpus.Count(d => d.Contains(term, StringComparer.Ordinal)));
            var idf = Math.Log(1 + ((corpus.Count - n + 0.5) / (n + 0.5)));
            score += idf * tf * (K1 + 1) / (tf + (K1 * (1 - B + (B * doc.Length / average))));
        }
        return score;
    }
}
