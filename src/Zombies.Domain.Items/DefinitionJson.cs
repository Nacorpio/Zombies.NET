using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zombies.Domain.Items;

/// <summary>Serializer settings shared by every definition type, so parsing and generated JSON Schema agree.</summary>
public static class DefinitionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
}
