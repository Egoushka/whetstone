using System.Text.Json.Serialization;

namespace Whetstone.Contracts;

/// <summary>What the owner decided about a template version, as <c>template_decisions</c> keeps it.</summary>
/// <param name="Counts">The report's counts at the time, as the JSON text the owner's command stored; left out when none were.</param>
public sealed record LibraryDecision(
    string DecidedAt, string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Counts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);

/// <summary>One line of <c>whetstone template export</c> (schemas/library/v1): a template version, its role and what was decided about it.</summary>
public sealed record LibraryRecord(
    string TemplateId, string Version, string Kind, string Before, string After, string Source, string Role, string CreatedAt,
    IReadOnlyList<LibraryDecision> Decisions);
