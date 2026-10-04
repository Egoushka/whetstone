using System.Text.Json;
using System.Text.Json.Nodes;
using Whetstone.Contracts;

namespace Whetstone.Tests;

/// <summary>The C# contract types read every valid schema example and write it back unchanged.</summary>
public class ContractTests
{
    private static readonly string Examples = Path.Combine(AppContext.BaseDirectory, "schemas");

    public static TheoryData<string> EnhanceExamples() => Valid("enhance");

    public static TheoryData<string> FeedbackExamples() => Valid("feedback");

    public static TheoryData<string> ExportExamples() => Valid("export");

    [Theory]
    [MemberData(nameof(ExportExamples))]
    public void Export_example_round_trips(string file) =>
        AssertRoundTrips<ExportRecord>(JsonNode.Parse(File.ReadAllText(file))!);

    [Theory]
    [MemberData(nameof(ExportExamples))]
    public void Export_example_validates_against_the_embedded_schema(string file) =>
        Assert.Empty(ContractSchemas.ValidateExport(JsonDocument.Parse(File.ReadAllText(file)).RootElement));

    [Theory]
    [InlineData("invalid-missing-prompt.json")]
    [InlineData("invalid-score-out-of-range.json")]
    [InlineData("invalid-unknown-field.json")]
    public void Invalid_export_example_is_refused_by_the_embedded_schema(string name) =>
        Assert.NotEmpty(ContractSchemas.ValidateExport(JsonDocument.Parse(File.ReadAllText(Path.Combine(Examples, "export", "v1", "examples", name))).RootElement));

    [Theory]
    [MemberData(nameof(EnhanceExamples))]
    public void Enhance_example_round_trips(string file)
    {
        var example = JsonNode.Parse(File.ReadAllText(file))!;

        AssertRoundTrips<EnhanceRequest>(example["request"]!);
        AssertRoundTrips<EnhanceResponse>(example["response"]!);
    }

    [Theory]
    [MemberData(nameof(FeedbackExamples))]
    public void Feedback_example_round_trips(string file) =>
        AssertRoundTrips<FeedbackRequest>(JsonNode.Parse(File.ReadAllText(file))!);

    [Fact]
    public void Missing_deadline_takes_the_schema_default()
    {
        var request = JsonSerializer.Deserialize<EnhanceRequest>("""{"prompt":"p"}""", ContractJson.Options)!;

        Assert.Equal(1500, request.DeadlineMs);
    }

    [Fact]
    public void Unknown_member_is_rejected() =>
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<FeedbackRequest>("""{"request_id":"r","outcome":{},"extra":1}""", ContractJson.Options));

    private static void AssertRoundTrips<T>(JsonNode wire)
    {
        var value = wire.Deserialize<T>(ContractJson.Options)!;
        var written = JsonSerializer.SerializeToNode(value, ContractJson.Options)!;

        foreach (var (name, expected) in wire.AsObject())
        {
            Assert.True(JsonNode.DeepEquals(expected, written[name]), $"{typeof(T).Name}.{name}: {expected?.ToJsonString()} != {written[name]?.ToJsonString()}");
        }

        // A member the example leaves out may be written with its default, never as null: the schemas allow null only
        // where an example shows it.
        AssertNoInventedNulls(wire.AsObject(), written.AsObject(), typeof(T).Name);
    }

    private static void AssertNoInventedNulls(JsonObject wire, JsonObject written, string path)
    {
        foreach (var (name, actual) in written)
        {
            Assert.True(actual is not null || wire.ContainsKey(name), $"{path}.{name} is written as null");
            if (actual is JsonObject nested && wire[name] is JsonObject expected)
            {
                AssertNoInventedNulls(expected, nested, $"{path}.{name}");
            }
        }
    }

    private static TheoryData<string> Valid(string tool) =>
        new(Directory.GetFiles(Path.Combine(Examples, tool, "v1", "examples"), "valid-*.json").Order());
}
