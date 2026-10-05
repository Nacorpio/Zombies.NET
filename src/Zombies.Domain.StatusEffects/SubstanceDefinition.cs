using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.StatusEffects;

public sealed class SubstanceDefinitionException(string message) : Exception(message);

/// <summary>An Item that delivers a substance, and how many doses of it one use is.</summary>
public sealed record SubstanceItem
{
    public SubstanceItem(ItemId item, int doses = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(doses, 1);
        Item = item;
        Doses = doses;
    }

    public ItemId Item { get; }

    public int Doses { get; }
}

/// <summary>What happens to an addicted creature that goes without the substance: an effect that starts <see cref="Delay"/> after the last use.</summary>
public sealed record SubstanceWithdrawal
{
    public SubstanceWithdrawal(string effect, TimeSpan delay)
    {
        if (!ItemId.TryParse(effect, out _))
        {
            throw new ArgumentException($"'{effect}' is not a valid Content ID.", nameof(effect));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        Effect = effect;
        Delay = delay;
    }

    /// <summary>Content ID of the Status effect. Its stages are how the withdrawal worsens.</summary>
    public string Effect { get; }

    public TimeSpan Delay { get; }
}

/// <summary>
/// Definition of a substance, such as a stimulant or alcohol: the Items that deliver it, the Status effect each dose
/// applies, how likely a use is to cause addiction, how fast tolerance grows, and the withdrawal that follows addiction.
/// </summary>
public sealed record SubstanceDefinition
{
    public SubstanceDefinition(
        string id,
        IEnumerable<SubstanceItem> items,
        string effect,
        double addictionChance,
        double toleranceGrowth,
        SubstanceWithdrawal? withdrawal = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        if (!ItemId.TryParse(effect, out _))
        {
            throw new ArgumentException($"'{effect}' is not a valid Content ID.", nameof(effect));
        }

        if (!double.IsFinite(addictionChance) || addictionChance is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(addictionChance), addictionChance, "A chance must be from 0 to 1.");
        }

        if (!double.IsFinite(toleranceGrowth) || toleranceGrowth is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceGrowth), toleranceGrowth, "Tolerance growth must be from 0 to 1.");
        }

        if (addictionChance > 0 && withdrawal is null)
        {
            throw new ArgumentException("A substance that can cause addiction needs a withdrawal.", nameof(withdrawal));
        }

        Items = [.. items];
        if (Items.Count == 0)
        {
            throw new ArgumentException("A substance needs at least one Item that delivers it.", nameof(items));
        }

        if (Items.Select(i => i.Item).Distinct().Count() != Items.Count)
        {
            throw new ArgumentException("A substance names an Item more than once.", nameof(items));
        }

        Id = id;
        Effect = effect;
        AddictionChance = addictionChance;
        ToleranceGrowth = toleranceGrowth;
        Withdrawal = withdrawal;
    }

    public string Id { get; }

    public IReadOnlyList<SubstanceItem> Items { get; }

    /// <summary>Content ID of the Status effect that each dose applies. Its stacking rule is how doses add up.</summary>
    public string Effect { get; }

    /// <summary>Chance, from 0 to 1, that a use makes a creature addicted. Tolerance raises it.</summary>
    public double AddictionChance { get; }

    /// <summary>How much tolerance, from 0 to 1, each dose adds.</summary>
    public double ToleranceGrowth { get; }

    public SubstanceWithdrawal? Withdrawal { get; }

    /// <summary>
    /// Name of the Stat that carries a creature's tolerance, such as <c>alcohol_tolerance</c>, taken from the last part of the Content ID.
    /// </summary>
    public StatName ToleranceStat => new($"{Id[(Id.LastIndexOf('/') + 1)..]}_tolerance");
}

/// <summary>JSON shape of an Item that delivers a substance.</summary>
public sealed record SubstanceItemDto
{
    /// <summary>Content ID of the Item, such as <c>base:item/beer</c>.</summary>
    public required string Item { get; init; }

    /// <summary>How many doses one use is. Defaults to 1.</summary>
    public int Doses { get; init; } = 1;
}

