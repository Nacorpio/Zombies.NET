namespace Zombies.Domain.World;

/// <summary>One Structure placed in a Settlement. <see cref="X"/> and <see cref="Z"/> are the world coordinates of its west, north corner.</summary>
public sealed record PlacedStructure(int Index, Structure Structure, int X, int Z)
{
    public int MaxX => X + Structure.Width - 1;

    public int MaxZ => Z + Structure.Depth - 1;

    /// <summary>World column near the middle of the floor, where the ground level is read from.</summary>
    public int CenterX => X + (Structure.Width / 2);

    public int CenterZ => Z + (Structure.Depth / 2);

    /// <summary>True when the footprint, or the yard around it that is levelled with the floor, touches the box.</summary>
    public bool YardTouches(int minX, int minZ, int maxX, int maxZ) =>
        X - SettlementContent.YardMargin <= maxX && MaxX + SettlementContent.YardMargin >= minX
        && Z - SettlementContent.YardMargin <= maxZ && MaxZ + SettlementContent.YardMargin >= minZ;
}

/// <summary>An Area of a placed Structure and the Area type it was given.</summary>
public sealed record PlacedArea(int StructureIndex, string Name, string AreaType);

/// <summary>A Container in the world. The Area type of its room, not its Structure, decides what it holds.</summary>
public sealed record WorldContainer(int X, int Y, int Z, string ContainerKind, string AreaName, string AreaType, int Danger, ulong Seed);

/// <summary>Where a zombie of some type spawns. Its height is the ground at that column.</summary>
public sealed record ZombieSpawn(string ZombieType, int X, int Z);

/// <summary>
/// A Settlement as planned for one Region: its type, the Structures with where each stands, the Area type of every Area,
/// where Containers are, and where zombies spawn. It is a pure function of the world seed and the Region.
/// </summary>
public sealed class SettlementPlan
{
    internal sealed record ContainerSite(int StructureIndex, StructureContainer Container, string AreaType, ulong Seed);

    private readonly IReadOnlyList<ContainerSite> _containers;

    internal SettlementPlan(
        SettlementSite site,
        int danger,
        SettlementType type,
        IReadOnlyList<PlacedStructure> structures,
        IReadOnlyList<PlacedArea> areas,
        IReadOnlyList<ContainerSite> containers,
        IReadOnlyList<ZombieSpawn> zombieSpawns)
    {
        Site = site;
        Danger = danger;
        Type = type;
        Structures = structures;
        Areas = areas;
        _containers = containers;
        ZombieSpawns = zombieSpawns;
    }

    public SettlementSite Site { get; }

    /// <summary>The Danger level of the Region.</summary>
    public int Danger { get; }

    public SettlementType Type { get; }

    public IReadOnlyList<PlacedStructure> Structures { get; }

    public IReadOnlyList<PlacedArea> Areas { get; }

    public IReadOnlyList<ZombieSpawn> ZombieSpawns { get; }

    /// <summary>The Area type of an Area of a placed Structure.</summary>
    public string AreaTypeOf(int structureIndex, string areaName) =>
        Areas.First(a => a.StructureIndex == structureIndex && a.Name == areaName).AreaType;

    /// <summary>
    /// Every Container in world coordinates. <paramref name="floorY"/> gives the height of each Structure's floor, which only
    /// the terrain knows.
    /// </summary>
    public IReadOnlyList<WorldContainer> ContainersAt(Func<PlacedStructure, int> floorY)
    {
        ArgumentNullException.ThrowIfNull(floorY);
        var result = new List<WorldContainer>(_containers.Count);
        foreach (var site in _containers)
        {
            var placed = Structures[site.StructureIndex];
            var pos = site.Container.Position;
            result.Add(new WorldContainer(
                placed.X + pos.X,
                floorY(placed) + pos.Y,
                placed.Z + pos.Z,
                site.Container.ContainerKind,
                site.Container.Area,
                site.AreaType,
                Danger,
                site.Seed));
        }

        return result;
    }
}

/// <summary>
/// Plans the Settlement of a Region from the world seed alone, with integer arithmetic only, so every platform plans the
/// same Settlement (ADR 0005). Structures stand on a grid centred on the Region's site, in an order the seed shuffles.
/// </summary>
public static class SettlementPlanner
{
    private const int SaltType = 111;
    private const int SaltCount = 112;
    private const int SaltCell = 113;
    private const int SaltArea = 114;
    private const int SaltContainer = 115;
    private const int SaltZombieCount = 116;
    private const int SaltZombieCell = 117;
    private const int SaltZombieOffset = 118;

