using System.Text.Json.Serialization;

namespace Whetstone.Contracts;

/// <summary>The <c>enhance</c> request (schemas/enhance/v1).</summary>
public sealed record EnhanceRequest(
    string Prompt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EnhanceContext? Context = null,
    int DeadlineMs = EnhanceRequest.DefaultDeadlineMs)
{
    public const int DefaultDeadlineMs = 1500;
}

/// <summary>Only the context the user allowed; never file contents or secrets. The schema has no nulls here: an unknown field is left out.</summary>
public sealed record EnhanceContext(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Repository = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Commit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Client = null);

/// <summary>The <c>enhance</c> response. A pass-through (<see cref="Changed"/> false) is a valid answer, not an error.</summary>
public sealed record EnhanceResponse(
    string Prompt,
    bool Changed,
    string? TemplateId,
    string? TemplateVersion,
    string Reason,
    string? TaskKind,
    string RequestId,
    bool HeldOut);
