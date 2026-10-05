using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Whetstone.Contracts;
using Whetstone.Retrieval;

namespace Whetstone.Server;

/// <summary>The text of <c>whetstone replay</c>: a header, then the pairs with the closest match first.</summary>
internal static partial class ReplayText
{
    private const int Shown = 90;

    public static string Format(ReplayReport report)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{report.Total} requests, {report.Scored} scored, {report.Eligible} eligible to be retrieved\n");
        text.Append("(eligible: scored, not model-overridden, at or above the median score of its task kind; fewer than ");
        text.Append(CultureInfo.InvariantCulture, $"{Eligibility.MinScoredForKind} scored of a kind uses the overall median)\n");
        if (report.Pairs.Count == 0)
            return text.Append("\nNothing is scored yet, so there is nothing to replay.\n").ToString();
        text.Append("\nFor each scored request: relevance (BM25, higher is closer), then the request (its score) and the best earlier match.\n");
        foreach (var pair in report.Pairs.OrderByDescending(p => p.Score).ThenBy(p => p.Query.CreatedAt, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{pair.Score,6:0.00}  {Line(pair.Query)}\n");
            text.Append(pair.Match is null ? "        no match\n" : $"        {Line(pair.Match)}{Elsewhere(pair)}\n");
        }
        return text.ToString();
    }

    private static string Line(ExportRecord row) =>
        string.Create(CultureInfo.InvariantCulture, $"({row.Outcome?.Score:0.00}) {Cut(Space().Replace(row.Prompt, " ").Trim())}");

    private static string Elsewhere(ReplayPair pair) =>
        pair.Match!.Context.Repository is { } repository && repository != pair.Query.Context.Repository ? $"  [from {repository}]" : "";

    private static string Cut(string prompt) => prompt.Length <= Shown ? $"\"{prompt}\"" : $"\"{prompt[..Shown]}…\"";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
