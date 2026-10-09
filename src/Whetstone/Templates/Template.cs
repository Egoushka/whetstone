using System.Text;
using System.Text.RegularExpressions;
using Whetstone.Contracts;

namespace Whetstone.Templates;

/// <summary>Where a template version came from (docs/specs/2026-10-10-templates-everywhere-design.md, decision 7).</summary>
public static class TemplateSources
{
    /// <summary>Written by hand and shipped with whetstone.</summary>
    public const string BuiltIn = "built-in";
}

/// <summary>What a template is doing for its kind.</summary>
public static class TemplateRoles
{
    public const string Champion = "champion";

    public const string Challenger = "challenger";

    public const string Retired = "retired";
}

/// <summary>
/// Versioned text that frames a prompt of one kind: <paramref name="Before"/> and <paramref name="After"/> around the original,
/// which is never changed (design decision 3). A version is immutable; new text is a new version. The slots
/// <c>{{repository}}</c>, <c>{{commit}}</c> and <c>{{task_kind}}</c> are filled from the request's context.
/// </summary>
public sealed partial record Template(string Id, string Version, string Kind, string Before, string After, string Source)
{
    public static readonly IReadOnlySet<string> Slots = new HashSet<string>(["repository", "commit", "task_kind"], StringComparer.Ordinal);

    [GeneratedRegex(@"\{\{(\w+)\}\}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Slot();

    /// <summary>The slot names a text uses that whetstone does not fill; a template that has any is not served.</summary>
    public IEnumerable<string> UnknownSlots() =>
        Slot().Matches(Before + "\n" + After).Select(m => m.Groups[1].Value).Where(name => !Slots.Contains(name)).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The prompt framed by the template, or null when the template cannot be served as it should (a slot it does not know, or a
    /// result that does not hold the prompt byte for byte). A slot with no value drops its whole line.
    /// </summary>
    public string? Render(string prompt, EnhanceContext? context)
    {
        if (UnknownSlots().Any())
            return null;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["repository"] = context?.Repository,
            ["commit"] = context?.Commit,
            ["task_kind"] = context?.TaskKind,
        };
        var text = new StringBuilder();
        var before = Fill(Before, values);
        if (before.Length > 0)
            text.Append(before).Append("\n\n");
        text.Append(prompt);
        var after = Fill(After, values);
        if (after.Length > 0)
            text.Append("\n\n").Append(after);
        var result = text.ToString();
        return result.Contains(prompt, StringComparison.Ordinal) ? result : null;
    }

    private static string Fill(string text, Dictionary<string, string?> values)
    {
        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var missing = false;
            var filled = Slot().Replace(line, m =>
            {
                if (values[m.Groups[1].Value] is { Length: > 0 } value)
                    return value;
                missing = true;
                return "";
            });
            if (!missing)
                lines.Add(filled.TrimEnd('\r'));
        }
        return string.Join("\n", lines).Trim('\n');
    }
}

/// <summary>The templates a kind has now: at most one champion and one challenger.</summary>
public sealed record KindTemplates(Template? Champion, Template? Challenger)
{
    public static readonly KindTemplates None = new(null, null);
}

/// <summary>A template version as stored, with the role it has.</summary>
public sealed record TemplateRow(Template Template, string Role, DateTimeOffset CreatedAt);

/// <summary>The template store (ADR 0004), per user, in the same file as the requests.</summary>
public interface ITemplates
{
    Task<KindTemplates> ForKindAsync(string kind, CancellationToken ct);

    /// <summary>Stores versions that are not there yet; a version already stored is left as it is, role included.</summary>
    Task SeedAsync(IReadOnlyList<Template> templates, string role, CancellationToken ct);
}
