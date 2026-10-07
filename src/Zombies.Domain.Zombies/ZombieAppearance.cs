using Zombies.Domain.Items;

namespace Zombies.Domain.Zombies;

/// <summary>
/// The melee weapon a zombie spawned holding. A one-handed weapon is in the right hand, or in the left when the right arm is a
/// Missing part from spawn; a two-handed one is in the right hand and needs both arms.
/// </summary>
public sealed record ZombieHeldWeapon(ItemId Item, int HandsNeeded, BodyPart Arm)
{
    /// <summary>Whether the zombie still holds it once it lacks <paramref name="missing"/>: losing the holding arm, or either arm of a two-handed weapon, drops it.</summary>
    public bool IsHeldWith(IReadOnlyCollection<BodyPart> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        return !missing.Contains(Arm) && (HandsNeeded < 2 || !missing.Contains(BodyPart.LeftArm));
    }
}

/// <summary>
/// How one zombie looks and what it wears, a pure function of its <see cref="ZombieSpec"/> and type (see <see cref="ZombieGenerator"/>).
/// Every client derives the same value from the same spec, so none of it is sent over the network.
/// </summary>
public sealed class ZombieAppearance : IEquatable<ZombieAppearance>
{
    public ZombieAppearance(
        int heightPermille,
        int buildPermille,
        uint skinColor,
        IReadOnlyList<ItemId> clothing,
        ItemId? headwear,
        ItemId? backpack,
        IReadOnlyList<BodyPart> missingParts,
        ZombieHeldWeapon? heldWeapon = null)
    {
        HeightPermille = heightPermille;
        BuildPermille = buildPermille;
        SkinColor = skinColor;
        Clothing = clothing;
        Headwear = headwear;
        Backpack = backpack;
        MissingParts = missingParts;
        HeldWeapon = heldWeapon;
    }

    /// <summary>Body height in thousandths of the skeleton's own.</summary>
    public int HeightPermille { get; }

    /// <summary>Body width and depth in thousandths of the skeleton's own.</summary>
    public int BuildPermille { get; }

    /// <summary>Skin color as 0xRRGGBB.</summary>
    public uint SkinColor { get; }

    public IReadOnlyList<ItemId> Clothing { get; }

    public ItemId? Headwear { get; }

    public ItemId? Backpack { get; }

    /// <summary>The Body parts the zombie lacks from spawn, in Body part order.</summary>
    public IReadOnlyList<BodyPart> MissingParts { get; }

    /// <summary>The melee weapon the zombie spawned holding, or null for none.</summary>
    public ZombieHeldWeapon? HeldWeapon { get; }

    public float HeightScale => HeightPermille / 1000f;

    public float BuildScale => BuildPermille / 1000f;

    /// <summary>Everything the zombie wears, which is what it drops when it dies.</summary>
    public IReadOnlyList<ItemId> WornItems
    {
        get
        {
            var items = new List<ItemId>(Clothing);
            if (Headwear is { } hat)
            {
                items.Add(hat);
            }

            if (Backpack is { } pack)
            {
                items.Add(pack);
            }

            return items;
        }
    }

    public bool Equals(ZombieAppearance? other) =>
        other is not null
        && HeightPermille == other.HeightPermille
        && BuildPermille == other.BuildPermille
        && SkinColor == other.SkinColor
        && Headwear == other.Headwear
        && Backpack == other.Backpack
        && HeldWeapon == other.HeldWeapon
        && Clothing.SequenceEqual(other.Clothing)
        && MissingParts.SequenceEqual(other.MissingParts);

    public override bool Equals(object? obj) => Equals(obj as ZombieAppearance);

    public override int GetHashCode() => HashCode.Combine(HeightPermille, BuildPermille, SkinColor, Headwear, Backpack, Clothing.Count, MissingParts.Count);
}
