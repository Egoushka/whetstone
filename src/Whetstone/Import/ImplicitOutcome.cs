using System.Text.RegularExpressions;

namespace Whetstone.Import;

/// <summary>
/// A score for a prompt nobody rated, from what the person did next (docs/specs/2026-10-09-it-imports-design.md): the next prompt
/// in the same session corrects, praises or moves on, and a session that ended with the person's own commit earns a bonus. The
/// weights are guesses; eligibility compares a score with the user's median, so only their order matters.
/// </summary>
public static partial class ImplicitOutcome
{
    public const double Correction = 0.2;
    public const double Praise = 0.9;
    public const double MovedOn = 0.6;
    public const double Last = 0.5;
    public const double Committed = 0.2;

    private const int Window = 300;

    // English and Ukrainian, matched on the start of the next prompt. A correction wins over praise ("ok but that's wrong").
    [GeneratedRegex(@"^(no|nope|wrong|stop|undo|revert|ні|нє|стоп|неправильно)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CorrectionStart();

    [GeneratedRegex(@"\b(that'?s (wrong|not (it|right|what))|not what i (asked|meant|wanted)|(doesn'?t|didn'?t|does not|did not) work"
        + @"|still (fails|failing|broken|wrong|doesn'?t|does not)|you (broke|missed|forgot|ignored)|(revert|undo) (it|that|this)"
        + @"|try again|не так|не працює|не спрацювало|знову не|відкоти|ти зламав|ти забув)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CorrectionAnywhere();

    [GeneratedRegex(@"^(thanks|thank you|great|perfect|nice|good|lgtm|ok|okay|yes|yep|cool|works|дякую|супер|добре|ок|так|чудово)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PraiseStart();

    [GeneratedRegex(@"\b(go ahead|do it|ship it|merge it|looks good|works now|it works|працює)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PraiseAnywhere();

    /// <summary>The base score a prompt earns from the one typed after it.</summary>
    public static double Judge(string nextPrompt)
    {
        var text = nextPrompt.Trim().ToLowerInvariant();
        if (text.Length > Window)
            text = text[..Window];
        if (CorrectionStart().IsMatch(text) || CorrectionAnywhere().IsMatch(text))
            return Correction;
        if (PraiseStart().IsMatch(text) || PraiseAnywhere().IsMatch(text))
            return Praise;
        return MovedOn;
    }

    /// <summary>One score per prompt of a session, in order: judged by the next prompt, the last one <see cref="Last"/>.</summary>
    public static IReadOnlyList<double> Scores(IReadOnlyList<string> prompts, bool committed)
    {
        var bonus = committed ? Committed : 0.0;
        var scores = new double[prompts.Count];
        for (var i = 0; i < prompts.Count; i++)
        {
            var score = i + 1 < prompts.Count ? Judge(prompts[i + 1]) : Last;
            scores[i] = Math.Round(Math.Min(1.0, score + bonus), 2);
        }
        return scores;
    }
}
