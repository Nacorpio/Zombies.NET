using System.Globalization;
using System.Text.Json;
using UnitsNet;

namespace Zombies.Domain.Items;

public sealed class ItemDefinitionException(string message) : Exception(message);

/// <summary>Parses an Item definition from JSON where mass and volume are UnitsNet strings such as "1.2 kg".</summary>
public static class ItemDefinitionJson
{
    public static ItemDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var id = ReadString(root, "id");
        if (!ItemId.TryParse(id, out var itemId))
        {
            throw new ItemDefinitionException($"'id' value '{id}' is not a valid Content ID.");
        }

        var mass = ParseQuantity(ReadString(root, "mass"), "mass", Mass.Parse);
        var volume = ParseQuantity(ReadString(root, "volume"), "volume", Volume.Parse);
        var maxStack = root.TryGetProperty("maxStack", out var stack) && stack.TryGetInt32(out var parsed) ? parsed : 1;

        try
        {
            return new ItemDefinition(itemId, mass, volume, maxStack);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ItemDefinitionException($"Item '{id}' has an out-of-range value: {ex.ParamName}.");
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        throw new ItemDefinitionException($"Missing or non-string '{name}'.");
    }

    private static T ParseQuantity<T>(string text, string name, Func<string, IFormatProvider?, T> parse)
    {
        try
        {
            return parse(text, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is UnitsNetException or FormatException or ArgumentException)
        {
            throw new ItemDefinitionException($"'{name}' value '{text}' is not a valid quantity.");
        }
    }
}
