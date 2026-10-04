using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Crafting;

/// <summary>Inclusive range of whole numbers, as <c>{ "min": 1, "max": 3 }</c>.</summary>
public sealed record IntRangeDto
{
    public required int Min { get; init; }

    public required int Max { get; init; }
}

public sealed record LootEntryDto
{
    /// <summary>Content ID of the item, such as <c>base:item/canned_beans</c>.</summary>
    public required string Item { get; init; }

    /// <summary>Chance of this entry relative to the others. Defaults to 1.</summary>
    public int Weight { get; init; } = 1;

    /// <summary>How many appear when this entry is rolled. Defaults to exactly 1.</summary>
    public IntRangeDto Count { get; init; } = new() { Min = 1, Max = 1 };
}

/// <summary>
/// JSON shape of a loot table. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="LootTableJson"/>.
/// </summary>
public sealed record LootTableDto
{
    /// <summary>Content ID in the form <c>namespace:loot/name</c>.</summary>
    public required string Id { get; init; }

    /// <summary>How many times the table is rolled each time it fills a container.</summary>
    public required IntRangeDto Rolls { get; init; }

    public required IReadOnlyList<LootEntryDto> Entries { get; init; }
}

public static class LootTableJson
{
    public static LootTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        LootTableDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<LootTableDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new LootTableException($"Invalid loot table: {ex.Message}");
        }

        if (dto is null)
        {
            throw new LootTableException("A loot table must be a JSON object.");
        }

        var entries = new List<LootEntry>();
        foreach (var entry in dto.Entries)
        {
            if (!ItemId.TryParse(entry.Item, out var item))
            {
                throw new LootTableException($"Loot table '{dto.Id}': '{entry.Item}' is not a valid Content ID.");
            }

            try
            {
                entries.Add(new LootEntry(item, entry.Weight, entry.Count.Min, entry.Count.Max));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new LootTableException($"Loot table '{dto.Id}': entry '{entry.Item}' has an out-of-range value ({ex.ParamName}).");
            }
        }

        try
        {
            return new LootTable(dto.Id, dto.Rolls.Min, dto.Rolls.Max, entries);
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            throw new LootTableException($"Loot table '{dto.Id}': {ex.Message}");
        }
    }
}
