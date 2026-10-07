using System.Numerics;

namespace Zombies.Engine.Physics.Gore;

/// <summary>A blood decal on a surface: where, which way the surface faces, how wide, and which of the atlas's splats to draw.</summary>
public readonly record struct BloodDecal(Vector3 Position, Vector3 Normal, float Radius, int Variant);

/// <summary>
/// A fixed-size ring of decals. When it is full the oldest decal is evicted to make room, so a long fight costs a constant amount to
/// draw and nothing is allocated after construction.
/// </summary>
public sealed class DecalPool
{
    private readonly BloodDecal[] _decals;
    private int _next;

    public DecalPool(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _decals = new BloodDecal[capacity];
    }

    public int Capacity => _decals.Length;

    public int Count { get; private set; }

    /// <summary>How many decals have been pushed out to make room.</summary>
    public int Evicted { get; private set; }

    public void Add(BloodDecal decal)
    {
        if (Count == _decals.Length)
        {
            Evicted++;
        }
        else
        {
            Count++;
        }

        _decals[_next] = decal;
        _next = (_next + 1) % _decals.Length;
    }

    /// <summary>The decal that is <paramref name="age"/> places from the oldest, 0 being the oldest.</summary>
    public BloodDecal this[int age]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(age);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(age, Count);
            var oldest = Count == _decals.Length ? _next : 0;
            return _decals[(oldest + age) % _decals.Length];
        }
    }

    public void Clear()
    {
        Count = 0;
        _next = 0;
    }
}
