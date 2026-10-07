using Zombies.Domain.Items;

namespace Zombies.Domain.Zombies;

/// <summary>A Trait a Zombie type has, by Content ID, with the numbers the Trait reads, such as <c>speedMultiplier</c>.</summary>
public sealed record TraitReference(string Trait, IReadOnlyDictionary<string, double> Values);

/// <summary>One thing a zombie may wear, and its chance against the others. A null item is a choice to wear nothing.</summary>
public sealed record WeightedWearable(ItemId? Item, int Weight);

/// <summary>What clothing, headwear, and backpacks a Zombie type can spawn wearing.</summary>
public sealed record OutfitTable(
    IReadOnlyList<WeightedWearable> Clothing,
    int MinClothing,
    int MaxClothing,
    IReadOnlyList<WeightedWearable> Headwear,
    IReadOnlyList<WeightedWearable> Backpacks)
{
    public static OutfitTable Empty { get; } = new([], 0, 0, [], []);
}

/// <summary>One melee weapon a zombie may spawn holding, and its chance against the others. A null item is a choice to hold nothing.</summary>
public sealed record WeightedHeldWeapon(ItemId? Item, int Weight);

/// <summary>The chance that a zombie of some type spawns without a Body part.</summary>
public sealed record MissingPartChance(BodyPart Part, int Basis);

/// <summary>The Status effect a bite of a zombie may cause, such as infection, and the chance in basis points that it does.</summary>
public sealed record ZombieBite(string Effect, int ChanceBasis);

/// <summary>
/// The Zombie type a zombie of some type becomes, and how many world days after the world began it does. A chain adds its delays up,
/// so with a walker that upgrades after 10 days and a runner after 20, a walker is a runner from day 10 and the next type from day 30.
/// </summary>
public sealed record ZombieUpgrade(string ZombieType, int AfterDays);

/// <summary>
/// A kind of zombie: base stats, Traits, senses, how its appearance may vary, and what it can wear. Everything about an
/// individual zombie is derived from this and its <see cref="ZombieSpec"/>.
/// </summary>
public sealed class ZombieTypeDefinition
{
    public const int MaxTraits = 16;
    public const int MaxLevel = 100;
    public const int MaxWearables = 32;
    public const int MaxHeldWeapons = 32;
    public const int BasisPoints = 10_000;

