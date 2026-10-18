using System.Text.RegularExpressions;

namespace Whetstone.Templates;

/// <summary>
/// The filler-stripping challenger class (docs/specs/2026-10-19-evidence-volume-design.md, decision 4): it deletes phrases that only ask
/// for effort from the original prompt. This is the one bend of "the original is never changed" (design decision 3): the change is a
/// deletion of the phrases listed here, the client shows it to the person, and the answer's <c>reason</c> names what went. Code spans
/// and fenced blocks are left as they are, and a phrase that goes on into more words ("be thorough about the edge cases") is left too,
/// since deleting it would change what was asked.
/// </summary>
public static partial class FillerStripper
{
    /// <summary>
    /// The phrases, lower case. A change to this list changes what a stored filler-stripping version does, so the owner gives the
    /// new behaviour a new version.
    /// </summary>
    public static readonly IReadOnlyList<string> Phrases =
    [
        "be thorough", "be very thorough", "be extremely thorough", "be careful", "be very careful", "be extra careful",
        "double-check everything", "double check everything", "double-check your work", "double check your work",
        "take your time", "do your best", "try your best", "try your hardest", "give it your best", "make sure it is perfect",
        "make sure it's perfect", "think very carefully", "think carefully", "think hard", "pay close attention",
    ];

    private static readonly Regex Filler = new(
        @"(?<ws>[ \t]*)(?<lead>(?:,[ \t]*)?\b(?:and|but|also|and also)[ \t]+|,[ \t]*)?(?:\bplease[ \t]+)?\b(?<phrase>"
        + string.Join('|', Phrases.OrderByDescending(p => p.Length).Select(Regex.Escape))
        + @")\b(?=[ \t]*(?:[.!,;:\n]|\r|\z))[ \t]*(?<end>[.!,;]?)[ \t]*(?<nl>\r?\n)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The prompt with the listed phrases deleted and the phrases that went; the prompt itself when none is there or nothing would be left.</summary>
    public static (string Prompt, IReadOnlyList<string> Removed) Strip(string prompt)
    {
        var removed = new List<string>();
        // Splitting on a capturing group keeps the code in the odd slots, which are not touched.
        var parts = Code().Split(prompt);
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = StripText(parts[i], removed);
        var result = string.Concat(parts);
        return removed.Count == 0 || string.IsNullOrWhiteSpace(result) ? (prompt, []) : (result, removed);
    }

    private static string StripText(string text, List<string> removed) =>
        Filler.Replace(text, m =>
        {
            removed.Add(m.Groups["phrase"].Value.ToLowerInvariant());
            var before = text[..m.Index];
            var after = text[(m.Index + m.Length)..];
            // A tail clause ("..., and be thorough.") gives its full stop to the sentence it ends.
            var keep = m.Groups["lead"].Length > 0 ? m.Groups["end"].Value : "";
            if (m.Groups["nl"].Success)
                return before.Length == 0 || before.EndsWith('\n') ? keep + (keep.Length > 0 ? m.Groups["nl"].Value : "") : keep + m.Groups["nl"].Value;
            var head = before + keep;
            var needSpace = head.Length > 0 && !char.IsWhiteSpace(head[^1]) && after.Length > 0 && !char.IsWhiteSpace(after[0]) && !".,;:!?".Contains(after[0], StringComparison.Ordinal);
            return keep + (needSpace ? " " : "");
        });

    [GeneratedRegex(@"(```[\s\S]*?```|`[^`\n]*`)", RegexOptions.CultureInvariant)]
    private static partial Regex Code();
}
