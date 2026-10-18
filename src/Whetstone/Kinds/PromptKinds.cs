using System.Text.RegularExpressions;

namespace Whetstone.Kinds;

/// <summary>
/// The kind of a prompt, by rules (docs/specs/2026-10-10-templates-everywhere-design.md, decision 2). A subagent prompt's kind is
/// its subagent type; a call to the general-purpose agent, or to none, is refined by the first verb of its description. A prompt
/// no rule covers is <see cref="Other"/> and is never templated.
/// </summary>
public static partial class PromptKinds
{
    public const string Other = "other";

    public const string Fetch = "fetch/extract";

    public const string Orchestrator = "orchestrator/turn";

    private const string GeneralPurpose = "general-purpose";

    /// <summary>The client name for prompts a person types in Claude Code (the same one the import stores).</summary>
    public const string TypedClient = "claude-code";

    /// <summary>A typed prompt shorter than this is steering a running session ("yes", "continue"); it has no kind to try.</summary>
    public const int MinTypedChars = 40;

    /// <summary>The first verb of a description, by the job it names. The classes are few on purpose: a kind needs 30 prompts to be tried.</summary>
    private static readonly Dictionary<string, string> Verbs = Build(
        ("explore", "explore find search locate list survey map scan read look inspect trace grep identify gather collect show"),
        ("research", "research investigate compare analyze analyse summarize summarise study evaluate explain understand describe inventory mine classify diagnose"),
        ("review", "review audit verify validate assess critique check test confirm re"),
        ("implement", "implement add fix write build create update refactor migrate port rename remove apply patch wire extend change translate improve tidy"),
        ("ship", "land merge rebase release deploy sync publish push open"));

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NotAlphanumeric();

    [GeneratedRegex(@"[A-Za-z]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Word();

    /// <summary>The kind of the prompt an agent wrote for a subagent.</summary>
    public static string ForAgent(string? subagentType, string? description)
    {
        var type = Slug(subagentType);
        if (type is not null && type != GeneralPurpose)
            return $"agent/{type}";
        var first = Word().Match(description ?? "");
        return first.Success && Verbs.TryGetValue(first.Value.ToLowerInvariant(), out var job) ? $"agent/{GeneralPurpose}/{job}" : $"agent/{GeneralPurpose}";
    }

    /// <summary>
    /// The kind of a prompt a person typed, by its opening words (design decision 2). A short prompt, or one that opens with no word
    /// the table knows, is <see cref="Other"/>. Continuing and shipping work are kinds so that they are recorded, but they have
    /// no template: they are held out entirely and serve as the baseline.
    /// </summary>
    public static string ForTyped(string? prompt)
    {
        var text = (prompt ?? "").Trim();
        if (text.Length < MinTypedChars)
            return Other;
        var words = Word().Matches(text.Length > 120 ? text[..120] : text).Select(m => m.Value.ToLowerInvariant()).Take(3).ToList();
        foreach (var word in words)
            if (TypedVerbs.TryGetValue(word, out var job))
                return $"typed/{job}";
        return Other;
    }

    private static readonly Dictionary<string, string> TypedVerbs = Build(
        ("continue", "continue resume proceed"),
        ("ship", "merge push rebase release deploy ship"),
        ("debug", "fix debug diagnose troubleshoot broken failing fails crash"),
        ("review", "review audit critique verify validate"),
        ("research", "research investigate compare explain analyze analyse explore find summarize summarise understand"),
        ("implement", "implement add create build write refactor migrate rename remove update extend change make"));

    private static string? Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var slug = NotAlphanumeric().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    private static Dictionary<string, string> Build(params (string Job, string Words)[] classes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (job, words) in classes)
            foreach (var word in words.Split(' '))
                map[word] = job;
        return map;
    }
}
