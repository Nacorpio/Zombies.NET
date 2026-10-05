using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

/// <summary>A named kind of weapon, such as pistol or blunt melee, that sets shared handling and the Mounts its weapons offer.</summary>
public sealed record WeaponCategory
{
    public WeaponCategory(string id, double handling, IEnumerable<string> mounts)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        WeaponValidation.ContentId(id, nameof(id));
        WeaponValidation.NonNegative(handling, nameof(handling));
        Id = id;
        Handling = handling;
        Mounts = [.. mounts.Select(m => WeaponValidation.MountName(m)).Distinct()];
    }

    public string Id { get; }

    /// <summary>Default handling of weapons in this category, unless a weapon sets its own.</summary>
    public double Handling { get; }

    /// <summary>Mounts every weapon in this category offers.</summary>
    public IReadOnlyList<string> Mounts { get; }
}

/// <summary>What makes an Item a Weapon: its category, base stats, ammo, wear, and any Mounts beyond its category's.</summary>
public sealed record WeaponDefinition
{
    public WeaponDefinition(
        ItemId item,
        string category,
        double damage,
        DamageType damageType,
        double rateOfFire,
        double reach,
        double noise,
        int handsNeeded = 1,
        double? handling = null,
        ItemId? ammoItem = null,
        int wearPerUse = 1,
        IEnumerable<string>? extraMounts = null)
    {
        WeaponValidation.ContentId(category, nameof(category));
        WeaponValidation.NonNegative(damage, nameof(damage));
        WeaponValidation.NonNegative(rateOfFire, nameof(rateOfFire));
        WeaponValidation.NonNegative(reach, nameof(reach));
        WeaponValidation.NonNegative(noise, nameof(noise));
        if (handling is { } h)
        {
            WeaponValidation.NonNegative(h, nameof(handling));
        }

        if (!Enum.IsDefined(damageType))
        {
            throw new ArgumentOutOfRangeException(nameof(damageType), damageType, "Unknown damage type.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(handsNeeded, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(handsNeeded, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(wearPerUse, 0);
        Item = item;
        Category = category;
        Damage = damage;
        DamageType = damageType;
        RateOfFire = rateOfFire;
        Reach = reach;
        Noise = noise;
        HandsNeeded = handsNeeded;
        Handling = handling;
        AmmoItem = ammoItem;
        WearPerUse = wearPerUse;
        ExtraMounts = [.. (extraMounts ?? []).Select(m => WeaponValidation.MountName(m)).Distinct()];
    }

    public ItemId Item { get; }

    public string Category { get; }

    public double Damage { get; }

    public DamageType DamageType { get; }

    /// <summary>Uses per second.</summary>
    public double RateOfFire { get; }

    /// <summary>Reach for melee or range for ranged weapons, in meters.</summary>
    public double Reach { get; }

    public double Noise { get; }

    public int HandsNeeded { get; }

    /// <summary>Handling that overrides the category's, or null to use it.</summary>
    public double? Handling { get; }

    /// <summary>The Item whose rounds this weapon spends, or null for a weapon that needs no ammo.</summary>
    public ItemId? AmmoItem { get; }

    /// <summary>Condition lost per use.</summary>
    public int WearPerUse { get; }

    public IReadOnlyList<string> ExtraMounts { get; }
}

/// <summary>A change an Attachment makes to one Stat, through the shared Modifier pipeline.</summary>
public sealed record AttachmentEffect(StatName Stat, ModifierOperation Operation, double Value);

/// <summary>An Item that fits one Mount of a weapon and changes its stats.</summary>
public sealed record AttachmentDefinition
{
    public AttachmentDefinition(ItemId item, string mount, IEnumerable<AttachmentEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        Item = item;
        Mount = WeaponValidation.MountName(mount);
        Effects = [.. effects];
        foreach (var effect in Effects)
        {
            _ = new Modifier(effect.Stat, effect.Operation, effect.Value, ModifierSource);
        }
    }

    public ItemId Item { get; }

    public string Mount { get; }

    public IReadOnlyList<AttachmentEffect> Effects { get; }

    /// <summary>The source of every Modifier this Attachment grants.</summary>
    public ModifierSource ModifierSource => new(Item.Value);
}

internal static class WeaponValidation
{
    public static void ContentId(string value, string name)
    {
        if (!ItemId.TryParse(value, out _))
        {
            throw new ArgumentException($"'{value}' is not a valid Content ID.", name);
        }
    }

    public static string MountName(string value)
    {
        if (!StatName.TryParse(value, out _))
        {
            throw new ArgumentException($"'{value}' is not a valid Mount name (expected lowercase letters, digits and '_').", nameof(value));
        }

        return value;
    }

    public static void NonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a finite number that is not negative.");
        }
    }
}
