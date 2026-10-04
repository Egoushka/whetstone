using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Whetstone.Contracts;

/// <summary>
/// The published contract schemas, embedded in this assembly. enhance/v1 holds the request and the response as $defs;
/// MCP needs each one as a schema of its own (a tool's inputSchema and outputSchema).
/// </summary>
public static class ContractSchemas
{
    public static JsonElement EnhanceRequest { get; } = Def("enhance", "request");

    public static JsonElement EnhanceResponse { get; } = Def("enhance", "response");

    public static JsonElement FeedbackRequest { get; } = Standalone(Node("feedback"));

    private static readonly JsonSchema EnhanceRequestSchema = JsonSchema.FromText(EnhanceRequest.GetRawText());

    private static readonly JsonSchema FeedbackRequestSchema = JsonSchema.FromText(FeedbackRequest.GetRawText());

    /// <summary>Errors against enhance/v1's request; empty means valid.</summary>
    public static IReadOnlyList<string> ValidateEnhance(JsonElement request) => Validate(EnhanceRequestSchema, request);

    /// <summary>Errors against feedback/v1; empty means valid.</summary>
    public static IReadOnlyList<string> ValidateFeedback(JsonElement request) => Validate(FeedbackRequestSchema, request);

    private static IReadOnlyList<string> Validate(JsonSchema schema, JsonElement document)
    {
        var result = schema.Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
            return [];
        return [.. (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Values.Select(e => $"{d.InstanceLocation}: {e}"))];
    }

    private static JsonElement Def(string tool, string name) => Standalone(Node(tool)["$defs"]![name]!.DeepClone().AsObject());

    // The $id stays off: the schema registry accepts one registration per $id, and a def is not the published document.
    private static JsonElement Standalone(JsonObject schema)
    {
        schema.Remove("$id");
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject Node(string tool)
    {
        using var stream = typeof(ContractSchemas).Assembly.GetManifestResourceStream($"v1/{tool}.schema.json")
            ?? throw new ArgumentException($"Unknown contract schema '{tool}/v1'.", nameof(tool));
        return JsonNode.Parse(stream)!.AsObject();
    }
}
