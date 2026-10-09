using System.Text.Json.Serialization;

namespace Whetstone.Contracts;

/// <summary>One line of <c>whetstone export</c> (schemas/export/v1): a stored call and what feedback reported for it.</summary>
/// <param name="CreatedAt">UTC, as stored.</param>
/// <param name="Outcome">Null until the client reports.</param>
public sealed record ExportRecord(
    string RequestId, string CreatedAt, string Prompt, bool Truncated, ExportContext Context, ExportAnswer Answer, ExportOutcome? Outcome);

/// <summary>A field the client did not send is left out, as the schema has no null there.</summary>
public sealed record ExportContext(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Repository = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Commit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Client = null);

/// <param name="SourceRequestId">The stored request a retrieval drew on; left out when there was none.</param>
public sealed record ExportAnswer(
    bool Changed, string? TemplateId, string? TemplateVersion, bool HeldOut,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceRequestId = null);

/// <summary>The run measures are left out when the client did not report them, as the schema has no null there.</summary>
public sealed record ExportOutcome(
    bool? RewriteAccepted, bool? ModelOverridden, double? Score, double? CostUsd, string? Model, string ReportedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Completed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokensIn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokensOut = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheReadTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheWriteTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ToolCalls = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AskedAgain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Effort = null);
