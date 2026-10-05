using System.Text.Json;
using System.Text.RegularExpressions;
using UnitsNet;

namespace Zombies.Domain.Items;

public sealed class WearableDefinitionException(string message) : Exception(message);

/// <summary>
/// JSON shape of a Wearable definition: what an Item does when worn.
/// This type is the source of the generated JSON Schema, so keep it in step with <see cref="WearableJson"/>.
/// </summary>
public sealed record WearableDto
{
    /// <summary>Content ID in the form <c>namespace:wearable/name</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Content ID of the Item that is worn, such as <c>base:item/hoodie</c>.</summary>
    public required string Item { get; init; }

    /// <summary>The Layer the Item occupies: <c>underwear</c>, <c>base</c>, <c>mid</c>, <c>outer</c> or <c>armor</c>.</summary>
    public required string Layer { get; init; }

    /// <summary>The Body parts it covers, such as <c>torso</c> and <c>leftArm</c>.</summary>
    public required IReadOnlyList<string> Coverage { get; init; }

    /// <summary>Thermal insulation added on every covered part, in square meter kelvins per watt. Defaults to none.</summary>
    public double Insulation { get; init; }

    /// <summary>Fraction (0 to 1) of each damage type absorbed, by damage type name such as <c>cut</c>.</summary>
    public IReadOnlyDictionary<string, double> Protection { get; init; } = new Dictionary<string, double>();

    /// <summary>Fraction (0 to 1) of the capability of every covered part that the Item takes away. Defaults to none.</summary>
    public double Encumbrance { get; init; }
}

/// <summary>Parses a Wearable definition from JSON.</summary>
public static partial class WearableJson
{
    public static WearableDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        WearableDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WearableDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new WearableDefinitionException($"Invalid Wearable definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new WearableDefinitionException("A Wearable definition must be a JSON object.");
        }

        if (!ContentIdPattern().IsMatch(dto.Id))
        {
            throw new WearableDefinitionException($"'id' value '{dto.Id}' is not a Content ID of the form namespace:wearable/name.");
        }

        if (!ItemId.TryParse(dto.Item, out var item))
        {
            throw new WearableDefinitionException($"Wearable '{dto.Id}' has an 'item' value '{dto.Item}' that is not a valid Content ID.");
        }

        var layer = Named<ClothingLayer>(dto.Layer, dto.Id, "layer");
        var coverage = dto.Coverage.Select(part => Named<BodyPart>(part, dto.Id, "coverage")).ToList();
        var protection = dto.Protection.ToDictionary(p => Named<DamageType>(p.Key, dto.Id, "protection"), p => p.Value);
        try
        {
            return new WearableDefinition(item, layer, coverage, ThermalResistance.FromSquareMeterKelvinsPerWatt(dto.Insulation), protection, dto.Encumbrance);
        }
        catch (ArgumentException ex)
        {
            throw new WearableDefinitionException($"Wearable '{dto.Id}' has an invalid value: {ex.Message}");
        }
    }

    private static T Named<T>(string text, string id, string field)
        where T : struct, Enum =>
        DefinitionJson.TryParseName<T>(text, out var value)
            ? value
            : throw new WearableDefinitionException($"Wearable '{id}' has an unknown '{field}' value '{text}'.");

    [GeneratedRegex("^[a-z0-9_]+:wearable/[a-z0-9_]+(/[a-z0-9_]+)*$")]
    private static partial Regex ContentIdPattern();
}
