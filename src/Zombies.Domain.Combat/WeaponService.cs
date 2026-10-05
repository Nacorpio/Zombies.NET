using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

/// <summary>Domain commands for weapons. A weapon's Condition, loaded rounds, and fitted Attachments live in its Item state.</summary>
public sealed class WeaponService(WeaponCatalog catalog)
{
    public const string ConditionValue = "condition";
    public const string RoundsValue = "rounds";
    public const int MaxCondition = 100;

    public static readonly StatName Damage = new("damage");
    public static readonly StatName RateOfFire = new("rate_of_fire");
    public static readonly StatName Reach = new("reach");
    public static readonly StatName Handling = new("handling");
    public static readonly StatName Noise = new("noise");

    private static readonly ModifierSource ConditionSource = new("condition");

    /// <summary>A worn weapon at zero Condition would still deal this fraction of its damage if it could be used.</summary>
    private const double WornDamageFloor = 0.5;

    /// <summary>The Condition of a weapon with this state. A weapon with no recorded Condition is in full Condition.</summary>
    public static int ConditionOf(ItemState? state) =>
        state is not null && state.Values.TryGetValue(ConditionValue, out var condition) ? Math.Clamp(condition, 0, MaxCondition) : MaxCondition;

    public static int RoundsOf(ItemState? state) =>
        state is not null && state.Values.TryGetValue(RoundsValue, out var rounds) ? Math.Max(rounds, 0) : 0;

    /// <summary>
    /// Base stats, Attachment modifiers, and Condition combined through the shared Modifier pipeline, along with the
    /// <paramref name="wielder"/>'s own Modifiers, such as the grip Limb score on <see cref="Handling"/>.
    /// </summary>
    public bool TryGetEffectiveStats(ItemId weapon, ItemState? state, out WeaponStats stats, ModifierSet? wielder = null)
    {
        stats = null!;
        if (!catalog.TryGetWeapon(weapon, out var definition))
        {
            return false;
        }

        var modifiers = new ModifierSet();
        foreach (var modifier in wielder?.All ?? [])
        {
            modifiers.Add(modifier);
        }

        foreach (var attached in state?.Attached.Keys ?? [])
        {
            if (catalog.TryGetAttachment(attached, out var attachment))
            {
                foreach (var effect in attachment.Effects)
                {
                    modifiers.Add(new Modifier(effect.Stat, effect.Operation, effect.Value, attachment.ModifierSource));
                }
            }
        }

        var wear = 1 - (double)ConditionOf(state) / MaxCondition;
        modifiers.Add(new Modifier(Damage, ModifierOperation.Multiply, 1 - (wear * (1 - WornDamageFloor)), ConditionSource));

        var handling = definition.Handling ?? (catalog.TryGetCategory(definition.Category, out var category) ? category.Handling : 0);
        stats = new WeaponStats(
            Math.Max(0, modifiers.EffectiveValue(Damage, definition.Damage)),
            Math.Max(0, modifiers.EffectiveValue(RateOfFire, definition.RateOfFire)),
            Math.Max(0, modifiers.EffectiveValue(Reach, definition.Reach)),
            Math.Max(0, modifiers.EffectiveValue(Handling, handling)),
            Math.Max(0, modifiers.EffectiveValue(Noise, definition.Noise)),
            definition.HandsNeeded);
        return true;
    }

    /// <summary>Fits an Attachment to the Mount it belongs on, if the weapon offers that Mount and it is free.</summary>
    public WeaponResult Attach(ItemId weapon, ItemState? state, ItemId attachment)
    {
        if (!catalog.TryGetWeapon(weapon, out var definition))
        {
            return WeaponResult.Failure(WeaponError.UnknownWeapon);
        }

        if (!catalog.TryGetAttachment(attachment, out var fitting))
        {
            return WeaponResult.Failure(WeaponError.UnknownAttachment);
        }

        if (!MountsOf(definition).Contains(fitting.Mount))
        {
            return WeaponResult.Failure(WeaponError.MountNotOffered);
        }

        state ??= ItemState.Create();
        if (state.Attached.Keys.Any(a => catalog.TryGetAttachment(a, out var other) && other.Mount == fitting.Mount))
        {
            return WeaponResult.Failure(WeaponError.MountOccupied);
        }

        return WeaponResult.Success(state.WithAttached(attachment, 1), new AttachmentFitted(weapon, attachment, fitting.Mount));
    }

    public WeaponResult Detach(ItemId weapon, ItemState? state, ItemId attachment)
    {
        if (!catalog.TryGetWeapon(weapon, out _))
        {
            return WeaponResult.Failure(WeaponError.UnknownWeapon);
        }

        if (state is null || !state.Attached.ContainsKey(attachment) || !catalog.TryGetAttachment(attachment, out var fitted))
        {
            return WeaponResult.Failure(WeaponError.NotFitted);
        }

        return WeaponResult.Success(state.WithoutAttached(attachment), new AttachmentRemoved(weapon, attachment, fitted.Mount));
    }

    /// <summary>Uses the weapon once: wears it, spends a round if it uses ammo, and returns the damage for Combat.</summary>
    public WeaponResult Use(ItemId weapon, ItemState? state)
    {
        if (!TryGetEffectiveStats(weapon, state, out var stats))
        {
            return WeaponResult.Failure(WeaponError.UnknownWeapon);
        }

        var definition = catalog.TryGetWeapon(weapon, out var found) ? found : throw new InvalidOperationException();
        var condition = ConditionOf(state);
        if (condition <= 0)
        {
            return WeaponResult.Failure(WeaponError.Broken);
        }

        var rounds = RoundsOf(state);
        if (definition.AmmoItem is not null && rounds < 1)
        {
            return WeaponResult.Failure(WeaponError.OutOfAmmo);
        }

        var worn = Math.Max(0, condition - definition.WearPerUse);
        var remaining = definition.AmmoItem is null ? rounds : rounds - 1;
        var next = (state ?? ItemState.Create()).With(ConditionValue, worn);
        if (definition.AmmoItem is not null)
        {
            next = next.With(RoundsValue, remaining);
        }

        var used = new WeaponUsed(weapon, stats.Damage, definition.DamageType, stats.Noise, worn, remaining);
        return worn == 0 && condition > 0
            ? WeaponResult.Success(next, used, new WeaponBroke(weapon))
            : WeaponResult.Success(next, used);
    }

    private IReadOnlyCollection<string> MountsOf(WeaponDefinition weapon) =>
        [.. (catalog.TryGetCategory(weapon.Category, out var category) ? category.Mounts : []).Concat(weapon.ExtraMounts)];
}