    public ZombieTypeDefinition(
        string id,
        double partHealth,
        double damage,
        double speed,
        double sightMeters,
        double hearingMeters,
        int maxLevel,
        double perLevelBonus,
        IEnumerable<TraitReference> traits,
        (int Min, int Max) heightPermille,
        (int Min, int Max) buildPermille,
        IEnumerable<uint> skinTones,
        OutfitTable outfit,
        IEnumerable<MissingPartChance> missingParts,
        string? weakpointSet = null,
        ZombieUpgrade? upgrade = null,
        IEnumerable<WeightedHeldWeapon>? heldWeapons = null,
        ZombieBite? bite = null)
    {
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(skinTones);
        ArgumentNullException.ThrowIfNull(outfit);
        ArgumentNullException.ThrowIfNull(missingParts);
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        RequirePositive(partHealth, nameof(partHealth));
        RequirePositive(damage, nameof(damage));
        RequirePositive(speed, nameof(speed));
        RequirePositive(sightMeters, nameof(sightMeters));
        RequirePositive(hearingMeters, nameof(hearingMeters));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLevel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLevel, MaxLevel);
        if (!double.IsFinite(perLevelBonus) || perLevelBonus < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(perLevelBonus), "The bonus per level must be finite and not negative.");
        }

        var traitList = traits.ToList();
        if (traitList.Count > MaxTraits)
        {
            throw new ArgumentException($"A Zombie type can have at most {MaxTraits} Traits.", nameof(traits));
        }

        foreach (var trait in traitList)
        {
            if (!ItemId.TryParse(trait.Trait, out _))
            {
                throw new ArgumentException($"'{trait.Trait}' is not a valid Content ID.", nameof(traits));
            }

            if (trait.Values.Values.Any(v => !double.IsFinite(v)))
            {
                throw new ArgumentException($"A value of Trait '{trait.Trait}' is not a finite number.", nameof(traits));
            }
        }

        if (traitList.Select(t => t.Trait).Distinct(StringComparer.Ordinal).Count() != traitList.Count)
        {
            throw new ArgumentException($"Zombie type '{id}' lists a Trait twice.", nameof(traits));
        }

        CheckRange(heightPermille, nameof(heightPermille));
        CheckRange(buildPermille, nameof(buildPermille));

        var skinList = skinTones.ToList();
        if (skinList.Count == 0)
        {
            throw new ArgumentException($"Zombie type '{id}' needs at least one skin tone.", nameof(skinTones));
        }

        CheckOutfit(outfit);

        var missingList = missingParts.OrderBy(m => m.Part).ToList();
        foreach (var chance in missingList)
        {
            if (!Enum.IsDefined(chance.Part) || chance.Part == BodyPart.Torso)
            {
                throw new ArgumentException($"{chance.Part} cannot be a Missing part at spawn.", nameof(missingParts));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(chance.Basis);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(chance.Basis, BasisPoints);
        }

        if (missingList.Select(m => m.Part).Distinct().Count() != missingList.Count)
        {
            throw new ArgumentException($"Zombie type '{id}' lists a Body part twice in its missing parts.", nameof(missingParts));
        }

        if (weakpointSet is not null && !ItemId.TryParse(weakpointSet, out _))
        {
            throw new ArgumentException($"'{weakpointSet}' is not a valid Content ID.", nameof(weakpointSet));
        }

        if (upgrade is not null)
        {
            if (!ItemId.TryParse(upgrade.ZombieType, out _))
            {
                throw new ArgumentException($"'{upgrade.ZombieType}' is not a valid Content ID.", nameof(upgrade));
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(upgrade.AfterDays, 1);
        }

        var heldList = (heldWeapons ?? []).ToList();
        if (heldList.Count > MaxHeldWeapons)
        {
            throw new ArgumentException($"A Zombie type can list at most {MaxHeldWeapons} held weapons.", nameof(heldWeapons));
        }

        foreach (var entry in heldList)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(entry.Weight, 1);
        }

        var heldItems = heldList.Where(h => h.Item is not null).Select(h => h.Item).ToList();
        if (heldItems.Distinct().Count() != heldItems.Count || heldList.Count(h => h.Item is null) > 1)
        {
            throw new ArgumentException($"Zombie type '{id}' lists a held weapon twice.", nameof(heldWeapons));
        }

        if (bite is not null)
        {
            if (!ItemId.TryParse(bite.Effect, out _))
            {
                throw new ArgumentException($"'{bite.Effect}' is not a valid Content ID.", nameof(bite));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(bite.ChanceBasis);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bite.ChanceBasis, BasisPoints);
        }

        Id = id;
        PartHealth = partHealth;
        Damage = damage;
        Speed = speed;
        SightMeters = sightMeters;
        HearingMeters = hearingMeters;
        TopLevel = maxLevel;
        PerLevelBonus = perLevelBonus;
        Traits = traitList;
        HeightPermille = heightPermille;
        BuildPermille = buildPermille;
        SkinTones = skinList;
        Outfit = outfit;
        MissingParts = missingList;
        WeakpointSet = weakpointSet;
        Upgrade = upgrade;
        HeldWeapons = heldList;
        Bite = bite;
    }

    public string Id { get; }

    /// <summary>Health of each Body part at Level 1.</summary>
    public double PartHealth { get; }

    /// <summary>Damage of one attack at Level 1.</summary>
    public double Damage { get; }

    /// <summary>Walking speed in meters per second, before Traits change it.</summary>
    public double Speed { get; }

    public double SightMeters { get; }

    public double HearingMeters { get; }

    /// <summary>The highest Level a zombie of this type can have.</summary>
    public int TopLevel { get; }

    /// <summary>The fraction of health and damage added for each Level above 1.</summary>
    public double PerLevelBonus { get; }

    public IReadOnlyList<TraitReference> Traits { get; }

    /// <summary>The range of body height, in thousandths of the skeleton's height.</summary>
    public (int Min, int Max) HeightPermille { get; }

    /// <summary>The range of body width and depth, in thousandths of the skeleton's.</summary>
    public (int Min, int Max) BuildPermille { get; }

    /// <summary>Skin colors as 0xRRGGBB.</summary>
    public IReadOnlyList<uint> SkinTones { get; }

    public OutfitTable Outfit { get; }

    /// <summary>The chance, in basis points, that each listed Body part is missing from spawn, in Body part order.</summary>
    public IReadOnlyList<MissingPartChance> MissingParts { get; }

    /// <summary>Content ID of the Weakpoint set of this type, or null when no part of it is weaker than another.</summary>
    public string? WeakpointSet { get; }

    /// <summary>What this type upgrades into as the world ages, or null when it stays what it is.</summary>
    public ZombieUpgrade? Upgrade { get; }

    /// <summary>
    /// The melee weapons a zombie of this type may spawn holding, by weight. An entry with no item is a chance of holding nothing.
    /// Empty means the type never holds a weapon.
    /// </summary>
    public IReadOnlyList<WeightedHeldWeapon> HeldWeapons { get; }

    /// <summary>The Status effect a bite of this type may cause, or null when its bites only wound.</summary>
    public ZombieBite? Bite { get; }

    public double PartHealthAt(int level) => PartHealth * LevelFactor(level);

    public double DamageAt(int level) => Damage * LevelFactor(level);

    /// <summary>How much stronger than Level 1 a zombie of this Level is: 1 at Level 1, plus the bonus for each Level above.</summary>
    public double LevelFactor(int level) => 1 + (PerLevelBonus * (Math.Clamp(level, 1, TopLevel) - 1));

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Must be a finite number above zero.");
        }
    }

    private static void CheckRange((int Min, int Max) range, string name)
    {
        if (range.Min < 100 || range.Max > 3000 || range.Min > range.Max)
        {
            throw new ArgumentOutOfRangeException(name, "A proportion range must satisfy 100 <= min <= max <= 3000 (thousandths).");
        }
    }

    private static void CheckOutfit(OutfitTable outfit)
    {
        foreach (var list in new[] { outfit.Clothing, outfit.Headwear, outfit.Backpacks })
        {
            if (list.Count > MaxWearables)
            {
                throw new ArgumentException($"An outfit list can hold at most {MaxWearables} entries.", nameof(outfit));
            }

            foreach (var entry in list)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(entry.Weight, 1);
            }
        }

        if (outfit.MinClothing < 0 || outfit.MinClothing > outfit.MaxClothing || outfit.MaxClothing > MaxWearables)
        {
            throw new ArgumentException("The clothing count must satisfy 0 <= min <= max.", nameof(outfit));
        }

        if (outfit.MaxClothing > 0 && outfit.Clothing.Count == 0)
        {
            throw new ArgumentException("A zombie that wears clothing needs clothing to choose from.", nameof(outfit));
        }
    }
}
