using System.Text.Json;
using Json.Schema;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Whetstone.Contracts;

namespace Whetstone.Tests;

internal static class McpHarness
{
    private static readonly JsonSchema ResponseSchema = JsonSchema.FromText(ContractSchemas.EnhanceResponse.GetRawText());

    public static Dictionary<string, object?> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

    /// <summary>The structured content of a successful enhance call, after checking it against enhance/v1's response.</summary>
    public static EnhanceResponse Enhanced(CallToolResult call)
    {
        Assert.True(call.IsError != true, string.Join(" | ", call.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        var content = call.StructuredContent!.Value;
        Assert.True(ResponseSchema.Evaluate(content).IsValid, content.GetRawText());
        return content.Deserialize<EnhanceResponse>(ContractJson.Options)!;
    }

    public static string Error(CallToolResult call)
    {
        Assert.True(call.IsError);
        return string.Join(" | ", call.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    public static Task<CallToolResult> Call(McpClient client, string tool, string json) => client.CallToolAsync(tool, Args(json)).AsTask();
}
