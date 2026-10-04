using System.Text.Json;

namespace Whetstone.Contracts;

/// <summary>Serializer options matching the wire format of schemas/*/v1: snake_case names, unknown members rejected.</summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
}
