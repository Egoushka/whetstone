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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Client = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Hints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ablation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Level = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentRequestId = null);

/// <summary>
/// The <c>enhance</c> response. A pass-through (<see cref="Changed"/> false) is a valid answer, not an error.
/// <paramref name="SourceRequestId"/> names the stored request a retrieval drew on; left out when there was none.
/// </summary>
public sealed record EnhanceResponse(
    string Prompt,
    bool Changed,
    string? TemplateId,
    string? TemplateVersion,
    string Reason,
    string? TaskKind,
    string RequestId,
    bool HeldOut,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceRequestId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Advice = null)
{
    /// <summary>Which arm this answer was (<c>champion</c>, <c>challenger</c>, <c>held_out</c>); kept in the store, not sent to the client.</summary>
    [JsonIgnore]
    public string? Arm { get; init; }

    /// <summary>The traits drawn for this answer, on or off by trait id (goal 0.7, decision 2); kept in the store, not sent to the client.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, bool>? Traits { get; init; }
}

/// <summary>The <c>brief</c> request (schemas/brief/v1): how to brief a subagent of a kind. Answered as an enhance call for that kind.</summary>
public sealed record BriefRequest(
    string TaskKind, string Prompt, string? Repository = null, string? Commit = null, string? Client = null, string? SessionId = null,
    string? ParentRequestId = null, int DeadlineMs = EnhanceRequest.DefaultDeadlineMs)
{
    public const string DefaultClient = "brief";

    public EnhanceRequest ToEnhance() =>
        new(Prompt, new EnhanceContext(Repository, Commit, TaskKind, Client ?? DefaultClient, SessionId, ParentRequestId: ParentRequestId), DeadlineMs);
}
