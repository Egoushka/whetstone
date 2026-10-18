using System.Globalization;
using System.Text;
using System.Text.Json;
using Whetstone.Contracts;
using Whetstone.Redaction;
using Whetstone.Storage;

namespace Whetstone.Templates;

/// <summary>
/// The model that writes challengers (docs/specs/2026-10-19-evidence-volume-design.md, decision 3), as the owner configured it: an
/// OpenAI-compatible chat endpoint, a model, the spend limits and the prices that turn tokens into dollars. All of it is a setting; none
/// has a default, and a model is called only when every one is given (<c>WHETSTONE_WRITER_*</c>).
/// </summary>
public sealed record WriterSettings(Uri Endpoint, string Model, string Key, double MaxUsdPerCall, double MaxUsdPerWeek, double PriceInPerMillion, double PriceOutPerMillion)
{
    public const string Usage =
        "WHETSTONE_WRITER_URL, WHETSTONE_WRITER_MODEL, WHETSTONE_WRITER_KEY, WHETSTONE_WRITER_MAX_USD_CALL, WHETSTONE_WRITER_MAX_USD_WEEK and "
        + "WHETSTONE_WRITER_PRICE (dollars per million tokens, input then output, such as 3,15) are all needed";

    /// <summary>The settings, null when none is set (the writer is off) or, with <paramref name="problem"/>, when they are incomplete or do not read.</summary>
    public static WriterSettings? From(Func<string, string?> setting, out string? problem)
    {
        problem = null;
        string[] names = ["WHETSTONE_WRITER_URL", "WHETSTONE_WRITER_MODEL", "WHETSTONE_WRITER_KEY", "WHETSTONE_WRITER_MAX_USD_CALL", "WHETSTONE_WRITER_MAX_USD_WEEK", "WHETSTONE_WRITER_PRICE"];
        var given = names.Select(n => setting(n)).ToList();
        if (given.All(string.IsNullOrWhiteSpace))
            return null;
        if (given.Any(string.IsNullOrWhiteSpace))
        {
            problem = Usage;
            return null;
        }
        var prices = given[5]!.Split(',', StringSplitOptions.TrimEntries);
        if (!Uri.TryCreate(given[0], UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
            || !Positive(given[3], out var call) || !Positive(given[4], out var week) || prices.Length != 2
            || !Positive(prices[0], out var priceIn) || !Positive(prices[1], out var priceOut))
        {
            problem = "WHETSTONE_WRITER_URL is an http(s) address and the limits and prices are numbers above 0; " + Usage;
            return null;
        }
        return new WriterSettings(endpoint, given[1]!.Trim(), given[2]!.Trim(), call, week, priceIn, priceOut);
    }

    private static bool Positive(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && double.IsFinite(value);

    public double Cost(long tokensIn, long tokensOut) => ((tokensIn * PriceInPerMillion) + (tokensOut * PriceOutPerMillion)) / 1_000_000;
}

/// <summary>What the model answered, and the tokens it reports having used (null when it does not).</summary>
public sealed record WriterReply(string Text, long? TokensIn, long? TokensOut);

/// <summary>The one outbound call of the writer; the HTTP version is in the server project, tests use a fake.</summary>
public interface IChallengerWriter
{
    Task<WriterReply> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct);
}

/// <summary>A stored request that ended in rework, reduced to what the writer reads: redacted text, the template it got and why it was corrected.</summary>
public sealed record ReworkExample(string RequestId, string Prompt, string? TemplateId, string Reason);

/// <summary>What <see cref="ChallengerWriting.PlanAsync"/> found: the text it would send and what it would cost at most.</summary>
public sealed record WriterPlan(string Kind, string System, string User, IReadOnlyList<string> ReadRequestIds, int Retired, double EstimatedUsd, string? Refusal);

public static class ChallengerWriting
{
    public const int MaxTokens = 600;

    public const int MaxExamples = 8;

    public const int MaxExampleChars = 400;

    public const int MaxRetiredShown = 3;

    public const int MaxTextChars = 1_200;

    public const string Source = "model-written";

