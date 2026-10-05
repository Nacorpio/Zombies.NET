using System.Numerics;
using Zombies.Domain.World;

namespace Zombies.Domain.Tests;

public sealed class SettlementTests
{
    private const string Palette = """[ { "symbol": "#", "block": "planks" }, { "symbol": ".", "block": "air" } ]""";

    // A 5 wide, 3 deep, 4 high hut: one room along its back wall and a doorway in the middle of the front wall.
    private const string Layers = """
        [
          ["#####", "#####", "#####"],
          ["#####", "#...#", "##.##"],
          ["#####", "#...#", "##.##"],
          ["#####", "#####", "#####"]
        ]
        """;

    private const string Areas = """[ { "name": "main", "min": { "x": 1, "y": 1, "z": 1 }, "max": { "x": 3, "y": 2, "z": 1 } } ]""";

    private const string Containers = """
        [
          { "containerKind": "cabinet", "at": { "x": 1, "y": 1, "z": 1 }, "area": "main" },
          { "containerKind": "shelf", "at": { "x": 3, "y": 1, "z": 1 }, "area": "main" }
        ]
        """;

    private static string StructureText(string id = "t:structure/hut", string palette = Palette, string layers = Layers, string areas = Areas, string containers = Containers) =>
        $$"""{ "id": "{{id}}", "palette": {{palette}}, "layers": {{layers}}, "areas": {{areas}}, "containers": {{containers}} }""";

    private static string KitchenArea(string name = "main") =>
        $$"""[ { "name": "{{name}}", "areaType": "t:area_type/kitchen", "min": { "x": 1, "y": 1, "z": 1 }, "max": { "x": 3, "y": 2, "z": 1 } } ]""";

    private static Structure Hut(string id = "t:structure/hut", bool kitchen = false) =>
        StructureJson.Parse(StructureText(id, areas: kitchen ? KitchenArea() : Areas));

    private static readonly ZombieSpawnRule[] DefaultZombies = [new ZombieSpawnRule("t:zombie/walker", 3, 6)];

    private static SettlementType Type(
        string id,
        int rarity = 1,
        int minDanger = 1,
        int maxDanger = 10,
        int minHuts = 2,
        int maxHuts = 4,
        bool inhabited = false,
        IEnumerable<ZombieSpawnRule>? zombies = null) =>
        new(
            id,
            inhabited,
            rarity,
            minDanger,
            maxDanger,
            [new StructureCount("t:structure/hut", minHuts, maxHuts), new StructureCount("t:structure/kitchen_hut", 1, 1)],
            [new WeightedAreaType("t:area_type/bedroom", 3), new WeightedAreaType("t:area_type/bathroom", 1)],
            zombies ?? DefaultZombies);

    private static SettlementContent Content(params SettlementType[] types) =>
        new([Hut(), Hut("t:structure/kitchen_hut", kitchen: true)], types);

    private static readonly RegionGrid EverySite = new(2024, siteChancePercent: 100);

    // Structure -------------------------------------------------------------------------------------------------

    [Fact]
    public void Structure_ParsesItsCellsAreasAndContainers()
    {
        var hut = Hut();

        Assert.Equal((5, 4, 3), (hut.Width, hut.Height, hut.Depth));
        Assert.Equal("planks", hut.BlockAt(0, 0, 0));
        Assert.Equal("air", hut.BlockAt(2, 1, 2));
        Assert.Equal("planks", hut.BlockAt(1, 1, 2));
        var area = Assert.Single(hut.Areas);
        Assert.Equal("main", area.Name);
        Assert.Null(area.AreaType);
        Assert.Equal(["cabinet", "shelf"], hut.Containers.Select(c => c.ContainerKind));
        Assert.Equal("main", hut.AreaAt(new StructurePos(2, 1, 1))?.Name);
        Assert.Null(hut.AreaAt(new StructurePos(0, 0, 0)));
        Assert.Equal("t:area_type/kitchen", Hut(kitchen: true).Areas[0].AreaType);
    }

