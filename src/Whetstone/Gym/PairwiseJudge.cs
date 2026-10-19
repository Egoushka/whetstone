using Whetstone.Templates;

namespace Whetstone.Gym;

/// <summary>
/// The gym's judge (ADR 0006): one call to the model the owner configured for the writer, which sees the original prompt and two answers
/// and names the better one or a tie. The answers are shown in the order the caller gives (<c>flipped</c> puts the challenger first), so a
/// preference for the first answer cancels out over cases. Answers are cut to <see cref="MaxAnswerChars"/>; nothing is stored.
/// </summary>
public sealed class PairwiseJudge(IChallengerWriter writer, WriterSettings settings) : IJudge
{
    public const int MaxAnswerChars = 6000;

    private const string System =
        "You compare two answers to the same request. Decide which answer does what the request asked better: correct, complete, grounded in "
        + "specifics, and no longer than it needs to be. Ignore order and style. Reply with exactly one word: A, B or TIE.";

    public string Model => settings.Model;

    public async Task<(string Winner, double? CostUsd)> JudgeAsync(string prompt, string control, string challenger, bool flipped, CancellationToken ct)
    {
        var (a, b) = flipped ? (challenger, control) : (control, challenger);
        var user = $"Request:\n{Cut(prompt)}\n\nAnswer A:\n{Cut(a)}\n\nAnswer B:\n{Cut(b)}";
        var reply = await writer.CompleteAsync(System, user, 8, ct);
        var cost = reply.TokensIn is { } tin && reply.TokensOut is { } tout ? settings.Cost(tin, tout) : (double?)null;
        var word = reply.Text.Trim().Trim('.', '"', '\'', '*').ToUpperInvariant();
        var winner = word switch
        {
            "A" => flipped ? GymArms.Challenger : GymArms.Control,
            "B" => flipped ? GymArms.Control : GymArms.Challenger,
            _ => "tie",
        };
        return (winner, cost);
    }

    private static string Cut(string text) => text.Length <= MaxAnswerChars ? text : text[..MaxAnswerChars];
}