    /// <summary>The prompt for one kind: its champion's text, the runs that needed rework with the reasons given, and the nearest retired texts to avoid.</summary>
    public static async Task<WriterPlan> PlanAsync(string kind, IStoreAdmin admin, WriterSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        var templates = await admin.TemplatesAsync(ct);
        var champion = templates.Where(t => t.Template.Kind == kind && t.Role == TemplateRoles.Champion).Select(t => t.Template).FirstOrDefault();
        if (champion is null)
            return Refused(kind, $"{kind} has no champion to improve");
        var rework = new List<ExportRecord>();
        await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
            if (record.Context.TaskKind == kind && record.Outcome is { } o && (o.CorrectionReason is { } reason ? CorrectionReasons.IsRework(reason) : o.AskedAgain == true))
                rework.Add(record);
        if (rework.Count == 0)
            return Refused(kind, $"no run of {kind} has ended in rework yet, so there is nothing for the writer to read");
        var examples = rework.OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal).Take(MaxExamples)
            .Select(r => new ReworkExample(r.RequestId, Cut(r.Prompt), r.Answer.TemplateId, r.Outcome!.CorrectionReason ?? "asked again")).ToList();
        var retired = templates.Where(t => t.Template.Kind == kind && t.Role == TemplateRoles.Retired).Select(t => t.Template).ToList();
        var lost = retired.OrderByDescending(t => Similarity.Closest(champion, [t])?.Closeness ?? 0).Take(MaxRetiredShown).ToList();
        var system = "You improve the instruction text a tool adds after a person's prompt of one kind. Reply with the new instruction text only: "
            + $"plain prose, at most {MaxTextChars / 8} words, no preamble, no code fence. Use only the slots {{{{repository}}}}, {{{{commit}}}} and {{{{task_kind}}}} if you use any.";
        var user = new StringBuilder();
        user.Append("Kind: ").Append(kind).Append("\n\nCurrent text:\n").Append(champion.Before.Length > 0 ? champion.Before + "\n" : "").Append(champion.After).Append("\n\n");
        user.Append("Runs of this kind that needed rework (the person's prompt, then why it was corrected):\n");
        foreach (var e in examples)
            user.Append("- [").Append(e.Reason).Append("] ").Append(e.Prompt.ReplaceLineEndings(" ")).Append('\n');
        if (lost.Count > 0)
        {
            user.Append("\nTexts that were tried and lost; do not write anything close to them:\n");
            foreach (var t in lost)
                user.Append("- ").Append((t.Before + " " + t.After).Trim().ReplaceLineEndings(" ")).Append('\n');
        }
        user.Append("\nWrite one new instruction text that would have avoided those corrections.");
        // Everything sent goes through the redactor once more: the stored text is redacted already, this is the second lock.
        var (sys, usr) = (Redactor.Redact(system).Text, Redactor.Redact(user.ToString()).Text);
        var estimate = settings.Cost((sys.Length + usr.Length) / 4 + 1, MaxTokens);
        var spent = await admin.WriterSpendSinceAsync(now.AddDays(-7), ct);
        if (estimate > settings.MaxUsdPerCall)
            return new WriterPlan(kind, sys, usr, [.. examples.Select(e => e.RequestId)], lost.Count, estimate, $"the call could cost up to ${estimate:0.0000}, over the ${settings.MaxUsdPerCall:0.0000} limit per call");
        if (spent + estimate > settings.MaxUsdPerWeek)
            return new WriterPlan(kind, sys, usr, [.. examples.Select(e => e.RequestId)], lost.Count, estimate, $"${spent:0.0000} was spent in the last 7 days; another ${estimate:0.0000} would pass the ${settings.MaxUsdPerWeek:0.0000} weekly limit");
        return new WriterPlan(kind, sys, usr, [.. examples.Select(e => e.RequestId)], lost.Count, estimate, null);
    }

    private static WriterPlan Refused(string kind, string why) => new(kind, "", "", [], 0, 0, why);

    private static string Cut(string text) => text.Length <= MaxExampleChars ? text : text[..MaxExampleChars] + "…";

    /// <summary>
    /// Calls the model for a plan, records what it cost, and stores its text as a challenger unless it fails a check or is too close to a
    /// text the kind already lost (the negative library). Null when stored, else why not.
    /// </summary>
    public static async Task<(string? Problem, double Usd)> WriteAsync(
        WriterPlan plan, IStoreAdmin admin, IChallengerWriter writer, WriterSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        var reply = await writer.CompleteAsync(plan.System, plan.User, MaxTokens, ct);
        var usd = settings.Cost(reply.TokensIn ?? (plan.System.Length + plan.User.Length) / 4 + 1, reply.TokensOut ?? MaxTokens);
        // The money is spent whatever the text turns out to be.
        await admin.AddWriterSpendAsync(now, plan.Kind, settings.Model, usd, ct);
        var text = Clean(reply.Text);
        if (text.Length == 0)
            return ("the model returned no text", usd);
        if (text.Length > MaxTextChars)
            return ($"the model's text is {text.Length} characters, over the {MaxTextChars} limit", usd);
        var id = "written-" + now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var template = new Template(id, "1", plan.Kind, "", text, Source);
        if (template.UnknownSlots().ToList() is { Count: > 0 } unknown)
            return ($"the model used the slot {{{{{unknown[0]}}}}}, which whetstone does not fill", usd);
        if (template.Render("x", null) is null)
            return ("the model's text cannot be rendered", usd);
        var all = await admin.TemplatesAsync(ct);
        var champion = all.Where(t => t.Template.Kind == plan.Kind && t.Role == TemplateRoles.Champion).Select(t => t.Template);
        if (Similarity.Closest(template, champion) is { } same && same.Closeness >= Similarity.TooClose)
            return ($"the model's text is too close to the champion {same.Retired.Id} v{same.Retired.Version} (closeness {same.Closeness:0.00}); dropped", usd);
        var counts = JsonSerializer.Serialize(new { read_requests = plan.ReadRequestIds, retired_shown = plan.Retired, model = settings.Model, cost_usd = Math.Round(usd, 6) });
        var problem = await admin.AddTemplateAsync(template, TemplateRoles.Challenger, new DecisionRecord(counts, $"written by {settings.Model} from {plan.ReadRequestIds.Count} runs with rework"), ct);
        return (problem, usd);
    }

    private static string Clean(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal) && trimmed.EndsWith("```", StringComparison.Ordinal) && trimmed.Length >= 6)
            trimmed = trimmed[3..^3].Trim();
        return trimmed;
    }
}
