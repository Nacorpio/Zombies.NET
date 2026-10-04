using UnitsNet;

namespace Zombies.Domain.Items;

/// <summary>Definition of an Item: what one unit weighs, how much room it takes, and how many fit in a Stack.</summary>
public sealed record ItemDefinition
{
    public ItemDefinition(ItemId id, Mass unitMass, Volume unitVolume, int maxStack)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(unitMass.Kilograms, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(unitVolume.CubicMeters, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStack, 1);
        Id = id;
        UnitMass = unitMass;
        UnitVolume = unitVolume;
        MaxStack = maxStack;
    }

    public ItemId Id { get; }

    public Mass UnitMass { get; }

    public Volume UnitVolume { get; }

    public int MaxStack { get; }
}
