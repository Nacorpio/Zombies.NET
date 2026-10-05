using UnitsNet;

namespace Zombies.Domain.Items;

/// <summary>
/// What a worn Item does: the Layer it occupies on each body part in its Coverage, the thermal insulation it adds there,
/// the fraction of each damage type it absorbs, and how much it encumbers the parts it covers.
/// </summary>
public sealed class WearableDefinition
{
    public WearableDefinition(
        ItemId item,
        ClothingLayer layer,
        IEnumerable<BodyPart> coverage,
        ThermalResistance insulation,
        IReadOnlyDictionary<DamageType, double>? protection = null,
        double encumbrance = 0)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var covered = new HashSet<BodyPart>(coverage);
        if (covered.Count == 0)
        {
            throw new ArgumentException("A wearable must cover at least one body part.", nameof(coverage));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(insulation.SquareMeterKelvinsPerWatt, 0);
        if (!double.IsFinite(encumbrance) || encumbrance is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(encumbrance), encumbrance, "Encumbrance must be between 0 and 1.");
        }

        var absorbed = new Dictionary<DamageType, double>(protection ?? new Dictionary<DamageType, double>());
        foreach (var (type, fraction) in absorbed)
        {
            if (fraction is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(protection), $"Protection against {type} must be between 0 and 1.");
            }
        }

        Item = item;
        Layer = layer;
        Coverage = covered;
        Insulation = insulation;
        Protection = absorbed;
        Encumbrance = encumbrance;
    }

    public ItemId Item { get; }

    public ClothingLayer Layer { get; }

    public IReadOnlySet<BodyPart> Coverage { get; }

    /// <summary>Insulation this item adds on every body part it covers.</summary>
    public ThermalResistance Insulation { get; }

    /// <summary>Fraction (0 to 1) of each damage type absorbed on every covered body part. Missing types absorb nothing.</summary>
    public IReadOnlyDictionary<DamageType, double> Protection { get; }

    /// <summary>Fraction (0 to 1) of the capability of every covered body part that this item takes away.</summary>
    public double Encumbrance { get; }
}

public interface IWearableCatalog
{
    bool TryGet(ItemId item, out WearableDefinition definition);
}

public sealed class WearableCatalog : IWearableCatalog
{
    private readonly Dictionary<ItemId, WearableDefinition> _definitions = [];

    public WearableCatalog(IEnumerable<WearableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Item, definition))
            {
                throw new ArgumentException($"Duplicate wearable '{definition.Item}'.", nameof(definitions));
            }
        }
    }

    public bool TryGet(ItemId item, out WearableDefinition definition) => _definitions.TryGetValue(item, out definition!);
}
