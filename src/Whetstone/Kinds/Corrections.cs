using System.Text.RegularExpressions;
using Whetstone.Contracts;
using Whetstone.Import;

namespace Whetstone.Kinds;

/// <summary>
/// Why the next prompt of a session corrected the run before it (docs/specs/2026-10-19-reward-signals-design.md, decision 2), by
/// rules over its opening, in English and Ukrainian. Rules first: a model is a later challenger, checked against the same hand labels.
/// </summary>
public static partial class Corrections
{
    private const int Window = 300;

    // Checked in this order; the first that matches names the reason. Most specific first: "stop, wrong file" is a stop. A rule
    // marked weak names a reason only for a prompt that already reads as a correction: "just run the tests" opens a new request.
    private static readonly (string Reason, Regex Rule, bool Weak)[] Rules =
    [
        ("stop", Stop(), false), ("misread", Misread(), false), ("wrong_target", WrongTarget(), false), ("too_much", TooMuch(), false),
        ("too_much", TooMuchWeak(), true), ("style", Style(), true), ("wrong_approach", WrongApproach(), true), ("incomplete", Incomplete(), false),
        ("incomplete", IncompleteWeak(), true), ("new_info", NewInfo(), false),
    ];

    [GeneratedRegex(@"^(stop|wait|hold on|cancel|abort|стоп|зачекай|почекай|стій|скасуй)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Stop();

    [GeneratedRegex(@"\b(i meant|i asked (for|you)|not what i (asked|meant|wanted)|you misunderstood|misread|that'?s not what|я мав на увазі|я просив|не зрозумів|не те що я)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Misread();

    [GeneratedRegex(@"\b(wrong (file|place|function|method|class|repo|branch|one|test)|not (that|this) (file|one|function|place)|the other (file|one)|не той (файл|метод|клас)|не там|не в тому)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WrongTarget();

    [GeneratedRegex(@"\b(i didn'?t ask|didn'?t ask (for|you)|don'?t (change|touch)|too much|не просив|не чіпай|забагато)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TooMuch();

    [GeneratedRegex(@"\b(only (change|do|fix|touch)|just (the|do|fix)|(revert|undo) (the|those|that|it|this)|лише|тільки|відкоти)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TooMuchWeak();

    [GeneratedRegex(@"\b(shorter|too long|too verbose|less verbose|more concise|formatting|in (english|ukrainian)|коротше|задовго|українською|англійською)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Style();

    [GeneratedRegex(@"\b(instead|different approach|another approach|don'?t use|wrong approach|not like that|не так|замість|інакше|інший підхід)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WrongApproach();

    [GeneratedRegex(@"\b(you (missed|forgot|skipped)|still (fails|failing|broken|wrong|missing|doesn'?t|does not)|(doesn'?t|didn'?t|does not|did not) work|ти (забув|пропустив)|не працює|не спрацювало)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Incomplete();

    [GeneratedRegex(@"\b(forgot|missing|not (done|finished|all)|досі|ще не)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IncompleteWeak();

    [GeneratedRegex(@"\b(fyi|btw|by the way|note that|for context|context:|до речі|зверни увагу|для контексту)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NewInfo();

    /// <summary>
    /// The reason the prompt corrects the run before it, or null when it reads as moving on or praise. A prompt that reads as a
    /// correction (<see cref="ImplicitOutcome"/>) but matches no reason is <c>other</c>; a strong reason rule alone also counts, so
    /// "fyi, the file moved" is <c>new_info</c> and "wrong file" is <c>wrong_target</c>.
    /// </summary>
    public static string? Reason(string nextPrompt)
    {
        var text = nextPrompt.Trim().ToLowerInvariant();
        if (text.Length > Window)
            text = text[..Window];
        var correction = ImplicitOutcome.Judge(text) == ImplicitOutcome.Correction;
        foreach (var (reason, rule, weak) in Rules)
            if ((correction || !weak) && rule.IsMatch(text))
                return reason;
        return correction ? "other" : null;
    }

    /// <summary>Every reason a rule can give is one the contract knows.</summary>
    internal static IEnumerable<string> Known => Rules.Select(r => r.Reason).Append("other").Distinct();
}