    [Fact]
    public void Structure_RejectsCellsThatDoNotAddUp()
    {
        const string ragged = """[ ["#####", "#####", "####"], ["#####", "#...#", "##.##"] ]""";
        const string unknownSymbol = """[ ["#####", "#####", "#####"], ["#####", "#.X.#", "##.##"] ]""";
        const string airFloor = """[ ["#####", "##.##", "#####"], ["#####", "#...#", "##.##"] ]""";
        const string missingRow = """[ ["#####", "#####", "#####"], ["#####", "#...#"] ]""";

        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(layers: ragged)));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(layers: unknownSymbol)));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(layers: airFloor)));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(layers: missingRow)));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(layers: "[]")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(layers: "[" + string.Join(",", Enumerable.Repeat("""["#"]""", Structure.MaxHeight + 1)) + "]")));
    }

    [Fact]
    public void Structure_RejectsABadPalette()
    {
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(palette: """[ { "symbol": "##", "block": "planks" } ]""")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(palette: """[ { "symbol": "#", "block": "planks" }, { "symbol": "#", "block": "air" }, { "symbol": ".", "block": "air" } ]""")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(palette: """[ { "symbol": "#", "block": "Planks" }, { "symbol": ".", "block": "air" } ]""")));
    }

    [Fact]
    public void Structure_RejectsAreasAndContainersThatDoNotFit()
    {
        const string outside = """[ { "name": "main", "min": { "x": 1, "y": 1, "z": 1 }, "max": { "x": 9, "y": 2, "z": 1 } } ]""";
        const string twoSame = """[ { "name": "a", "min": { "x": 1, "y": 1, "z": 1 }, "max": { "x": 2, "y": 2, "z": 1 } }, { "name": "a", "min": { "x": 3, "y": 1, "z": 1 }, "max": { "x": 3, "y": 2, "z": 1 } } ]""";
        const string overlapping = """[ { "name": "a", "min": { "x": 1, "y": 1, "z": 1 }, "max": { "x": 2, "y": 2, "z": 1 } }, { "name": "b", "min": { "x": 2, "y": 1, "z": 1 }, "max": { "x": 3, "y": 2, "z": 1 } } ]""";
        const string reversed = """[ { "name": "main", "min": { "x": 3, "y": 1, "z": 1 }, "max": { "x": 1, "y": 2, "z": 1 } } ]""";
        const string badType = """[ { "name": "main", "areaType": "nope", "min": { "x": 1, "y": 1, "z": 1 }, "max": { "x": 3, "y": 2, "z": 1 } } ]""";

        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(areas: outside)));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(areas: twoSame, containers: "[]")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(areas: overlapping, containers: "[]")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(areas: reversed, containers: "[]")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(areas: badType, containers: "[]")));

        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(containers: """[ { "containerKind": "cabinet", "at": { "x": 1, "y": 1, "z": 1 }, "area": "nowhere" } ]""")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(containers: """[ { "containerKind": "cabinet", "at": { "x": 4, "y": 1, "z": 1 }, "area": "main" } ]""")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(containers: """[ { "containerKind": "cabinet", "at": { "x": 1, "y": 0, "z": 1 }, "area": "main" } ]""")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(containers: """[ { "containerKind": "Bad Kind", "at": { "x": 1, "y": 1, "z": 1 }, "area": "main" } ]""")));
        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(containers: """[ { "containerKind": "cabinet", "at": { "x": 1, "y": 1, "z": 1 }, "area": "main" }, { "containerKind": "shelf", "at": { "x": 1, "y": 1, "z": 1 }, "area": "main" } ]""")));
    }

    [Fact]
    public void Structure_ARoomMustBeBuiltOfAirForAContainerToStandIn()
    {
        // The area spans the whole room and the wall column x = 0, so a container in the wall is inside the area but not in empty space.
        const string wide = """[ { "name": "main", "min": { "x": 0, "y": 1, "z": 1 }, "max": { "x": 3, "y": 2, "z": 1 } } ]""";

        Assert.Throws<StructureException>(() => StructureJson.Parse(StructureText(areas: wide, containers: """[ { "containerKind": "cabinet", "at": { "x": 0, "y": 1, "z": 1 }, "area": "main" } ]""")));
    }

    [Theory]
    [InlineData("""{ "palette": [] }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Structure_RejectsJsonThatIsNotAStructure(string json)
    {
        Assert.Throws<StructureException>(() => StructureJson.Parse(json));
    }

    // Settlement type -------------------------------------------------------------------------------------------

    private const string VillageJson = """
        {
          "id": "t:settlement_type/hamlet",
          "inhabited": true,
          "rarity": 4,
          "danger": { "min": 2, "max": 6 },
          "structures": [ { "structure": "t:structure/hut", "count": { "min": 1, "max": 3 } } ],
          "areaTypes": [ { "areaType": "t:area_type/kitchen", "weight": 3 }, { "areaType": "t:area_type/bedroom" } ],
          "zombieSpawns": [ { "zombieType": "t:zombie/walker", "count": { "min": 0, "max": 5 } } ]
        }
        """;

    [Fact]
    public void SettlementTypeJson_ParsesEveryField()
    {
        var type = SettlementTypeJson.Parse(VillageJson);

        Assert.Equal("t:settlement_type/hamlet", type.Id);
        Assert.True(type.Inhabited);
        Assert.Equal(4, type.Rarity);
        Assert.Equal((2, 6), (type.MinDanger, type.MaxDanger));
        Assert.Equal(new StructureCount("t:structure/hut", 1, 3), Assert.Single(type.Structures));
        Assert.Equal([new WeightedAreaType("t:area_type/kitchen", 3), new WeightedAreaType("t:area_type/bedroom", 1)], type.AreaTypes);
        Assert.Equal(new ZombieSpawnRule("t:zombie/walker", 0, 5), Assert.Single(type.ZombieSpawns));
        Assert.False(type.AllowsDanger(1));
        Assert.True(type.AllowsDanger(2));
        Assert.True(type.AllowsDanger(6));
        Assert.False(type.AllowsDanger(7));
    }

    [Fact]
    public void SettlementTypeJson_AnAbandonedSettlementIsJustTheFlagOff()
    {
        var type = SettlementTypeJson.Parse(VillageJson.Replace("\"inhabited\": true", "\"inhabited\": false", StringComparison.Ordinal));

        Assert.False(type.Inhabited);
    }

    [Theory]
    [InlineData("\"rarity\": 4", "\"rarity\": 0")]
    [InlineData("\"min\": 2, \"max\": 6", "\"min\": 0, \"max\": 6")]
    [InlineData("\"min\": 2, \"max\": 6", "\"min\": 2, \"max\": 11")]
    [InlineData("\"min\": 2, \"max\": 6", "\"min\": 7, \"max\": 3")]
    [InlineData("\"min\": 1, \"max\": 3", "\"min\": 3, \"max\": 1")]
    [InlineData("\"min\": 1, \"max\": 3", "\"min\": 0, \"max\": 0")]
    [InlineData("\"min\": 1, \"max\": 3", "\"min\": 1, \"max\": 99")]
    [InlineData("\"min\": 0, \"max\": 5", "\"min\": 0, \"max\": 99")]
    [InlineData("\"structure\": \"t:structure/hut\"", "\"structure\": \"hut\"")]
    [InlineData("\"zombieType\": \"t:zombie/walker\"", "\"zombieType\": \"Walker\"")]
    [InlineData("\"areaType\": \"t:area_type/bedroom\"", "\"areaType\": \"t:area_type/bedroom\", \"weight\": 0")]
    [InlineData("\"id\": \"t:settlement_type/hamlet\"", "\"id\": \"Hamlet\"")]
    [InlineData("\"inhabited\": true,", "")]
    public void SettlementTypeJson_RejectsInvalidDefinitions(string original, string replacement)
    {
        Assert.Throws<SettlementTypeException>(() => SettlementTypeJson.Parse(VillageJson.Replace(original, replacement, StringComparison.Ordinal)));
    }

    [Fact]
    public void SettlementTypeJson_RejectsNoStructuresAndNonObjects()
    {
        Assert.Throws<SettlementTypeException>(() => SettlementTypeJson.Parse(VillageJson.Replace("""[ { "structure": "t:structure/hut", "count": { "min": 1, "max": 3 } } ]""", "[]", StringComparison.Ordinal)));
        Assert.Throws<SettlementTypeException>(() => SettlementTypeJson.Parse("[]"));
        Assert.Throws<SettlementTypeException>(() => SettlementTypeJson.Parse("not json"));
    }

    // Content ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Content_RejectsPartsThatDoNotFitTogether()
    {
        var hut = Hut();
        var kitchenHut = Hut("t:structure/kitchen_hut", kitchen: true);

        Assert.Throws<ArgumentException>(() => new SettlementContent([hut, hut], []));
        Assert.Throws<ArgumentException>(() => new SettlementContent([hut, kitchenHut], [Type("t:settlement_type/a"), Type("t:settlement_type/a")]));
        Assert.Throws<ArgumentException>(() => new SettlementContent([hut], [Type("t:settlement_type/a")]));

        var noPool = new SettlementType("t:settlement_type/b", false, 1, 1, 10, [new StructureCount("t:structure/hut", 1, 1)], [], []);
        Assert.Throws<ArgumentException>(() => new SettlementContent([hut], [noPool]));
        _ = new SettlementContent([kitchenHut], [new SettlementType("t:settlement_type/c", false, 1, 1, 10, [new StructureCount("t:structure/kitchen_hut", 1, 1)], [], [])]);

        // A 32 block wide Structure takes a 38 block cell. A grid of 9 columns (65 Structures) is 342 blocks across, more than the 320 a Region leaves between its margins; 8 columns (64) is 304.
        var wall = $"[\"{new string('#', Structure.MaxWidth)}\"]";
        var big = StructureJson.Parse(StructureText("t:structure/big", layers: $"[{wall}, {wall}]", areas: "[]", containers: "[]"));
        var tooBig = new SettlementType("t:settlement_type/d", false, 1, 1, 10, [new StructureCount("t:structure/big", 1, 65)], [], []);
        Assert.Throws<ArgumentException>(() => new SettlementContent([big], [tooBig]));
        _ = new SettlementContent([big], [new SettlementType("t:settlement_type/e", false, 1, 1, 10, [new StructureCount("t:structure/big", 1, 64)], [], [])]);
    }

    // Planning --------------------------------------------------------------------------------------------------

    private static IReadOnlyList<RegionCoord> SomeRegions() =>
        [.. Enumerable.Range(-6, 13).SelectMany(x => Enumerable.Range(-6, 13).Select(z => new RegionCoord(x, z))).Where(r => r != default)];

    private static string Describe(SettlementPlan plan) =>
        $"{plan.Type.Id}|{plan.Danger}|" + string.Join(";", plan.Structures.Select(s => $"{s.Structure.Id}@{s.X},{s.Z}"))
        + "|" + string.Join(";", plan.Areas.Select(a => $"{a.StructureIndex}:{a.Name}={a.AreaType}"))
        + "|" + string.Join(";", plan.ContainersAt(_ => 64).Select(c => $"{c.X},{c.Y},{c.Z},{c.ContainerKind},{c.AreaType},{c.Seed}"))
        + "|" + string.Join(";", plan.ZombieSpawns.Select(z => $"{z.ZombieType}@{z.X},{z.Z}"));

    [Fact]
    public void Plan_IsNullWhereThereIsNoSiteAndNoTypeFits()
    {
        var content = Content(Type("t:settlement_type/hamlet"));

        Assert.Null(SettlementPlanner.Plan(content, EverySite, default));
        Assert.Null(SettlementPlanner.Plan(content, new RegionGrid(1, siteChancePercent: 0), new RegionCoord(3, 3)));

        var farOnly = Content(Type("t:settlement_type/far", minDanger: 9, maxDanger: 10));
        Assert.Null(SettlementPlanner.Plan(farOnly, EverySite, new RegionCoord(1, 0)));
        Assert.NotNull(SettlementPlanner.Plan(farOnly, EverySite, new RegionCoord(40, 40)));
    }

    [Fact]
    public void Plan_IsTheSameEveryTimeAndForEveryCaller()
    {
        var content = Content(Type("t:settlement_type/hamlet"));
        var again = Content(Type("t:settlement_type/hamlet"));

        foreach (var region in SomeRegions().Take(40))
        {
            var first = SettlementPlanner.Plan(content, EverySite, region);
            var second = SettlementPlanner.Plan(again, new RegionGrid(2024, siteChancePercent: 100), region);

            Assert.NotNull(first);
            Assert.Equal(Describe(first), Describe(second!));
        }
    }

    [Fact]
    public void Plan_DiffersBySeedAndByRegion()
    {
        var content = Content(Type("t:settlement_type/hamlet"));
        var region = new RegionCoord(4, -3);

        var a = Describe(SettlementPlanner.Plan(content, new RegionGrid(1, 100), region)!);
        var b = Describe(SettlementPlanner.Plan(content, new RegionGrid(2, 100), region)!);
        var c = Describe(SettlementPlanner.Plan(content, new RegionGrid(1, 100), new RegionCoord(4, -2))!);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Choose_OnlyConsidersTypesWhoseDangerRangeFits()
    {
        var mild = Type("t:settlement_type/mild", maxDanger: 4);
        var deadly = Type("t:settlement_type/deadly", minDanger: 5);
        var content = Content(mild, deadly);

        for (ulong seed = 0; seed < 200; seed++)
        {
            Assert.Equal(mild, SettlementPlanner.Choose(content, 2, seed));
            Assert.Equal(deadly, SettlementPlanner.Choose(content, 8, seed));
        }

        Assert.Null(SettlementPlanner.Choose(Content(deadly), 4, 1));
    }

    [Fact]
    public void Choose_FollowsRarityWeights()
    {
        var common = Type("t:settlement_type/common", rarity: 9);
        var rare = Type("t:settlement_type/rare", rarity: 1);
        var content = Content(common, rare);

        var commonCount = Enumerable.Range(0, 2000).Count(seed => SettlementPlanner.Choose(content, 3, (ulong)seed) == common);

        Assert.InRange(commonCount, 1700, 1900);
    }

    [Fact]
    public void Plan_PutsTheCountsOfEachStructureInRange()
    {
        var content = Content(Type("t:settlement_type/hamlet", minHuts: 2, maxHuts: 4));
        var seen = new HashSet<int>();

        foreach (var region in SomeRegions())
        {
            var plan = SettlementPlanner.Plan(content, EverySite, region)!;
            var huts = plan.Structures.Count(s => s.Structure.Id == "t:structure/hut");
            Assert.InRange(huts, 2, 4);
            Assert.Equal(1, plan.Structures.Count(s => s.Structure.Id == "t:structure/kitchen_hut"));
            seen.Add(huts);
        }

        Assert.Equal([2, 3, 4], seen.Order());
    }

    [Fact]
    public void Plan_KeepsStructuresApartAndInsideTheirRegion()
    {
        var content = Content(Type("t:settlement_type/hamlet", minHuts: 6, maxHuts: 10));

        foreach (var region in SomeRegions().Take(60))
        {
            var plan = SettlementPlanner.Plan(content, EverySite, region)!;
            var yard = SettlementContent.YardMargin;
            foreach (var s in plan.Structures)
            {
                Assert.Equal(region, RegionGrid.RegionOf(s.X - yard, s.Z - yard));
                Assert.Equal(region, RegionGrid.RegionOf(s.MaxX + yard, s.MaxZ + yard));
            }

            foreach (var a in plan.Structures)
            {
                foreach (var b in plan.Structures.Where(other => other.Index > a.Index))
                {
                    var apart = a.MaxX + yard < b.X - yard || b.MaxX + yard < a.X - yard || a.MaxZ + yard < b.Z - yard || b.MaxZ + yard < a.Z - yard;
                    Assert.True(apart, $"{a.Index} and {b.Index} share ground in {region}");
                }
            }
        }
    }

    [Fact]
    public void Plan_GivesEveryAreaAnAreaType_AFixedOneIsKeptAndAnOpenOneComesFromTheSettlementType()
    {
        var content = Content(Type("t:settlement_type/hamlet", minHuts: 4, maxHuts: 4));
        var openTypes = new HashSet<string>();

        foreach (var region in SomeRegions().Take(40))
        {
            var plan = SettlementPlanner.Plan(content, EverySite, region)!;
            foreach (var placed in plan.Structures)
            {
                var area = Assert.Single(placed.Structure.Areas);
                var given = plan.AreaTypeOf(placed.Index, area.Name);
                if (area.AreaType is { } fixedType)
                {
                    Assert.Equal(fixedType, given);
                }
                else
                {
                    Assert.True(given is "t:area_type/bedroom" or "t:area_type/bathroom", given);
                    openTypes.Add(given);
                }
            }
        }

        Assert.Equal(["t:area_type/bathroom", "t:area_type/bedroom"], openTypes.Order());
    }

    [Fact]
    public void Plan_TwoIdenticalStructuresCanBeDifferentRooms()
    {
        var content = Content(Type("t:settlement_type/hamlet", minHuts: 10, maxHuts: 10));

        var differing = SomeRegions().Take(30).Count(region =>
        {
            var plan = SettlementPlanner.Plan(content, EverySite, region)!;
            return plan.Areas.Where(a => plan.Structures[a.StructureIndex].Structure.Id == "t:structure/hut").Select(a => a.AreaType).Distinct().Count() > 1;
        });

        Assert.True(differing >= 10, $"only {differing} of 30 settlements had two kinds of room in the same house");
    }

    [Fact]
    public void Plan_PlacesContainersInsideTheirStructureWithTheAreaTypeOfTheirRoom()
    {
        var content = Content(Type("t:settlement_type/hamlet"));

        foreach (var region in SomeRegions().Take(20))
        {
            var plan = SettlementPlanner.Plan(content, EverySite, region)!;
            var containers = plan.ContainersAt(s => 100 + s.Index);

            Assert.Equal(plan.Structures.Sum(s => s.Structure.Containers.Count), containers.Count);
            foreach (var container in containers)
            {
                var placed = plan.Structures.Single(s => container.X >= s.X && container.X <= s.MaxX && container.Z >= s.Z && container.Z <= s.MaxZ);
                Assert.Equal(100 + placed.Index + 1, container.Y);
                Assert.Equal(plan.AreaTypeOf(placed.Index, container.AreaName), container.AreaType);
                Assert.Equal(plan.Danger, container.Danger);
            }

            Assert.Equal(containers.Count, containers.Select(c => c.Seed).Distinct().Count());
        }
    }

    [Fact]
    public void Plan_ZombiesSpawnInRangeAndNeverInsideAStructureOrItsYard()
    {
        var content = Content(Type("t:settlement_type/hamlet", zombies: [new ZombieSpawnRule("t:zombie/walker", 2, 5), new ZombieSpawnRule("t:zombie/runner", 1, 1)]));
        var yard = SettlementContent.YardMargin;

        foreach (var region in SomeRegions())
        {
            var plan = SettlementPlanner.Plan(content, EverySite, region)!;

            Assert.InRange(plan.ZombieSpawns.Count(z => z.ZombieType == "t:zombie/walker"), 2, 5);
            Assert.Equal(1, plan.ZombieSpawns.Count(z => z.ZombieType == "t:zombie/runner"));
            foreach (var spawn in plan.ZombieSpawns)
            {
                Assert.Equal(region, RegionGrid.RegionOf(spawn.X, spawn.Z));
                Assert.DoesNotContain(plan.Structures, s => spawn.X >= s.X - yard && spawn.X <= s.MaxX + yard && spawn.Z >= s.Z - yard && spawn.Z <= s.MaxZ + yard);
            }
        }
    }

    [Fact]
    public void Plan_ASettlementTypeWithNoZombiesSpawnsNone_AndInhabitedIsJustCarried()
    {
        var content = Content(Type("t:settlement_type/town", inhabited: true, zombies: []));

        var plan = SettlementPlanner.Plan(content, EverySite, new RegionCoord(2, 2))!;

        Assert.Empty(plan.ZombieSpawns);
        Assert.True(plan.Type.Inhabited);
    }

    [Fact]
    public void Plan_AStructureCountOfZeroCanLeaveOnlyTheOtherStructures()
    {
        var content = Content(Type("t:settlement_type/hamlet", minHuts: 0, maxHuts: 1));

        var counts = SomeRegions().Select(r => SettlementPlanner.Plan(content, EverySite, r)!.Structures.Count(s => s.Structure.Id == "t:structure/hut")).ToHashSet();

        Assert.Equal([0, 1], counts.Order());
    }

    // Targeting -------------------------------------------------------------------------------------------------

    private static WorldContainer At(int x, int y, int z) => new(x, y, z, "cabinet", "main", "t:area_type/kitchen", 1, 7);

    [Fact]
    public void Targeting_FindsTheContainerThePlayerLooksAt()
    {
        var cabinet = At(10, 5, 10);

        var found = ContainerTargeting.Find([cabinet], new Vector3(10.5f, 5.5f, 8f), Vector3.UnitZ);

        Assert.Equal(cabinet, found);
    }

    [Fact]
    public void Targeting_IgnoresWhatIsOutOfReachBehindOrBesideTheRay()
    {
        var cabinet = At(10, 5, 10);
        var eye = new Vector3(10.5f, 5.5f, 5f);

        Assert.Null(ContainerTargeting.Find([cabinet], eye, Vector3.UnitZ));
        Assert.NotNull(ContainerTargeting.Find([cabinet], eye, Vector3.UnitZ, reach: 6f));
        Assert.Null(ContainerTargeting.Find([cabinet], new Vector3(10.5f, 5.5f, 12f), Vector3.UnitZ, reach: 6f));
        Assert.Null(ContainerTargeting.Find([cabinet], new Vector3(12.5f, 5.5f, 8f), Vector3.UnitZ));
        Assert.Null(ContainerTargeting.Find([cabinet], new Vector3(10.5f, 5.5f, 8f), Vector3.Zero));
    }

    [Fact]
    public void Targeting_TakesTheNearerOfTwoInLine_AndWorksOnAnAngle()
    {
        var near = At(10, 5, 10);
        var far = At(10, 5, 11);

        Assert.Equal(near, ContainerTargeting.Find([far, near], new Vector3(10.5f, 5.5f, 8f), Vector3.UnitZ));
        Assert.Equal(near, ContainerTargeting.Find([near], new Vector3(8.5f, 6.5f, 8f), new Vector3(2f, -1f, 2f), reach: 4f));
    }
}
