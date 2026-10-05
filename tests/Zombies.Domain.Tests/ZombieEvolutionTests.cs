using Zombies.Domain.Zombies;

namespace Zombies.Domain.Tests;

public sealed class ZombieEvolutionTests
{
    private static string Type(string name, int maxLevel = 5, string? upgrade = null, int days = 10) => $$"""
        {
          "id": "t:zombie/{{name}}",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4, "maxLevel": {{maxLevel}}, "perLevelBonus": 0.5 },
          "senses": { "sight": 20, "hearing": 30 },
          "appearance": {
            "height": { "min": 0.8, "max": 1.2 },
            "skinTones": ["#8fa07a", "#9aa88a"]
          }{{(upgrade is null ? string.Empty : $$""", "upgrade": { "zombieType": "t:zombie/{{upgrade}}", "afterDays": {{days}} }""")}}
        }
        """;

    private static ZombieCatalog Catalog(params string[] types) => new(types.Select(ZombieTypeJson.Parse));

    // walker -> runner after 10 days -> brute after 20 more.
    private static ZombieCatalog Chain() => Catalog(Type("walker", upgrade: "runner", days: 10), Type("runner", maxLevel: 4, upgrade: "brute", days: 20), Type("brute", maxLevel: 2));

    private static ZombieSpec Walker(ulong seed = 7, int level = 3) => new(seed, "t:zombie/walker", level);

    [Fact]
    public void Parse_ReadsTheUpgradeTargetAndDelay()
    {
        var walker = ZombieTypeJson.Parse(Type("walker", upgrade: "runner", days: 12));

        Assert.Equal(new ZombieUpgrade("t:zombie/runner", 12), walker.Upgrade);
        Assert.Null(ZombieTypeJson.Parse(Type("runner")).Upgrade);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AnUpgradeNeedsAtLeastOneDay(int days)
    {
        Assert.Throws<ZombieTypeDefinitionException>(() => ZombieTypeJson.Parse(Type("walker", upgrade: "runner", days: days)));
    }

    [Fact]
    public void AnUpgradeTargetMustBeAContentId()
    {
        Assert.Throws<ZombieTypeDefinitionException>(() => ZombieTypeJson.Parse(Type("walker", upgrade: "runner", days: 5).Replace("t:zombie/runner", "Runner", StringComparison.Ordinal)));
    }

    [Fact]
    public void ACatalogRefusesAnUpgradeIntoAnUnknownType()
    {
        var error = Assert.Throws<ArgumentException>(() => Catalog(Type("walker", upgrade: "runner")));

        Assert.Contains("t:zombie/runner", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACatalogRefusesCyclesOfUpgrades()
    {
        var self = Assert.Throws<ArgumentException>(() => Catalog(Type("walker", upgrade: "walker")));
        var pair = Assert.Throws<ArgumentException>(() => Catalog(Type("a", upgrade: "b"), Type("b", upgrade: "a")));
        var tail = Assert.Throws<ArgumentException>(() => Catalog(Type("a", upgrade: "b"), Type("b", upgrade: "c"), Type("c", upgrade: "b")));

        Assert.Contains("cycle", self.Message, StringComparison.Ordinal);
        Assert.Contains("t:zombie/a -> t:zombie/b -> t:zombie/a", pair.Message, StringComparison.Ordinal);
        Assert.Contains("cycle", tail.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChainResolvesToTheTypeItsAddedUpDaysReach()
    {
        var catalog = Chain();
        catalog.TryGet("t:zombie/walker", out var walker);

        Assert.Equal("t:zombie/walker", catalog.TypeAt(walker, 0).Id);
        Assert.Equal("t:zombie/walker", catalog.TypeAt(walker, 9).Id);
        Assert.Equal("t:zombie/runner", catalog.TypeAt(walker, 10).Id);
        Assert.Equal("t:zombie/runner", catalog.TypeAt(walker, 29).Id);
        Assert.Equal("t:zombie/brute", catalog.TypeAt(walker, 30).Id);
        Assert.Equal("t:zombie/brute", catalog.TypeAt(walker, int.MaxValue).Id);
    }

    [Fact]
    public void ATypeWithoutAnUpgrade_NeverChanges()
    {
        var catalog = Chain();
        catalog.TryGet("t:zombie/brute", out var brute);

        Assert.Same(brute, catalog.TypeAt(brute, 100_000));
    }

    [Fact]
    public void TheWorldCannotHaveANegativeAge()
    {
        var catalog = Chain();
        catalog.TryGet("t:zombie/walker", out var walker);

        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.TypeAt(walker, -1));
    }

    [Fact]
    public void TheSpecAcrossTheBoundary_IsTheSameOnEveryServerAndClient()
    {
        var server = Chain();
        var client = Chain();

        foreach (var day in new[] { 0, 9, 10, 11, 29, 30, 500 })
        {
            Assert.Equal(Walker().At(server, day), Walker().At(client, day));
        }

        Assert.Equal("t:zombie/walker", Walker().At(server, 9).Type);
        Assert.Equal("t:zombie/runner", Walker().At(server, 10).Type);
    }

    [Fact]
    public void AnEvolvedSpec_KeepsItsSeed_AndHoldsItsLevelToTheNewTypesTop()
    {
        var catalog = Chain();

        var runner = Walker(seed: 99, level: 5).At(catalog, 10);
        var brute = Walker(seed: 99, level: 5).At(catalog, 30);

        Assert.Equal(new ZombieSpec(99, "t:zombie/runner", 4), runner);
        Assert.Equal(new ZombieSpec(99, "t:zombie/brute", 2), brute);
        Assert.Equal(new ZombieSpec(99, "t:zombie/walker", 1), Walker(seed: 99, level: 1).At(catalog, 0));
    }

    [Fact]
    public void AnEvolvedZombie_IsGeneratedWithTheAppearanceOfItsNewType()
    {
        var catalog = Chain();
        catalog.TryGet("t:zombie/runner", out var runner);
        var evolved = Walker().At(catalog, 15);


        Assert.Equal(ZombieGenerator.Generate(runner, evolved), ZombieGenerator.Generate(runner, Walker().At(Chain(), 15)));
        Assert.Throws<ArgumentException>(() => ZombieGenerator.Generate(runner, Walker()));
    }

    [Fact]
    public void ASpecOfAnUnknownType_CannotEvolve()
    {
        Assert.Throws<ArgumentException>(() => new ZombieSpec(1, "t:zombie/none", 1).At(Chain(), 5));
    }
}
