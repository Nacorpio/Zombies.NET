using UnitsNet;

namespace Zombies.Domain.Items;

/// <summary>A Status effect an Item applies when it is consumed, and the chance in basis points that it does, such as spoiled food that poisons three times in four.</summary>
public sealed record ConsumeEffect(string Effect, int ChanceBasis)
{
    public const int BasisPoints = 10_000;
}

/// <summary>Definition of an Item: what one unit weighs, how much room it takes, and how many fit in a Stack.</summary>
public sealed record ItemDefinition
{
    public ItemDefinition(ItemId id, Mass unitMass, Volume unitVolume, int maxStack, bool edible = false, bool drinkable = false, IEnumerable<ConsumeEffect>? onConsume = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(unitMass.Kilograms, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(unitVolume.CubicMeters, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStack, 1);
        Id = id;
        UnitMass = unitMass;
        UnitVolume = unitVolume;
        MaxStack = maxStack;
        Edible = edible;
        Drinkable = drinkable;
        OnConsume = [.. onConsume ?? []];
        foreach (var effect in OnConsume)
        {
            if (!ItemId.TryParse(effect.Effect, out _))
            {
                throw new ArgumentException($"'{effect.Effect}' is not a valid Content ID.", nameof(onConsume));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(effect.ChanceBasis, nameof(onConsume));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(effect.ChanceBasis, ConsumeEffect.BasisPoints, nameof(onConsume));
        }

        if (OnConsume.Count > 0 && !edible && !drinkable)
        {
            throw new ArgumentException($"Item '{id}' applies effects when consumed but can be neither eaten nor drunk.", nameof(onConsume));
        }
    }

    public ItemId Id { get; }

    public Mass UnitMass { get; }

    public Volume UnitVolume { get; }

    public int MaxStack { get; }

    /// <summary>Whether the item can be eaten.</summary>
    public bool Edible { get; }

    /// <summary>Whether the item can be drunk.</summary>
    public bool Drinkable { get; }

    /// <summary>Whether the item can be consumed, which is how Status effects are applied and cured by it.</summary>
    public bool Consumable => Edible || Drinkable;

    /// <summary>The Status effects consuming the item may apply.</summary>
    public IReadOnlyList<ConsumeEffect> OnConsume { get; }
}
