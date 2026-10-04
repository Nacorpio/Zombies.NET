using Zombies.Domain.World;

namespace Zombies.Domain.Tests;

public sealed class WorldTests
{
    private const string ForestJson = """
        {
          "id": "base:biome/temperate_forest",
          "habitats": ["grassland", "forest", "forest"],
          "climate": { "temperature": { "min": 0, "max": 100 }, "humidity": { "min": 0, "max": 100 } },
          "terrain": { "baseHeight": 52, "amplitude": 14, "treesPerThousand": 50 }
        }
        """;

    [Fact]
    public void WorldHash_MatchesKnownAnswersAndIsStable()
    {
        Assert.Equal(0x2FC0C89CDCBE023FUL, WorldHash.Mix(12345, 1, 2, 3));
        Assert.Equal(WorldHash.Mix(12345, 1, 2, 3), WorldHash.Mix(12345, 1, 2, 3));
    }

    [Fact]
    public void WorldHash_EveryInputChangesTheOutput()
    {
        var baseline = WorldHash.Mix(1, 2, 3, 4);

        Assert.NotEqual(baseline, WorldHash.Mix(2, 2, 3, 4));
        Assert.NotEqual(baseline, WorldHash.Mix(1, 3, 3, 4));
        Assert.NotEqual(baseline, WorldHash.Mix(1, 2, 4, 4));
        Assert.NotEqual(baseline, WorldHash.Mix(1, 2, 3, 5));
        Assert.NotEqual(WorldHash.Mix(1, 2, 3, 4), WorldHash.Mix(1, 3, 2, 4));
    }

    [Fact]
    public void WorldHash_HandlesNegativeCoordinates()
    {
        Assert.NotEqual(WorldHash.Mix(7, -1, 5, 0), WorldHash.Mix(7, 1, 5, 0));
    }

    [Fact]
    public void BiomeJson_ParsesAndNormalisesHabitatTags()
    {
        var biome = BiomeJson.Parse(ForestJson);

        Assert.Equal("base:biome/temperate_forest", biome.Id);
        Assert.Equal(["forest", "grassland"], biome.Habitats);
        Assert.Equal((0, 100), biome.Temperature);
        Assert.Equal(52, biome.BaseHeight);
        Assert.Equal(14, biome.Amplitude);
        Assert.Equal(50, biome.TreesPerThousand);
    }

    [Fact]
    public void BiomeJson_TreesDefaultToNone()
    {
        var biome = BiomeJson.Parse(ForestJson.Replace(", \"treesPerThousand\": 50", string.Empty, StringComparison.Ordinal));

        Assert.Equal(0, biome.TreesPerThousand);
    }

