using System.Numerics;

namespace Zombies.Domain.World;

/// <summary>Finds the Container a player is looking at, so the game can show its interaction prompt and open it.</summary>
public static class ContainerTargeting
{
    /// <summary>How far, in blocks, a player can reach to open a Container.</summary>
    public const float DefaultReach = 3f;

    /// <summary>
    /// The nearest Container whose block the ray from <paramref name="eye"/> along <paramref name="direction"/> enters within
    /// <paramref name="reach"/> blocks, or null. A Container occupies the block at its integer coordinates.
    /// </summary>
    public static WorldContainer? Find(IEnumerable<WorldContainer> containers, Vector3 eye, Vector3 direction, float reach = DefaultReach)
    {
        ArgumentNullException.ThrowIfNull(containers);
        if (direction.LengthSquared() < 1e-12f)
        {
            return null;
        }

        var unit = Vector3.Normalize(direction);
        WorldContainer? best = null;
        var bestDistance = float.MaxValue;
        foreach (var container in containers)
        {
            if (Hit(container, eye, unit) is { } distance && distance <= reach && distance < bestDistance)
            {
                best = container;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Distance along the ray at which it enters the block (slab test), or null when it misses or the block is behind the eye.</summary>
    private static float? Hit(WorldContainer container, Vector3 eye, Vector3 unit)
    {
        var near = 0f;
        var far = float.MaxValue;
        var min = new Vector3(container.X, container.Y, container.Z);
        for (var axis = 0; axis < 3; axis++)
        {
            var origin = Component(eye, axis);
            var step = Component(unit, axis);
            var low = Component(min, axis);
            if (MathF.Abs(step) < 1e-9f)
            {
                if (origin < low || origin > low + 1f)
                {
                    return null;
                }

                continue;
            }

            var t1 = (low - origin) / step;
            var t2 = (low + 1f - origin) / step;
            near = MathF.Max(near, MathF.Min(t1, t2));
            far = MathF.Min(far, MathF.Max(t1, t2));
            if (near > far)
            {
                return null;
            }
        }

        return near;
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
