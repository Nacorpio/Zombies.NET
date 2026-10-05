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

    /// <summary>Finds the member of enum <typeparamref name="T"/> that <paramref name="text"/> names, ignoring case. A number never names a member.</summary>
    public static bool TryParseName<T>(string? text, out T value)
        where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}
