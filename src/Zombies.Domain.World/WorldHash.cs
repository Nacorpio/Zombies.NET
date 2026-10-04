namespace Zombies.Domain.World;

/// <summary>
/// Integer-only hashing of a world seed and coordinates. Every random-looking world decision goes through this,
/// so the same seed gives the same world on every platform. Floating point is never used (ADR 0005).
/// </summary>
public static class WorldHash
{
    /// <summary>Mixes a seed, two coordinates, and a salt that keeps different decisions independent.</summary>
    public static ulong Mix(ulong seed, int a, int b, int salt)
    {
        var h = seed
            ^ ((ulong)(uint)a * 0x9E3779B97F4A7C15UL)
            ^ ((ulong)(uint)b * 0xC2B2AE3D27D4EB4FUL)
            ^ ((ulong)(uint)salt * 0x165667B19E3779F9UL);
        return Finalize(h);
    }

    /// <summary>The SplitMix64 finalizer: spreads every input bit over every output bit.</summary>
    public static ulong Finalize(ulong value)
    {
        var z = value + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
