namespace Zombies.Domain.World;

public readonly record struct RegionCoord(int X, int Z)
{
    public override string ToString() => $"region({X},{Z})";
}

/// <summary>Where a settlement is to be built in a region, and the seed that makes its layout repeatable.</summary>
public sealed record SettlementSite(RegionCoord Region, int X, int Z, ulong Seed);

/// <summary>
/// Divides the world into large regions and decides, from the world seed alone, which regions hold a settlement
/// site and how dangerous each region is.
/// </summary>
public sealed class RegionGrid
{
    public const int Size = 512;

    /// <summary>Sites stay this far from a region's edge so settlements never straddle two regions.</summary>
    public const int Margin = 96;

    public const int MinDanger = 1;
    public const int MaxDanger = 10;

    private const int SaltSiteExists = 101;
    private const int SaltSiteX = 102;
    private const int SaltSiteZ = 103;
    private const int SaltSiteSeed = 104;
    private const int SaltDanger = 105;

    private readonly ulong _seed;
    private readonly int _siteChancePercent;

    public RegionGrid(ulong seed, int siteChancePercent = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(siteChancePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(siteChancePercent, 100);
        _seed = seed;
        _siteChancePercent = siteChancePercent;
    }

    public static RegionCoord RegionOf(int worldX, int worldZ) => new(FloorDiv(worldX, Size), FloorDiv(worldZ, Size));

    /// <summary>The settlement site in a region, or null if the region has none. The spawn region never does.</summary>
    public SettlementSite? SiteIn(RegionCoord region)
    {
        if (region == default)
        {
            return null;
        }

        if (WorldHash.Mix(_seed, region.X, region.Z, SaltSiteExists) % 100 >= (ulong)_siteChancePercent)
        {
            return null;
        }

        var span = (ulong)(Size - (2 * Margin));
        var x = (region.X * Size) + Margin + (int)(WorldHash.Mix(_seed, region.X, region.Z, SaltSiteX) % span);
        var z = (region.Z * Size) + Margin + (int)(WorldHash.Mix(_seed, region.X, region.Z, SaltSiteZ) % span);
        return new SettlementSite(region, x, z, WorldHash.Mix(_seed, region.X, region.Z, SaltSiteSeed));
    }

    /// <summary>Danger level from 1 to 10: it grows with distance from the spawn region, with a little noise. The spawn region is always 1.</summary>
    public int DangerOf(RegionCoord region)
    {
        if (region == default)
        {
            return MinDanger;
        }

        var distance = Math.Max(Math.Abs(region.X), Math.Abs(region.Z));
        var jitter = (int)(WorldHash.Mix(_seed, region.X, region.Z, SaltDanger) % 3) - 1;
        return Math.Clamp(MinDanger + (distance / 2) + jitter, MinDanger, MaxDanger);
    }

    private static int FloorDiv(int value, int divisor) => (value / divisor) - ((value % divisor < 0) ? 1 : 0);
}
