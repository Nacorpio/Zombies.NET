using System.Numerics;
using Zombies.Domain.Items;

namespace Zombies.Domain.Zombies;

/// <summary>How an attack reaches a zombie. A Weakpoint can be harder to hit with one than with the other.</summary>
public enum AttackKind
{
    Melee,
    Ranged,
}

/// <summary>What landing enough damage on a Weakpoint can do besides the extra damage.</summary>
public enum WeakpointEffect
{
    Stagger,
}

/// <summary>
/// A sub-region of a Body part where a hit does more. The region is a cube centered at <see cref="Center"/> in the part's box,
/// which spans 0 to 1 on each axis, with sides of <see cref="Size"/> of the box. A difficulty shrinks the cube, so a harder
/// Weakpoint is a smaller target. Above <see cref="EffectThreshold"/> damage, <see cref="Effect"/> has a chance, in basis points,
/// to happen.
/// </summary>
public sealed record Weakpoint(
    string Name,
    BodyPart Part,
    Vector3 Center,
    float Size,
    double CriticalMultiplier,
    double RangedDifficulty,
    double MeleeDifficulty,
    WeakpointEffect? Effect,
    double EffectThreshold,
    int EffectChanceBasis);

/// <summary>A hit landed on a Weakpoint. Clients use it to show where, and it carries what the Server decided.</summary>
public sealed record WeakpointHit(uint Entity, string Weakpoint, BodyPart Part, double Damage, WeakpointEffect? Effect) : IDomainEvent;

/// <summary>The Weakpoints a Zombie type declares, which a Zombie type refers to by Content ID so mods can share and patch them.</summary>
public sealed class WeakpointSet
{
    public const int MaxWeakpoints = 16;

    public WeakpointSet(string id, IEnumerable<Weakpoint> weakpoints)
    {
        ArgumentNullException.ThrowIfNull(weakpoints);
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        var list = weakpoints.ToList();
        if (list.Count == 0 || list.Count > MaxWeakpoints)
        {
            throw new ArgumentException($"A Weakpoint set holds 1 to {MaxWeakpoints} Weakpoints.", nameof(weakpoints));
        }

        foreach (var weakpoint in list)
        {
            Check(weakpoint);
        }

        if (list.Select(w => w.Name).Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            throw new ArgumentException($"Weakpoint set '{id}' lists a Weakpoint twice.", nameof(weakpoints));
        }

        Id = id;
        Weakpoints = list;
    }

    public string Id { get; }

    public IReadOnlyList<Weakpoint> Weakpoints { get; }

    /// <summary>
    /// The Weakpoint of <paramref name="part"/> a ray passes through first, if any. The ray is in the part's box space, where the
    /// box spans 0 to 1 on each axis. A Weakpoint listed earlier wins a tie.
    /// </summary>
    public Weakpoint? Find(BodyPart part, Vector3 origin, Vector3 direction, AttackKind attack)
    {
        Weakpoint? best = null;
        var bestDistance = float.MaxValue;
        foreach (var weakpoint in Weakpoints)
        {
            if (weakpoint.Part != part)
            {
                continue;
            }

            var difficulty = attack == AttackKind.Melee ? weakpoint.MeleeDifficulty : weakpoint.RangedDifficulty;
            var half = (float)(weakpoint.Size * (1 - difficulty) / 2);
            if (TryRegion(origin, direction, weakpoint.Center - new Vector3(half), weakpoint.Center + new Vector3(half), out var distance) && distance < bestDistance)
            {
                best = weakpoint;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static void Check(Weakpoint weakpoint)
    {
        if (string.IsNullOrWhiteSpace(weakpoint.Name))
        {
            throw new ArgumentException("A Weakpoint needs a name.");
        }

        if (!Enum.IsDefined(weakpoint.Part))
        {
            throw new ArgumentException($"Weakpoint '{weakpoint.Name}' is on no Body part.");
        }

        if (!Unit(weakpoint.Center.X) || !Unit(weakpoint.Center.Y) || !Unit(weakpoint.Center.Z))
        {
            throw new ArgumentException($"The center of Weakpoint '{weakpoint.Name}' must be inside its Body part, 0 to 1 on each axis.");
        }

        if (!float.IsFinite(weakpoint.Size) || weakpoint.Size is <= 0 or > 1)
        {
            throw new ArgumentException($"The size of Weakpoint '{weakpoint.Name}' must be above 0 and at most 1.");
        }

        if (!double.IsFinite(weakpoint.CriticalMultiplier) || weakpoint.CriticalMultiplier < 1)
        {
            throw new ArgumentException($"The critical multiplier of Weakpoint '{weakpoint.Name}' must be at least 1.");
        }

        if (!double.IsFinite(weakpoint.RangedDifficulty) || weakpoint.RangedDifficulty is < 0 or >= 1
            || !double.IsFinite(weakpoint.MeleeDifficulty) || weakpoint.MeleeDifficulty is < 0 or >= 1)
        {
            throw new ArgumentException($"A difficulty of Weakpoint '{weakpoint.Name}' must be from 0 up to but not including 1.");
        }

        if (weakpoint.Effect is { } effect && !Enum.IsDefined(effect))
        {
            throw new ArgumentException($"Weakpoint '{weakpoint.Name}' has an unknown effect.");
        }

        if (!double.IsFinite(weakpoint.EffectThreshold) || weakpoint.EffectThreshold < 0)
        {
            throw new ArgumentException($"The effect threshold of Weakpoint '{weakpoint.Name}' must be finite and not negative.");
        }

        if (weakpoint.EffectChanceBasis is < 0 or > ZombieTypeDefinition.BasisPoints)
        {
            throw new ArgumentException($"The effect chance of Weakpoint '{weakpoint.Name}' must be between 0 and 1.");
        }

        if (weakpoint.Effect is null && weakpoint.EffectChanceBasis > 0)
        {
            throw new ArgumentException($"Weakpoint '{weakpoint.Name}' has an effect chance but no effect.");
        }
    }

    private static bool Unit(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

    /// <summary>The distance at which a ray first enters a box (zero when it starts inside), by the slab method.</summary>
    private static bool TryRegion(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float distance)
    {
        var near = 0f;
        var far = float.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = origin[axis];
            var d = direction[axis];
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < min[axis] || o > max[axis])
                {
                    distance = 0;
                    return false;
                }

                continue;
            }

            var t1 = (min[axis] - o) / d;
            var t2 = (max[axis] - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far)
            {
                distance = 0;
                return false;
            }
        }

        distance = near;
        return true;
    }
}
