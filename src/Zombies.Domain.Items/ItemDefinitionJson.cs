using System.Globalization;
using System.Text.Json;
using UnitsNet;

namespace Zombies.Domain.Items;

public sealed class ItemDefinitionException(string message) : Exception(message);

/// <summary>A Status effect that consuming an item may apply.</summary>
public sealed record ConsumeEffectDto
{
    /// <summary>Content ID of the Status effect, such as <c>base:status_effect/painkiller</c>.</summary>
    public required string Effect { get; init; }

    /// <summary>Chance from 0 to 1 that the effect is applied. Defaults to 1.</summary>
    public double Chance { get; init; } = 1;
}

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

    /// <summary>Whether the item can be eaten. Defaults to false.</summary>
    public bool Edible { get; init; }

    /// <summary>Whether the item can be drunk. Defaults to false.</summary>
    public bool Drinkable { get; init; }

    /// <summary>Status effects that consuming the item may apply. Only edible or drinkable items can have them.</summary>
    public IReadOnlyList<ConsumeEffectDto> OnConsume { get; init; } = [];
}

/// <summary>Parses an Item definition from JSON.</summary>
public static class ItemDefinitionJson
{
    public static ItemDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ItemDefinitionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ItemDefinitionDto>(json, DefinitionJson.Options);
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
            return new ItemDefinition(itemId, mass, volume, dto.MaxStack, dto.Edible, dto.Drinkable, dto.OnConsume.Select(ToConsume));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ItemDefinitionException($"Item '{dto.Id}' has an out-of-range value: {ex.ParamName}.");
        }
        catch (ArgumentException ex)
        {
            throw new ItemDefinitionException($"Item '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static ConsumeEffect ToConsume(ConsumeEffectDto dto) =>
        new(dto.Effect, double.IsFinite(dto.Chance) && dto.Chance is >= 0 and <= 1 ? (int)Math.Round(dto.Chance * ConsumeEffect.BasisPoints) : throw new ArgumentException("A chance must be between 0 and 1."));

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