/// <summary>JSON shape of the withdrawal that follows addiction.</summary>
public sealed record SubstanceWithdrawalDto
{
    /// <summary>Content ID of the Status effect, which should progress through stages, such as <c>base:status_effect/stimulant_withdrawal</c>.</summary>
    public required string StatusEffect { get; init; }

    /// <summary>Seconds after the last use that the withdrawal starts.</summary>
    public required double Delay { get; init; }
}

/// <summary>JSON shape of a substance definition. This type is the source of the generated JSON Schema.</summary>
public sealed record SubstanceDto
{
    /// <summary>Content ID in the form <c>namespace:substance/name</c>, such as <c>base:substance/alcohol</c>.</summary>
    public required string Id { get; init; }

    public required IReadOnlyList<SubstanceItemDto> Items { get; init; }

    /// <summary>Content ID of the Status effect that each dose applies, such as <c>base:status_effect/drunk</c>.</summary>
    public required string StatusEffect { get; init; }

    /// <summary>Chance from 0 to 1 that a use makes a creature addicted. Defaults to 0.</summary>
    public double AddictionChance { get; init; }

    /// <summary>Tolerance from 0 to 1 that each dose adds. Defaults to 0.</summary>
    public double ToleranceGrowth { get; init; }

    /// <summary>What happens to an addicted creature that goes without. Required when the addiction chance is above 0.</summary>
    public SubstanceWithdrawalDto? Withdrawal { get; init; }
}

/// <summary>Parses a substance definition from JSON.</summary>
public static class SubstanceJson
{
    public static SubstanceDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        SubstanceDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SubstanceDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new SubstanceDefinitionException($"Invalid substance definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new SubstanceDefinitionException("A substance definition must be a JSON object.");
        }

        try
        {
            return new SubstanceDefinition(
                dto.Id,
                dto.Items.Select(i => new SubstanceItem(ItemId.TryParse(i.Item, out var item) ? item : throw new ArgumentException($"'{i.Item}' is not a valid Content ID."), i.Doses)),
                dto.StatusEffect,
                dto.AddictionChance,
                dto.ToleranceGrowth,
                dto.Withdrawal is { } withdrawal ? new SubstanceWithdrawal(withdrawal.StatusEffect, Seconds(withdrawal.Delay)) : null);
        }
        catch (ArgumentException ex)
        {
            throw new SubstanceDefinitionException($"Substance '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && Math.Abs(value) < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a usable number of seconds.");
}

/// <summary>The substances the game knows.</summary>
public sealed class SubstanceCatalog
{
    private readonly Dictionary<string, SubstanceDefinition> _definitions = [];
    private readonly Dictionary<ItemId, (SubstanceDefinition Substance, int Doses)> _byItem = [];

    /// <exception cref="ArgumentException">A substance is defined twice, an Item delivers two substances, or an effect is not in <paramref name="effects"/>.</exception>
    public SubstanceCatalog(IEnumerable<SubstanceDefinition> definitions, StatusEffectCatalog effects)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(effects);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException($"Substance '{definition.Id}' is defined more than once.", nameof(definitions));
            }

            string[] named = definition.Withdrawal is { } withdrawal ? [definition.Effect, withdrawal.Effect] : [definition.Effect];
            foreach (var effect in named.Where(e => !effects.TryGet(e, out _)))
            {
                throw new ArgumentException($"Substance '{definition.Id}' names unknown Status effect '{effect}'.", nameof(definitions));
            }

            foreach (var item in definition.Items)
            {
                if (!_byItem.TryAdd(item.Item, (definition, item.Doses)))
                {
                    throw new ArgumentException($"Item '{item.Item}' delivers more than one substance.", nameof(definitions));
                }
            }
        }
    }

    public IReadOnlyCollection<SubstanceDefinition> All => _definitions.Values;

    public bool TryGet(string id, out SubstanceDefinition definition) => _definitions.TryGetValue(id, out definition!);

    /// <summary>The substance an Item delivers and how many doses one use is, or false when the Item is not a substance.</summary>
    public bool TryGetForItem(ItemId item, out SubstanceDefinition substance, out int doses)
    {
        var found = _byItem.TryGetValue(item, out var entry);
        (substance, doses) = found ? entry : (null!, 0);
        return found;
    }
}
