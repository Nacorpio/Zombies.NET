using System.Text.Json;
using System.Text.RegularExpressions;
using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

public sealed class ProfessionException(string message) : Exception(message);

public sealed record ProfessionItemDto
{
    /// <summary>Content ID of the Item, such as <c>base:item/bandage</c>.</summary>
    public required string Item { get; init; }

    /// <summary>How many the player starts with. Defaults to 1.</summary>
    public int Count { get; init; } = 1;
}

public sealed record ProfessionModifierDto
{
    /// <summary>Name of the Stat to change, such as <c>treatment_speed</c>.</summary>
    public required string Stat { get; init; }

    /// <summary><c>add</c> to add the value, or <c>multiply</c> to scale the Stat by it.</summary>
    public required ModifierOperation Operation { get; init; }

    public required double Value { get; init; }
}

/// <summary>
/// JSON shape of a Profession. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="ProfessionJson"/>.
/// </summary>
public sealed record ProfessionDto
{
    /// <summary>Content ID in the form <c>namespace:profession/name</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Items the player always starts carrying.</summary>
    public IReadOnlyList<ProfessionItemDto> Items { get; init; } = [];

    /// <summary>Content IDs of loot tables rolled once each for more starting items, such as <c>base:loot/medical</c>.</summary>
    public IReadOnlyList<string> Loot { get; init; } = [];

    /// <summary>Content IDs of the Items the player starts wearing. Each must be a Wearable.</summary>
    public IReadOnlyList<string> Outfit { get; init; } = [];

    /// <summary>Changes to Stats the player has for the whole life of their character.</summary>
    public IReadOnlyList<ProfessionModifierDto> Modifiers { get; init; } = [];
}

/// <summary>An Item and how many of it a Profession hands out.</summary>
public sealed record StartingItem(ItemId Item, int Count);

/// <summary>A change to one Stat that a Profession gives its character. The Server applies it with the Profession as source.</summary>
public sealed record ProfessionModifier(StatName Stat, ModifierOperation Operation, double Value)
{
    public Modifier From(ModifierSource source) => new(Stat, Operation, Value, source);
}

/// <summary>
/// What a player starts with and is good at: a loadout, what they wear, and Modifiers. A player picks one when they join,
/// and each player in a co-op world picks their own.
/// </summary>
public sealed record Profession(
    string Id,
    IReadOnlyList<StartingItem> Items,
    IReadOnlyList<string> LootTables,
    IReadOnlyList<ItemId> Outfit,
    IReadOnlyList<ProfessionModifier> Modifiers);

/// <summary>Parses a Profession from JSON.</summary>
public static partial class ProfessionJson
{
    public static Profession Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ProfessionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProfessionDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ProfessionException($"Invalid Profession: {ex.Message}");
        }

        if (dto is null)
        {
            throw new ProfessionException("A Profession must be a JSON object.");
        }

        if (!IdPattern().IsMatch(dto.Id))
        {
            throw new ProfessionException($"'id' value '{dto.Id}' is not a Content ID of the form namespace:profession/name.");
        }

        var items = dto.Items.Select(i => new StartingItem(ParseItem(i.Item, dto.Id, "items"), i.Count > 0 ? i.Count : throw new ProfessionException($"Profession '{dto.Id}' starts with {i.Count} of '{i.Item}'; the count must be at least 1."))).ToList();
        var outfit = dto.Outfit.Select(o => ParseItem(o, dto.Id, "outfit")).ToList();
        if (outfit.Distinct().Count() != outfit.Count)
        {
            throw new ProfessionException($"Profession '{dto.Id}' lists the same Item twice in its outfit.");
        }

        foreach (var table in dto.Loot)
        {
            ParseItem(table, dto.Id, "loot");
        }

        var modifiers = dto.Modifiers.Select(m => ParseModifier(m, dto.Id)).ToList();
        return new Profession(dto.Id, items, [.. dto.Loot], outfit, modifiers);
    }

    private static ItemId ParseItem(string text, string profession, string field) =>
        ItemId.TryParse(text, out var id) ? id : throw new ProfessionException($"Profession '{profession}' has '{text}' in '{field}', which is not a valid Content ID.");

    private static ProfessionModifier ParseModifier(ProfessionModifierDto dto, string profession)
    {
        if (!StatName.TryParse(dto.Stat, out var stat))
        {
            throw new ProfessionException($"Profession '{profession}' has a Modifier with an invalid 'stat' value '{dto.Stat}'.");
        }

        return Enum.IsDefined(dto.Operation) && double.IsFinite(dto.Value)
            ? new ProfessionModifier(stat, dto.Operation, dto.Value)
            : throw new ProfessionException($"Profession '{profession}' has a Modifier on '{dto.Stat}' with an unknown operation or a value that is not a number.");
    }

    [GeneratedRegex("^[a-z0-9_]+:profession/[a-z0-9_]+(/[a-z0-9_]+)*$")]
    private static partial Regex IdPattern();
}

/// <summary>Every Profession the loaded mods declare.</summary>
public sealed class ProfessionCatalog
{
    private readonly Dictionary<string, Profession> _professions = new(StringComparer.Ordinal);

    /// <exception cref="ArgumentException">A Profession is defined twice.</exception>
    public ProfessionCatalog(IEnumerable<Profession> professions)
    {
        ArgumentNullException.ThrowIfNull(professions);
        foreach (var profession in professions)
        {
            if (!_professions.TryAdd(profession.Id, profession))
            {
                throw new ArgumentException($"Duplicate Profession '{profession.Id}'.", nameof(professions));
            }
        }
    }

    /// <summary>Every Profession, ordered by Content ID.</summary>
    public IReadOnlyList<Profession> All => [.. _professions.Values.OrderBy(p => p.Id, StringComparer.Ordinal)];

    public bool TryGet(string id, out Profession profession) => _professions.TryGetValue(id, out profession!);
}
