using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Crafting;

public sealed class AreaTypeDefinitionException(string message) : Exception(message);

/// <summary>A loot table an Area type can use, with how Danger level shifts its weight.</summary>
public sealed record WeightedLootTableDto
{
    /// <summary>Content ID of the loot table, such as <c>base:loot/kitchen</c>.</summary>
    public required string Table { get; init; }

    /// <summary>Chance of this table relative to the others. Defaults to 1.</summary>
    public int Weight { get; init; } = 1;

    /// <summary>Added to the weight for each point of Danger level. Defaults to 0.</summary>
    public int DangerWeightShift { get; init; }
}

public sealed record AreaLootRuleDto
{
    /// <summary>Name of the container kind, such as <c>cabinet</c> or <c>fridge</c>.</summary>
    public required string ContainerKind { get; init; }

    public required IReadOnlyList<WeightedLootTableDto> Tables { get; init; }
}

/// <summary>
/// JSON shape of an Area type definition. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="AreaTypeJson"/>.
/// </summary>
public sealed record AreaTypeDto
{
    /// <summary>Content ID in the form <c>namespace:area_type/name</c>, such as <c>base:area_type/kitchen</c>.</summary>
    public required string Id { get; init; }

    public required IReadOnlyList<AreaLootRuleDto> Rules { get; init; }
}

/// <summary>Parses an Area type definition from JSON.</summary>
public static class AreaTypeJson
{
    public static AreaTypeDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        AreaTypeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AreaTypeDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new AreaTypeDefinitionException($"Invalid Area type definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new AreaTypeDefinitionException("An Area type definition must be a JSON object.");
        }

        try
        {
            return new AreaTypeDefinition(
                dto.Id,
                dto.Rules.Select(r => new AreaLootRule(r.ContainerKind, r.Tables.Select(t => new WeightedLootTable(t.Table, t.Weight, t.DangerWeightShift)))));
        }
        catch (ArgumentException ex)
        {
            throw new AreaTypeDefinitionException($"Area type '{dto.Id}' is invalid: {ex.Message}");
        }
    }
}