    /// <summary>The Settlement in a Region, or null when the Region has no site or no Settlement type fits its Danger level.</summary>
    public static SettlementPlan? Plan(SettlementContent content, RegionGrid grid, RegionCoord region)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.SiteIn(region) is not { } site)
        {
            return null;
        }

        var danger = grid.DangerOf(region);
        return Choose(content, danger, site.Seed) is { } type ? Build(content, type, site, danger) : null;
    }

    /// <summary>Picks among the types whose danger range holds <paramref name="danger"/>, each in proportion to its rarity weight.</summary>
    public static SettlementType? Choose(SettlementContent content, int danger, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(content);
        var eligible = content.Types.Where(t => t.AllowsDanger(danger)).ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        var total = eligible.Sum(t => (long)t.Rarity);
        var pick = (long)(WorldHash.Mix(seed, danger, 0, SaltType) % (ulong)total);
        foreach (var type in eligible)
        {
            if (pick < type.Rarity)
            {
                return type;
            }

            pick -= type.Rarity;
        }

        return eligible[^1];
    }

    private static SettlementPlan Build(SettlementContent content, SettlementType type, SettlementSite site, int danger)
    {
        var instances = new List<Structure>();
        for (var i = 0; i < type.Structures.Count; i++)
        {
            var entry = type.Structures[i];
            content.TryGetStructure(entry.Structure, out var structure);
            var count = Roll(site.Seed, i, 0, SaltCount, entry.MinCount, entry.MaxCount);
            for (var k = 0; k < count; k++)
            {
                instances.Add(structure);
            }
        }

        if (instances.Count == 0)
        {
            content.TryGetStructure(type.Structures[0].Structure, out var first);
            instances.Add(first);
        }

        var pitch = SettlementContent.PitchFor(instances.Max(s => Math.Max(s.Width, s.Depth)));
        var (columns, rows) = SettlementContent.GridSize(instances.Count);
        var minX = site.X - ((columns * pitch) / 2);
        var minZ = site.Z - ((rows * pitch) / 2);
        var cells = Enumerable.Range(0, columns * rows)
            .OrderBy(c => WorldHash.Mix(site.Seed, c, 0, SaltCell))
            .ThenBy(c => c)
            .Take(instances.Count)
            .ToList();

        var structures = new List<PlacedStructure>(instances.Count);
        var areas = new List<PlacedArea>();
        var containers = new List<SettlementPlan.ContainerSite>();
        for (var i = 0; i < instances.Count; i++)
        {
            var structure = instances[i];
            var cellX = minX + ((cells[i] % columns) * pitch);
            var cellZ = minZ + ((cells[i] / columns) * pitch);
            structures.Add(new PlacedStructure(i, structure, cellX + ((pitch - structure.Width) / 2), cellZ + ((pitch - structure.Depth) / 2)));

            for (var a = 0; a < structure.Areas.Count; a++)
            {
                var area = structure.Areas[a];
                areas.Add(new PlacedArea(i, area.Name, area.AreaType ?? PickAreaType(type, site.Seed, i, a)));
            }

            for (var c = 0; c < structure.Containers.Count; c++)
            {
                var container = structure.Containers[c];
                var areaType = areas.First(x => x.StructureIndex == i && x.Name == container.Area).AreaType;
                containers.Add(new SettlementPlan.ContainerSite(i, container, areaType, WorldHash.Mix(site.Seed, i, c, SaltContainer)));
            }
        }

        var zombies = new List<ZombieSpawn>();
        for (var r = 0; r < type.ZombieSpawns.Count; r++)
        {
            var rule = type.ZombieSpawns[r];
            var count = Roll(site.Seed, r, 0, SaltZombieCount, rule.MinCount, rule.MaxCount);
            for (var k = 0; k < count; k++)
            {
                var n = (r * SettlementType.MaxZombies) + k;
                var cell = (int)(WorldHash.Mix(site.Seed, n, 0, SaltZombieCell) % (ulong)(columns * rows));
                var offset = (int)(WorldHash.Mix(site.Seed, n, 0, SaltZombieOffset) % (ulong)pitch);

                // The west edge of a grid cell is outside every footprint and yard, so a zombie never spawns in a wall.
                zombies.Add(new ZombieSpawn(rule.ZombieType, minX + ((cell % columns) * pitch), minZ + ((cell / columns) * pitch) + offset));
            }
        }

        return new SettlementPlan(site, danger, type, structures, areas, containers, zombies);
    }

    private static string PickAreaType(SettlementType type, ulong seed, int structureIndex, int areaIndex)
    {
        var total = type.AreaTypes.Sum(a => (long)a.Weight);
        var pick = (long)(WorldHash.Mix(seed, structureIndex, areaIndex, SaltArea) % (ulong)total);
        foreach (var entry in type.AreaTypes)
        {
            if (pick < entry.Weight)
            {
                return entry.AreaType;
            }

            pick -= entry.Weight;
        }

        return type.AreaTypes[^1].AreaType;
    }

    private static int Roll(ulong seed, int a, int b, int salt, int min, int max) =>
        min + (int)(WorldHash.Mix(seed, a, b, salt) % (ulong)(max - min + 1));
}
