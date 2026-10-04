using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnitsNet;

namespace Zombies.Domain.Items;

public sealed class ItemDefinitionException(string message) : Exception(message);

/// <summary>
/// JSON shape of an Item definition. Mass and volume are UnitsNet strings such as "1.2 kg".
/// This type is the source of the generated JSON Schema, so keep it in step with <see cref="ItemDefinitionJson"/>.
/// </summary>
public sealed record ItemDefinitionDto
{
    /// <summary>Content ID in the form <c>namespace:path</c>, such as <c>base:item/canned_beans</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Mass of one unit, as a quantity string such as "0.4 kg" or "25 g".</summary>
    public required string Mass { get; init; }

    /// <summary>Volume of one unit, as a quantity string such as "350 ml" or "1 l".</summary>
    public required string Volume { get; init; }

    /// <summary>How many units fit in one Stack. Defaults to 1.</summary>
    public int MaxStack { get; init; } = 1;
}

/// <summary>Parses an Item definition from JSON.</summary>
public static class ItemDefinitionJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static ItemDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ItemDefinitionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ItemDefinitionDto>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new ItemDefinitionException($"Invalid Item definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new ItemDefinitionException("Item definition must be a JSON object.");
        }

        if (!ItemId.TryParse(dto.Id, out var itemId))
        {
            throw new ItemDefinitionException($"'id' value '{dto.Id}' is not a valid Content ID.");
        }

        var mass = ParseQuantity(dto.Mass, "mass", Mass.Parse);
        var volume = ParseQuantity(dto.Volume, "volume", Volume.Parse);

        try
        {
            return new ItemDefinition(itemId, mass, volume, dto.MaxStack);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ItemDefinitionException($"Item '{dto.Id}' has an out-of-range value: {ex.ParamName}.");
        }
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
