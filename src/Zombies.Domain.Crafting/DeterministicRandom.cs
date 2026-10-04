namespace Zombies.Domain.Crafting;

/// <summary>
/// SplitMix64 with integer-only arithmetic, so the same seed yields the same numbers on every platform and runtime.
/// <see cref="System.Random"/> is not used because its seeded sequence is not guaranteed to stay stable.
/// </summary>
public sealed class DeterministicRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform value in <c>[0, bound)</c>, without modulo bias.</summary>
    public ulong NextBelow(ulong bound)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bound);
        var threshold = (0UL - bound) % bound;
        while (true)
        {
            var value = NextUInt64();
            if (value >= threshold)
            {
                return value % bound;
            }
        }
    }

    /// <summary>Uniform integer in <c>[min, max]</c>, both ends included.</summary>
    public int NextInt(int min, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);
        var span = (ulong)((long)max - min) + 1;
        return (int)(min + (long)NextBelow(span));
    }

    /// <summary>Mixes two values into one seed, such as a world seed and a container id.</summary>
    public static ulong Combine(ulong a, ulong b)
    {
        var mixer = new DeterministicRandom(a);
        var first = mixer.NextUInt64();
        return new DeterministicRandom(first ^ b).NextUInt64();
    }
}