    [Theory]
    [InlineData("""{ "id": "a:biome/x", "habitats": ["Forest"], "climate": { "temperature": { "min": 0, "max": 100 }, "humidity": { "min": 0, "max": 100 } }, "terrain": { "baseHeight": 52, "amplitude": 14 } }""")]
    [InlineData("""{ "id": "a:biome/x", "habitats": [], "climate": { "temperature": { "min": 60, "max": 40 }, "humidity": { "min": 0, "max": 100 } }, "terrain": { "baseHeight": 52, "amplitude": 14 } }""")]
    [InlineData("""{ "id": "a:biome/x", "habitats": [], "climate": { "temperature": { "min": 0, "max": 101 }, "humidity": { "min": 0, "max": 100 } }, "terrain": { "baseHeight": 52, "amplitude": 14 } }""")]
    [InlineData("""{ "id": "a:biome/x", "habitats": [], "climate": { "temperature": { "min": 0, "max": 100 }, "humidity": { "min": 0, "max": 100 } }, "terrain": { "baseHeight": 0, "amplitude": 14 } }""")]
    [InlineData("""{ "id": "a:biome/x", "habitats": [], "climate": { "temperature": { "min": 0, "max": 100 }, "humidity": { "min": 0, "max": 100 } }, "terrain": { "baseHeight": 52, "amplitude": 99 } }""")]
    [InlineData("""{ "id": "a:biome/x", "habitats": [], "climate": { "temperature": { "min": 0, "max": 100 }, "humidity": { "min": 0, "max": 100 } }, "terrain": { "baseHeight": 52, "amplitude": 14, "treesPerThousand": 1001 } }""")]
    [InlineData("""{ "id": "a:biome/x", "habitats": [] }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void BiomeJson_RejectsInvalidBiomes(string json)
    {
        Assert.Throws<BiomeException>(() => BiomeJson.Parse(json));
    }

    [Fact]
    public void BiomeCatalog_SelectsByClimateAndFallsBackToTheFirstBiome()
    {
        var desert = new Biome("base:biome/desert", ["desert"], (70, 100), (0, 30), 40, 6, 0);
        var forest = new Biome("base:biome/forest", ["forest"], (20, 69), (31, 100), 52, 14, 50);
        var catalog = new BiomeCatalog([forest, desert]);

        Assert.Equal(["base:biome/desert", "base:biome/forest"], catalog.Biomes.Select(b => b.Id));
        Assert.Same(desert, catalog.Select(85, 10));
        Assert.Same(forest, catalog.Select(40, 60));
        Assert.Same(desert, catalog.Select(5, 99));
    }

    [Fact]
    public void BiomeCatalog_RejectsEmptyAndDuplicateSets()
    {
        var forest = new Biome("base:biome/forest", ["forest"], (0, 100), (0, 100), 52, 14, 50);

        Assert.Throws<ArgumentException>(() => new BiomeCatalog([]));
        Assert.Throws<ArgumentException>(() => new BiomeCatalog([forest, forest]));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(511, 511, 0)]
    [InlineData(512, 0, 1)]
    [InlineData(-1, 0, -1)]
    [InlineData(-512, 0, -1)]
    [InlineData(-513, 0, -2)]
    public void RegionOf_RoundsDownIncludingNegatives(int worldX, int worldZ, int expectedX)
    {
        var region = RegionGrid.RegionOf(worldX, worldZ);

        Assert.Equal(expectedX, region.X);
        Assert.Equal(0, region.Z);
    }

    [Fact]
    public void SiteIn_IsDeterministicInsideTheRegionAndNeverInTheSpawnRegion()
    {
        var grid = new RegionGrid(99);
        Assert.Null(grid.SiteIn(default));

        var sites = 0;
        for (var x = -20; x <= 20; x++)
        {
            for (var z = -20; z <= 20; z++)
            {
                var region = new RegionCoord(x, z);
                var site = grid.SiteIn(region);
                Assert.Equal(site, grid.SiteIn(region));
                if (site is null)
                {
                    continue;
                }

                sites++;
                Assert.Equal(region, RegionGrid.RegionOf(site.X, site.Z));
                Assert.InRange(site.X - (x * RegionGrid.Size), RegionGrid.Margin, RegionGrid.Size - RegionGrid.Margin);
                Assert.InRange(site.Z - (z * RegionGrid.Size), RegionGrid.Margin, RegionGrid.Size - RegionGrid.Margin);
            }
        }

        Assert.InRange(sites / 1680.0, 0.5, 0.7);
    }

    [Fact]
    public void SiteIn_ChanceControlsHowManyRegionsHaveASite()
    {
        var none = new RegionGrid(1, siteChancePercent: 0);
        var all = new RegionGrid(1, siteChancePercent: 100);

        Assert.All(Enumerable.Range(1, 30), x => Assert.Null(none.SiteIn(new RegionCoord(x, 1))));
        Assert.All(Enumerable.Range(1, 30), x => Assert.NotNull(all.SiteIn(new RegionCoord(x, 1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegionGrid(1, 101));
    }

    [Fact]
    public void SiteIn_DiffersBySeed()
    {
        var a = new RegionGrid(1);
        var b = new RegionGrid(2);

        var different = Enumerable.Range(1, 40).Count(x => a.SiteIn(new RegionCoord(x, 2)) != b.SiteIn(new RegionCoord(x, 2)));

        Assert.True(different > 20);
    }

    [Fact]
    public void DangerOf_MatchesKnownAnswersAndStaysInRange()
    {
        var grid = new RegionGrid(12345);

        Assert.Equal(1, grid.DangerOf(default));
        Assert.Equal(2, grid.DangerOf(new RegionCoord(3, -2)));
        Assert.Equal(5, grid.DangerOf(new RegionCoord(8, 5)));

        for (var x = -40; x <= 40; x++)
        {
            Assert.InRange(grid.DangerOf(new RegionCoord(x, 17)), RegionGrid.MinDanger, RegionGrid.MaxDanger);
        }
    }

    [Fact]
    public void DangerOf_GrowsWithDistanceFromSpawn()
    {
        var grid = new RegionGrid(7);

        double Average(int distance) => Enumerable.Range(-distance, (2 * distance) + 1).Average(z => grid.DangerOf(new RegionCoord(distance, z)));

        Assert.True(Average(2) < Average(8));
        Assert.True(Average(8) < Average(16));
        Assert.Equal(RegionGrid.MaxDanger, grid.DangerOf(new RegionCoord(60, 60)));
    }
}
