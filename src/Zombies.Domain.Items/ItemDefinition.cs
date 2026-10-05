using UnitsNet;

namespace Zombies.Domain.Items;

/// <summary>Definition of an Item: what one unit weighs, how much room it takes, and how many fit in a Stack.</summary>
public sealed record ItemDefinition
{
    public ItemDefinition(ItemId id, Mass unitMass, Volume unitVolume, int maxStack, bool edible = false, bool drinkable = false, double fatigueRelief = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fatigueRelief, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fatigueRelief, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(unitMass.Kilograms, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(unitVolume.CubicMeters, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStack, 1);
        Id = id;
        UnitMass = unitMass;
        UnitVolume = unitVolume;
        MaxStack = maxStack;
        Edible = edible;
        Drinkable = drinkable;
        FatigueRelief = fatigueRelief;
    }

    public ItemId Id { get; }

    public Mass UnitMass { get; }

    public Volume UnitVolume { get; }

    public int MaxStack { get; }

    /// <summary>Whether the item can be eaten.</summary>
    public bool Edible { get; }

    /// <summary>Whether the item can be drunk.</summary>
    public bool Drinkable { get; }

    /// <summary>The fraction of fatigue consuming one unit takes away, from 0 to 1.</summary>
    public double FatigueRelief { get; }
}
