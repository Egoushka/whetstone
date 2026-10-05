using System.Text.RegularExpressions;

namespace Whetstone.Retrieval;

/// <summary>A prompt as the words the search sees: lower case, redaction markers removed so they never match each other.</summary>
public static partial class Words
{
    public const int MaxTerms = 64;

    public const int MinLength = 3;

    public static string Normalise(string prompt) =>
        string.Join(' ', WordPattern().Matches(Marker().Replace(prompt, " ")).Select(m => m.Value.ToLowerInvariant()));

    /// <summary>The distinct words worth searching for, in order of appearance, at most <see cref="MaxTerms"/>.</summary>
    public static string[] Terms(string prompt) =>
        [.. Normalise(prompt).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= MinLength).Distinct(StringComparer.Ordinal).Take(MaxTerms)];

    /// <summary>The terms as an FTS5 query: each one quoted, any may match.</summary>
    public static string MatchAny(IEnumerable<string> terms) => string.Join(" OR ", terms.Select(t => $"\"{t}\""));

    [GeneratedRegex(@"\[REDACTED:[a-z-]+\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordPattern();
}
