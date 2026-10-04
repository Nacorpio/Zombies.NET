using Zombies.Domain.World;

namespace Zombies.Engine.Voxel;

/// <summary>
/// Value noise in integer arithmetic only. Results are 0 to 65535 and identical on every platform and runtime,
/// which floating-point noise cannot promise (ADR 0005).
/// </summary>
public static class IntNoise
{
    public const int Max = 65535;

    private const int One = 65536;

    /// <summary>Smooth noise on a grid whose cells are <c>1 &lt;&lt; cellShift</c> blocks wide.</summary>
    public static int Value2D(ulong seed, int x, int z, int cellShift)
    {
        var mask = (1 << cellShift) - 1;
        var cx = x >> cellShift;
        var cz = z >> cellShift;
        var tx = Smooth((x & mask) << (16 - cellShift));
        var tz = Smooth((z & mask) << (16 - cellShift));

        var n00 = Lattice(seed, cx, cz);
        var n10 = Lattice(seed, cx + 1, cz);
        var n01 = Lattice(seed, cx, cz + 1);
        var n11 = Lattice(seed, cx + 1, cz + 1);

        var top = Lerp(n00, n10, tx);
        var bottom = Lerp(n01, n11, tx);
        return Lerp(top, bottom, tz);
    }

    /// <summary>Sums octaves of <see cref="Value2D"/>, each half the size and half the weight of the one before.</summary>
    public static int Fractal2D(ulong seed, int x, int z, int firstCellShift, int octaves)
    {
        long total = 0;
        var weightSum = 0;
        for (var o = 0; o < octaves; o++)
        {
            var weight = 1 << (octaves - 1 - o);
            var shift = Math.Max(1, firstCellShift - o);
            total += (long)weight * Value2D(seed + (ulong)o, x, z, shift);
            weightSum += weight;
        }

        return (int)(total / weightSum);
    }

    private static int Lattice(ulong seed, int x, int z) => (int)(WorldHash.Mix(seed, x, z, 0) >> 48);

    /// <summary>Smoothstep on a 0 to 65535 fraction, in 64-bit integer math.</summary>
    private static int Smooth(int t) => (int)(((long)t * t * ((3L * One) - (2L * t))) >> 32);

    private static int Lerp(int a, int b, int t) => a + (int)(((long)(b - a) * t) >> 16);
}
