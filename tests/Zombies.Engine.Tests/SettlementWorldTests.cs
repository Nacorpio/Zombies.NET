using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>Villages in the generated world: stamped at region sites, enterable, with Containers whose loot comes from the room they are in.</summary>
public sealed class SettlementWorldTests
{
    private sealed class Repo
    {
        public Repo()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
            {
                directory = directory.Parent;
            }

            var root = directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
            var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(root, "mods")));
            Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));
            Registry = loaded.Registry;
            Biomes = new BiomeCatalog(Registry.OfKind("biome").Select(d => BiomeJson.Parse(d.Json)));
            Settlements = SettlementContentLoader.Load(Registry);
            Items = new ItemCatalog(Registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
            Areas = new AreaTypeCatalog(Registry.OfKind("area_type").Select(d => AreaTypeJson.Parse(d.Json)));
            AreaLoot = new AreaLootService(Areas, new LootService(new LootTableCatalog(Registry.OfKind("loot").Select(d => LootTableJson.Parse(d.Json)))));
        }

        public DefinitionRegistry Registry { get; }

        public BiomeCatalog Biomes { get; }

        public SettlementContent Settlements { get; }

        public ItemCatalog Items { get; }

        public AreaTypeCatalog Areas { get; }

        public AreaLootService AreaLoot { get; }
    }

    private static readonly Lazy<Repo> Shared = new(() => new Repo());

    private static Repo Content => Shared.Value;

    private static WorldGenerator Generator(ulong seed = 12345) => new(seed, Content.Biomes, Content.Settlements);

    private static (RegionCoord Region, SettlementPlan Plan) FirstSettlement(WorldGenerator generator)
    {
        for (var ring = 1; ring <= 8; ring++)
        {
            for (var x = -ring; x <= ring; x++)
            {
                for (var z = -ring; z <= ring; z++)
                {
                    var region = new RegionCoord(x, z);
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) == ring && generator.SettlementIn(region) is { } plan)
                    {
                        return (region, plan);
                    }
                }
            }
        }

        throw new InvalidOperationException("No settlement within eight regions.");
    }

    private sealed class World(WorldGenerator generator)
    {
        private readonly Dictionary<ChunkCoord, Chunk> _chunks = [];

        public ushort At(int x, int y, int z)
        {
            var coord = new ChunkCoord(x >> 4, z >> 4);
            if (!_chunks.TryGetValue(coord, out var chunk))
            {
                chunk = generator.Generate(coord);
                _chunks[coord] = chunk;
            }

            return chunk.Get(x & 15, y, z & 15);
        }
    }

    private static IEnumerable<ChunkCoord> ChunksOf(SettlementPlan plan)
    {
        var minX = plan.Structures.Min(s => s.X - SettlementContent.YardMargin) >> 4;
        var maxX = plan.Structures.Max(s => s.MaxX + SettlementContent.YardMargin) >> 4;
        var minZ = plan.Structures.Min(s => s.Z - SettlementContent.YardMargin) >> 4;
        var maxZ = plan.Structures.Max(s => s.MaxZ + SettlementContent.YardMargin) >> 4;
        for (var cz = minZ; cz <= maxZ; cz++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                yield return new ChunkCoord(cx, cz);
            }
        }
    }

    // Generation ------------------------------------------------------------------------------------------------

    [Fact]
    public void Base_ShipsAVillageMadeOfStructuresAndASettlementType()
    {
        var structures = Content.Settlements.Structures.Select(s => s.Id).Order().ToList();

        Assert.Equal(["base:structure/garage", "base:structure/house_family", "base:structure/house_small"], structures);
        var village = Assert.Single(Content.Settlements.Types);
        Assert.Equal("base:settlement_type/village", village.Id);
        Assert.False(village.Inhabited);
        Assert.Equal(["base:structure/house_small", "base:structure/house_family", "base:structure/garage"], village.Structures.Select(s => s.Structure));
        Assert.NotEmpty(village.ZombieSpawns);
    }

    [Fact]
    public void Generation_WithoutSettlementContent_PlansNothing()
    {
        var plain = new WorldGenerator(12345, Content.Biomes);

        Assert.Null(plain.SettlementIn(new RegionCoord(3, 3)));
        Assert.Empty(plain.ContainersIn(new RegionCoord(3, 3)));
    }

    [Fact]
    public void Generation_OutsideSettlements_IsUnchanged()
    {
        var plain = new WorldGenerator(12345, Content.Biomes);
        var withVillages = Generator();

        foreach (var coord in new[] { new ChunkCoord(0, 0), new ChunkCoord(1, 0), new ChunkCoord(-3, 5), new ChunkCoord(7, -9) })
        {
            Assert.Equal(plain.Generate(coord).Hash(), withVillages.Generate(coord).Hash());
        }
    }

    [Fact]
    public void Settlement_IsStampedAtItsRegionSite()
    {
        var generator = Generator();
        var (region, plan) = FirstSettlement(generator);
        var plain = new WorldGenerator(12345, Content.Biomes);

        Assert.Equal(region, RegionGrid.RegionOf(plan.Site.X, plan.Site.Z));
        Assert.Equal(new RegionGrid(12345).SiteIn(region), plan.Site);
        var centre = new ChunkCoord(plan.Structures[0].CenterX >> 4, plan.Structures[0].CenterZ >> 4);
        Assert.NotEqual(plain.Generate(centre).Hash(), generator.Generate(centre).Hash());
    }

    [Fact]
    public void Settlement_ChunksAreIdenticalForEveryGeneratorAndInAnyOrder()
    {
        var first = Generator();
        var second = Generator();
        var (_, plan) = FirstSettlement(first);
        var coords = ChunksOf(plan).ToList();

        var forward = coords.ToDictionary(c => c, c => first.Generate(c).Hash());
        var backward = new Dictionary<ChunkCoord, ulong>();
        foreach (var coord in Enumerable.Reverse(coords))
        {
            backward[coord] = second.Generate(coord).Hash();
        }

        Assert.Equal(forward, backward);
        Assert.True(coords.Count > 1, "the village should span more than one chunk");
        Assert.NotEqual(Generator(1).Generate(coords[0]).Hash(), Generator(2).Generate(coords[0]).Hash());
    }

    [Fact]
    public void Settlement_EveryStructureIsStampedBlockForBlock_AcrossChunkBorders()
    {
        var generator = Generator();
        var (region, plan) = FirstSettlement(generator);
        var world = new World(generator);
        var containers = generator.ContainersIn(region).Select(c => (c.X, c.Y, c.Z)).ToHashSet();

        foreach (var placed in plan.Structures)
        {
            var floor = generator.FloorY(placed);
            var structure = placed.Structure;
            for (var y = 0; y < structure.Height; y++)
            {
                for (var z = 0; z < structure.Depth; z++)
                {
                    for (var x = 0; x < structure.Width; x++)
                    {
                        var at = (placed.X + x, floor + y, placed.Z + z);
                        Assert.True(Blocks.TryFromName(structure.BlockAt(x, y, z), out var expected));
                        if (containers.Contains(at))
                        {
                            expected = Blocks.Crate;
                        }

                        Assert.True(expected == world.At(at.Item1, at.Item2, at.Item3), $"{structure.Id} cell ({x},{y},{z}) should be {Blocks.NameOf(expected)} but is {Blocks.NameOf(world.At(at.Item1, at.Item2, at.Item3))}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Settlement_ContainersAreCratesAndEveryChunkKnowsItsOwn()
    {
        var generator = Generator();
        var (region, plan) = FirstSettlement(generator);
        var world = new World(generator);

        var all = generator.ContainersIn(region);
        var byChunk = ChunksOf(plan).SelectMany(c => generator.ContainersIn(c)).ToList();

        Assert.NotEmpty(all);
        Assert.Equal(plan.Structures.Sum(s => s.Structure.Containers.Count), all.Count);
        Assert.Equal(all.OrderBy(c => (c.X, c.Y, c.Z)), byChunk.OrderBy(c => (c.X, c.Y, c.Z)));
        Assert.All(all, c => Assert.Equal(Blocks.Crate, world.At(c.X, c.Y, c.Z)));
        Assert.All(all, c => Assert.Equal(Blocks.Planks, world.At(c.X, c.Y - 1, c.Z)));
    }

    [Fact]
    public void Settlement_TheGroundAroundEveryStructureIsLevelAndClear()
    {
        var generator = Generator();
        var (_, plan) = FirstSettlement(generator);
        var world = new World(generator);
        var yard = SettlementContent.YardMargin;

        foreach (var placed in plan.Structures)
        {
            var floor = generator.FloorY(placed);
            for (var z = placed.Z - yard; z <= placed.MaxZ + yard; z++)
            {
                for (var x = placed.X - yard; x <= placed.MaxX + yard; x++)
                {
                    if (x >= placed.X && x <= placed.MaxX && z >= placed.Z && z <= placed.MaxZ)
                    {
                        continue;
                    }

                    Assert.Equal(Blocks.Grass, world.At(x, floor, z));
                    Assert.True(Blocks.IsOpaque(world.At(x, floor - 1, z)));
                    for (var y = floor + 1; y <= floor + 12; y++)
                    {
                        Assert.Equal(Blocks.Air, world.At(x, y, z));
                    }
                }
            }
        }
    }

    [Fact]
    public void Houses_CanBeWalkedIntoAndEveryContainerCanBeReached()
    {
        var generator = Generator();
        var (_, plan) = FirstSettlement(generator);
        var world = new World(generator);

        foreach (var placed in plan.Structures)
        {
            var floor = generator.FloorY(placed);
            var y = floor + 1;
            var margin = SettlementContent.YardMargin;

            bool Standable(int x, int z) =>
                !Blocks.IsOpaque(world.At(x, y, z)) && !Blocks.IsOpaque(world.At(x, y + 1, z)) && Blocks.IsOpaque(world.At(x, y - 1, z));

            var reached = new HashSet<(int X, int Z)>();
            var pending = new Stack<(int X, int Z)>();
            pending.Push((placed.X - margin, placed.Z - margin));
            while (pending.TryPop(out var cell))
            {
                if (cell.X < placed.X - margin || cell.X > placed.MaxX + margin || cell.Z < placed.Z - margin || cell.Z > placed.MaxZ + margin
                    || !Standable(cell.X, cell.Z) || !reached.Add(cell))
                {
                    continue;
                }

                pending.Push((cell.X + 1, cell.Z));
                pending.Push((cell.X - 1, cell.Z));
                pending.Push((cell.X, cell.Z + 1));
                pending.Push((cell.X, cell.Z - 1));
            }

            foreach (var area in placed.Structure.Areas)
            {
                var roomCells = from x in Enumerable.Range(placed.X + area.Min.X, area.Max.X - area.Min.X + 1)
                                from z in Enumerable.Range(placed.Z + area.Min.Z, area.Max.Z - area.Min.Z + 1)
                                select (x, z);
                Assert.Contains(roomCells, c => reached.Contains(c));
            }

            foreach (var container in placed.Structure.Containers)
            {
                var (cx, cz) = (placed.X + container.Position.X, placed.Z + container.Position.Z);
                Assert.True(
                    reached.Contains((cx + 1, cz)) || reached.Contains((cx - 1, cz)) || reached.Contains((cx, cz + 1)) || reached.Contains((cx, cz - 1)),
                    $"{placed.Structure.Id}: nobody can stand next to the {container.ContainerKind} at ({container.Position.X},{container.Position.Z})");
            }
        }
    }

    [Fact]
    public void Villages_AppearInRegionsAcrossTheWorld_AndOnlyWhereTheGridHasASite()
    {
        var generator = Generator();
        var grid = new RegionGrid(12345);
        var found = 0;

        for (var x = -5; x <= 5; x++)
        {
            for (var z = -5; z <= 5; z++)
            {
                var region = new RegionCoord(x, z);
                var plan = generator.SettlementIn(region);
                Assert.Equal(grid.SiteIn(region) is not null, plan is not null);
                if (plan is not null)
                {
                    found++;
                    Assert.Same(plan, generator.SettlementIn(region));
                }
            }
        }

        Assert.InRange(found, 40, 90);
    }

    [Fact]
    public void ANewKindOfSettlement_IsOnlyData()
    {
        var farm = SettlementTypeJson.Parse("""
            {
              "id": "mymod:settlement_type/farmstead",
              "inhabited": true,
              "rarity": 1,
              "danger": { "min": 1, "max": 10 },
              "structures": [ { "structure": "base:structure/house_small", "count": { "min": 2, "max": 2 } } ],
              "areaTypes": [ { "areaType": "base:area_type/bathroom" } ],
              "zombieSpawns": []
            }
            """);
        var content = new SettlementContent(Content.Settlements.Structures, [farm]);
        var generator = new WorldGenerator(12345, Content.Biomes, content);

        var (region, plan) = FirstSettlement(generator);

        Assert.Equal("mymod:settlement_type/farmstead", plan.Type.Id);
        Assert.Equal(2, plan.Structures.Count);
        Assert.Empty(plan.ZombieSpawns);
        Assert.All(generator.ContainersIn(region), c => Assert.Equal("base:area_type/bathroom", c.AreaType));
    }

    [Fact]
    public void ZombieDensityOption_ScalesTheZombiesOfTheBaseSettlements()
    {
        var normal = new WorldGenerator(12345, Content.Biomes, Content.Settlements);
        var none = new WorldGenerator(12345, Content.Biomes, Content.Settlements, zombieDensityPercent: 0);
        var triple = new WorldGenerator(12345, Content.Biomes, Content.Settlements, zombieDensityPercent: 300);

        var (region, plan) = FirstSettlement(normal);

        Assert.NotEmpty(plan.ZombieSpawns);
        Assert.Empty(none.SettlementIn(region)!.ZombieSpawns);
        Assert.True(triple.SettlementIn(region)!.ZombieSpawns.Count > plan.ZombieSpawns.Count);
        Assert.Equal(plan.Structures.Count, none.SettlementIn(region)!.Structures.Count);
    }

    // Content ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryAreaTypeAStructureCanEndUpWith_HasARuleForEveryContainerInIt()
    {
        foreach (var structure in Content.Settlements.Structures)
        {
            foreach (var area in structure.Areas)
            {
                var candidates = area.AreaType is { } fixedType
                    ? new List<string> { fixedType }
                    : Content.Settlements.Types.Where(t => t.Structures.Any(s => s.Structure == structure.Id)).SelectMany(t => t.AreaTypes.Select(a => a.AreaType)).Distinct().ToList();

                Assert.NotEmpty(candidates);
                foreach (var candidate in candidates)
                {
                    Assert.True(Content.Areas.TryGet(candidate, out var areaType), $"{structure.Id} {area.Name} names {candidate}, which is not an Area type");
                    foreach (var container in structure.Containers.Where(c => c.Area == area.Name))
                    {
                        Assert.True(areaType.TryGetRule(container.ContainerKind, out _), $"{candidate} has no rule for a {container.ContainerKind} in {structure.Id}");
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryAreaTypeASettlementTypeNames_Exists()
    {
        foreach (var type in Content.Settlements.Types)
        {
            Assert.All(type.AreaTypes, a => Assert.True(Content.Areas.TryGet(a.AreaType, out _), $"{type.Id} names {a.AreaType}"));
        }
    }

    // Loot ------------------------------------------------------------------------------------------------------

    private static WorldContainer Cabinet(string areaType, int x = 10, ulong seed = 99, string kind = "cabinet") => new(x, 60, 20, kind, "main", areaType, 2, seed);

    private static WorldContainerOpener Opener(IContainerRepository? repository = null) =>
        new(Content.AreaLoot, Content.Items, repository ?? new InMemoryContainerRepository());

    [Fact]
    public void Opening_FillsTheContainerFromTheAreaTypeOfItsRoom_NotFromItsBuilding()
    {
        var kitchen = Opener().Open(Cabinet("base:area_type/kitchen"));
        var bathroom = Opener().Open(Cabinet("base:area_type/bathroom"));

        // The same kind of cabinet in the same position holds what its room says.
        Assert.True(kitchen.IsSuccess);
        Assert.True(bathroom.IsSuccess);
        Assert.Equal("base:loot/kitchen", kitchen.Table);
        Assert.Equal("base:loot/bathroom", bathroom.Table);
        Assert.NotEmpty(kitchen.Container!.Stacks);
        Assert.NotEmpty(bathroom.Container!.Stacks);
        Assert.All(kitchen.Container.Stacks, s => Assert.True(s.Item.Value is "base:item/canned_beans" or "base:item/water_bottle", s.Item.Value));
        Assert.All(bathroom.Container.Stacks, s => Assert.True(s.Item.Value is "base:item/bandage" or "base:item/water_bottle", s.Item.Value));
    }

    [Fact]
    public void Opening_TheSameContainerTwice_ReturnsWhatWasLeftInIt()
    {
        var repository = new InMemoryContainerRepository();
        var opener = Opener(repository);
        var container = Cabinet("base:area_type/kitchen");

        var first = opener.Open(container);
        var item = first.Container!.Stacks[0].Item;
        new InventoryService(Content.Items, repository).RemoveItems(first.Container.Id, item, first.Container.Stacks[0].Count);
        var second = opener.Open(container);

        Assert.True(first.Generated);
        Assert.False(second.Generated);
        Assert.True(second.IsSuccess);
        Assert.Same(first.Container, second.Container);
        Assert.DoesNotContain(second.Container!.Stacks, s => s.Item == item);
    }

    [Fact]
    public void Opening_IsTheSameInEveryWorld_ForTheSameContainer()
    {
        var a = Opener().Open(Cabinet("base:area_type/bedroom", kind: "wardrobe"));
        var b = Opener().Open(Cabinet("base:area_type/bedroom", kind: "wardrobe"));
        var other = Opener().Open(Cabinet("base:area_type/bedroom", kind: "wardrobe", seed: 100));

        Assert.True(a.IsSuccess);
        Assert.Equal(a.Table, b.Table);
        Assert.Equal(a.Container!.Stacks.Select(s => (s.Item, s.Count)), b.Container!.Stacks.Select(s => (s.Item, s.Count)));
        Assert.Equal(a.Container.Id, b.Container.Id);
        Assert.True(other.IsSuccess);
    }

    [Fact]
    public void Opening_AContainerKindTheRoomHasNoRuleFor_FailsAndStoresNothing()
    {
        var repository = new InMemoryContainerRepository();

        var result = Opener(repository).Open(Cabinet("base:area_type/bathroom", kind: "fridge"));
        var unknown = Opener(repository).Open(Cabinet("base:area_type/nowhere"));

        Assert.False(result.IsSuccess);
        Assert.Equal(AreaLootError.NoRuleForContainerKind, result.Error);
        Assert.Equal(AreaLootError.UnknownAreaType, unknown.Error);
        Assert.Empty(repository.Ids());
    }

    [Fact]
    public void ContainerIds_AreStableAndDifferByPosition()
    {
        var here = WorldContainerOpener.IdOf(Cabinet("base:area_type/kitchen", x: 10));
        var again = WorldContainerOpener.IdOf(Cabinet("base:area_type/bedroom", x: 10, seed: 1));
        var elsewhere = WorldContainerOpener.IdOf(Cabinet("base:area_type/kitchen", x: 11));

        Assert.Equal(here, again);
        Assert.NotEqual(here, elsewhere);
        Assert.True(here.Value > 0);
    }

    [Fact]
    public void EveryContainerInTheBaseWorld_CanBeOpenedAndHoldsLoot()
    {
        var generator = Generator();
        var opener = Opener();
        var opened = 0;

        for (var x = -3; x <= 3; x++)
        {
            for (var z = -3; z <= 3; z++)
            {
                foreach (var container in generator.ContainersIn(new RegionCoord(x, z)))
                {
                    var result = opener.Open(container);

                    Assert.True(result.IsSuccess, $"{container.ContainerKind} in {container.AreaType}: {result.Error}");
                    Assert.NotNull(result.Table);
                    Assert.NotEmpty(result.Container!.Stacks);
                    opened++;
                }
            }
        }

        Assert.True(opened > 20, $"only {opened} containers in 49 regions");
    }

    [Fact]
    public void ContainersInTheSameRoomKind_GetLootFromTheTablesOfThatKind()
    {
        var generator = Generator();
        var (region, _) = FirstSettlement(generator);
        var opener = Opener();

        foreach (var container in generator.ContainersIn(region))
        {
            var result = opener.Open(container);

            Assert.True(Content.Areas.TryGet(container.AreaType, out var areaType));
            Assert.True(areaType.TryGetRule(container.ContainerKind, out var rule));
            Assert.Contains(result.Table, rule.Tables.Select(t => t.Table));
        }
    }
}
