using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Items;

namespace Zombies.Domain.Zombies;

/// <summary>
/// Derives a zombie's appearance from its <see cref="ZombieSpec"/>. It uses integer arithmetic and the project's own
/// <see cref="DeterministicRandom"/>, so the same spec gives the same zombie on every client and every platform. The draws happen
/// in a fixed order, so adding a field later means adding a draw at the end and never reordering.
/// </summary>
public static class ZombieGenerator
{
    /// <param name="type">The type of the spec.</param>
    /// <param name="spec">What to derive the zombie from.</param>
    /// <param name="weapons">Where the held weapons of the type are looked up, to know how many hands each needs. Required when the type lists any.</param>
    /// <exception cref="ArgumentException">The spec names another type, its Level is outside the type's range, or the type holds a weapon <paramref name="weapons"/> does not know.</exception>
    public static ZombieAppearance Generate(ZombieTypeDefinition type, ZombieSpec spec, WeaponCatalog? weapons = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        Check(type, spec);

        var random = new DeterministicRandom(spec.Seed);
        var height = random.NextInt(type.HeightPermille.Min, type.HeightPermille.Max);
        var build = random.NextInt(type.BuildPermille.Min, type.BuildPermille.Max);
        var skin = type.SkinTones[(int)random.NextBelow((ulong)type.SkinTones.Count)];

        var outfit = type.Outfit;
        var clothing = new List<ItemId>();
        var remaining = outfit.Clothing.ToList();
        var pieces = random.NextInt(outfit.MinClothing, outfit.MaxClothing);
        for (var i = 0; i < pieces && remaining.Count > 0; i++)
        {
            var index = PickIndex(random, remaining);
            if (remaining[index].Item is { } piece)
            {
                clothing.Add(piece);
            }

            remaining.RemoveAt(index);
        }

        var headwear = outfit.Headwear.Count == 0 ? null : outfit.Headwear[PickIndex(random, outfit.Headwear)].Item;
        var backpack = outfit.Backpacks.Count == 0 ? null : outfit.Backpacks[PickIndex(random, outfit.Backpacks)].Item;

        var missing = new List<BodyPart>();
        foreach (var chance in type.MissingParts)
        {
            if ((int)random.NextBelow(ZombieTypeDefinition.BasisPoints) < chance.Basis)
            {
                missing.Add(chance.Part);
            }
        }

        return new ZombieAppearance(height, build, skin, clothing, headwear, backpack, missing, PickHeldWeapon(type, weapons, random, missing));
    }

    /// <summary>
    /// The last draw, so zombies of a type with no held weapons come out exactly as they did before. Only weapons the arms left at
    /// spawn can hold are in the draw: none with no arms, and only one-handed ones with one.
    /// </summary>
    private static ZombieHeldWeapon? PickHeldWeapon(ZombieTypeDefinition type, WeaponCatalog? weapons, DeterministicRandom random, IReadOnlyList<BodyPart> missing)
    {
        if (type.HeldWeapons.Count == 0)
        {
            return null;
        }

        var arms = 2 - missing.Count(p => p is BodyPart.LeftArm or BodyPart.RightArm);
        var candidates = new List<(ItemId? Item, int Weight, int Hands)>();
        foreach (var entry in type.HeldWeapons)
        {
            if (entry.Item is not { } item)
            {
                candidates.Add((null, entry.Weight, 0));
                continue;
            }

            if (weapons is null || !weapons.TryGetWeapon(item, out var weapon))
            {
                throw new ArgumentException($"Zombie type '{type.Id}' holds '{item}', which is not a known Weapon.", nameof(weapons));
            }

            if (weapon.HandsNeeded <= arms)
            {
                candidates.Add((item, entry.Weight, weapon.HandsNeeded));
            }
        }

        var total = candidates.Sum(c => (long)c.Weight);
        if (total == 0)
        {
            return null;
        }

        var roll = (long)random.NextBelow((ulong)total);
        foreach (var candidate in candidates)
        {
            roll -= candidate.Weight;
            if (roll < 0)
            {
                return candidate.Item is { } held
                    ? new ZombieHeldWeapon(held, candidate.Hands, missing.Contains(BodyPart.RightArm) ? BodyPart.LeftArm : BodyPart.RightArm)
                    : null;
            }
        }

        return null;
    }

    private static void Check(ZombieTypeDefinition type, ZombieSpec spec)
    {
        if (!string.Equals(spec.Type, type.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The spec is for '{spec.Type}', not '{type.Id}'.", nameof(spec));
        }

        if (spec.Level < 1 || spec.Level > type.TopLevel)
        {
            throw new ArgumentException($"Level {spec.Level} is outside 1 to {type.TopLevel} for '{type.Id}'.", nameof(spec));
        }
    }

    private static int PickIndex(DeterministicRandom random, IReadOnlyList<WeightedWearable> entries)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            total += entry.Weight;
        }

        var roll = (long)random.NextBelow((ulong)total);
        for (var i = 0; i < entries.Count; i++)
        {
            roll -= entries[i].Weight;
            if (roll < 0)
            {
                return i;
            }
        }

        return entries.Count - 1;
    }
}
