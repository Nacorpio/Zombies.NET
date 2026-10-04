using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

public sealed record ItemEquipped(ItemId Item, ClothingLayer Layer) : IDomainEvent;

public sealed record ItemUnequipped(ItemId Item) : IDomainEvent;

/// <summary>
/// What a character wears, stacked in Layers per body part. Computes the protection and insulation the worn items give,
/// scaled by their wear state (wetness and condition).
/// </summary>
public sealed class Outfit(IWearableCatalog catalog)
{
    /// <summary>Fraction of an item's insulation lost when fully wet.</summary>
    public const double WetInsulationLoss = 0.7;

    private sealed class Worn(WearableDefinition definition)
    {
        public WearableDefinition Definition { get; } = definition;

        public double Wetness { get; set; }

        public double Condition { get; set; } = 1;
    }

    private readonly List<Worn> _worn = [];

    public IReadOnlyList<ItemId> WornItems => [.. _worn.Select(w => w.Definition.Item)];

    public InventoryResult Equip(ItemId item)
    {
        if (!catalog.TryGet(item, out var definition))
        {
            return InventoryResult.Failure(InventoryError.NotWearable);
        }

        var occupied = _worn.Any(w => w.Definition.Layer == definition.Layer && w.Definition.Coverage.Overlaps(definition.Coverage));
        if (occupied)
        {
            return InventoryResult.Failure(InventoryError.LayerOccupied);
        }

        _worn.Add(new Worn(definition));
        return InventoryResult.Success(new ItemEquipped(item, definition.Layer));
    }

    public InventoryResult Unequip(ItemId item)
    {
        var index = _worn.FindIndex(w => w.Definition.Item == item);
        if (index < 0)
        {
            return InventoryResult.Failure(InventoryError.NotWorn);
        }

        _worn.RemoveAt(index);
        return InventoryResult.Success(new ItemUnequipped(item));
    }

    public InventoryResult SetWearState(ItemId item, double wetness, double condition)
    {
        if (wetness is < 0 or > 1 || condition is < 0 or > 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidWearState);
        }

        var worn = _worn.Find(w => w.Definition.Item == item);
        if (worn is null)
        {
            return InventoryResult.Failure(InventoryError.NotWorn);
        }

        worn.Wetness = wetness;
        worn.Condition = condition;
        return InventoryResult.Success();
    }

    /// <summary>Fraction (0 to 1) of a damage type absorbed on a body part, combining every worn Layer covering it.</summary>
    public double Protection(BodyPart part, DamageType type)
    {
        var passing = 1.0;
        foreach (var worn in _worn.Where(w => w.Definition.Coverage.Contains(part)))
        {
            var absorbed = worn.Definition.Protection.GetValueOrDefault(type) * worn.Condition;
            passing *= 1 - absorbed;
        }

        return 1 - passing;
    }

    /// <summary>Total insulation on a body part: worn Layers add up, and wet items insulate less.</summary>
    public ThermalResistance Insulation(BodyPart part)
    {
        var total = ThermalResistance.Zero;
        foreach (var worn in _worn.Where(w => w.Definition.Coverage.Contains(part)))
        {
            total += worn.Definition.Insulation * (1 - (WetInsulationLoss * worn.Wetness));
        }

        return total;
    }

    /// <summary>Mean insulation over every body part, which is what the whole body feels.</summary>
    public ThermalResistance AverageInsulation()
    {
        var parts = Enum.GetValues<BodyPart>();
        var sum = ThermalResistance.Zero;
        foreach (var part in parts)
        {
            sum += Insulation(part);
        }

        return sum / parts.Length;
    }
}
