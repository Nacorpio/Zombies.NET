using UnitsNet;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class LootTests
{
    private static readonly ItemId Beans = new("base:item/canned_beans");
    private static readonly ItemId Water = new("base:item/water_bottle");
    private static readonly ItemId Ghost = new("base:item/ghost");
    private static readonly ContainerId Bag = new(1);

    private static readonly ItemCatalog Items = new(
    [
        new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), maxStack: 4),
        new ItemDefinition(Water, Mass.FromKilograms(1.0), Volume.FromLiters(1.0), maxStack: 2),
    ]);

    private static LootTable Table(int minRolls, int maxRolls, params LootEntry[] entries) => new("test:loot/t", minRolls, maxRolls, entries);

    private static LootService ServiceFor(LootTable table) => new(new LootTableCatalog([table]));

    private static (InventoryService Inventory, Container Container, ContainerItemSink Sink) NewBag(Mass massLimit, Volume volumeLimit)
    {
        var repository = new InMemoryContainerRepository();
        var inventory = new InventoryService(Items, repository);
        var container = new Container(Bag, massLimit, volumeLimit, Items);
        inventory.AddContainer(container);
        return (inventory, container, new ContainerItemSink(inventory, Bag));
    }

    private sealed class CappedSink(int perOffer) : IItemSink
    {
        public int Offer(ItemId item, int count) => Math.Min(count, perOffer);
    }

    private sealed class OverReportingSink : IItemSink
    {
        public int Offer(ItemId item, int count) => count + 5;
    }

    [Fact]
    public void DeterministicRandom_MatchesKnownSplitMix64Outputs()
    {
        var random = new DeterministicRandom(0);

        Assert.Equal(0xE220A8397B1DCDAFUL, random.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, random.NextUInt64());
        Assert.Equal(0x06C45D188009454FUL, random.NextUInt64());
    }

    [Fact]
    public void NextInt_StaysInRangeAndReachesBothEnds()
    {
        var random = new DeterministicRandom(7);
        var seen = new HashSet<int>();

        for (var i = 0; i < 1000; i++)
        {
            var value = random.NextInt(-3, 3);
            Assert.InRange(value, -3, 3);
            seen.Add(value);
        }

        Assert.Equal(7, seen.Count);
    }

    [Fact]
    public void NextInt_HandlesDegenerateAndFullRanges()
    {
        var random = new DeterministicRandom(1);

        Assert.Equal(5, random.NextInt(5, 5));
        _ = random.NextInt(int.MinValue, int.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(2, 1));
    }

    [Fact]
    public void NextBelow_RejectsZeroAndIsZeroForOne()
    {
        var random = new DeterministicRandom(1);

        Assert.Equal(0UL, random.NextBelow(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextBelow(0));
    }

    [Fact]
    public void Combine_IsStableAndOrderSensitive()
    {
        Assert.Equal(DeterministicRandom.Combine(1, 2), DeterministicRandom.Combine(1, 2));
        Assert.NotEqual(DeterministicRandom.Combine(1, 2), DeterministicRandom.Combine(2, 1));
    }

    [Fact]
    public void Roll_SameSeedGivesSameDropsAndSeedsDiffer()
    {
        var table = Table(1, 4, new LootEntry(Beans, 3, 1, 3), new LootEntry(Water, 1, 1, 2));

        var outcomes = Enumerable.Range(0, 50).Select(s => string.Join(',', LootService.Roll(table, (ulong)s).Select(d => $"{d.Item}x{d.Count}"))).ToList();
        var again = Enumerable.Range(0, 50).Select(s => string.Join(',', LootService.Roll(table, (ulong)s).Select(d => $"{d.Item}x{d.Count}"))).ToList();

        Assert.Equal(outcomes, again);
        Assert.True(outcomes.Distinct().Count() > 5);
    }

    [Fact]
    public void Roll_RespectsRollAndCountBounds()
    {
        var table = Table(2, 3, new LootEntry(Beans, 1, 2, 5));

        for (ulong seed = 0; seed < 300; seed++)
        {
            var drop = Assert.Single(LootService.Roll(table, seed));
            Assert.InRange(drop.Count, 4, 15);
        }
    }

    [Fact]
    public void Roll_WeightsBiasThePicks()
    {
        var table = Table(1, 1, new LootEntry(Beans, 9, 1, 1), new LootEntry(Water, 1, 1, 1));

        var beans = Enumerable.Range(0, 4000).Count(s => LootService.Roll(table, (ulong)s).Single().Item == Beans);

        Assert.InRange(beans / 4000.0, 0.85, 0.95);
    }

    [Fact]
    public void Roll_WithZeroRolls_DropsNothing()
    {
        Assert.Empty(LootService.Roll(Table(0, 0, new LootEntry(Beans, 1, 1, 1)), 42));
    }

    [Fact]
    public void Roll_MergesRepeatedItems()
    {
        var table = Table(5, 5, new LootEntry(Beans, 1, 1, 1), new LootEntry(Water, 1, 1, 1));

        var drops = LootService.Roll(table, 3);

        Assert.Equal(drops.Select(d => d.Item).Distinct().Count(), drops.Count);
        Assert.Equal(5, drops.Sum(d => d.Count));
    }

    [Fact]
    public void Table_RejectsInvalidShapes()
    {
        Assert.Throws<ArgumentException>(() => Table(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Table(3, 2, new LootEntry(Beans, 1, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Table(-1, 2, new LootEntry(Beans, 1, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootEntry(Beans, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootEntry(Beans, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootEntry(Beans, 1, 3, 2));
    }

    [Fact]
    public void Json_ParsesATableWithDefaults()
    {
        var table = LootTableJson.Parse("""
            {
              "id": "base:loot/kitchen",
              "rolls": { "min": 1, "max": 3 },
              "entries": [
                { "item": "base:item/canned_beans", "weight": 10, "count": { "min": 1, "max": 3 } },
                { "item": "base:item/water_bottle" }
              ]
            }
            """);

        Assert.Equal("base:loot/kitchen", table.Id);
        Assert.Equal((1, 3), (table.MinRolls, table.MaxRolls));
        Assert.Equal(new LootEntry(Beans, 10, 1, 3), table.Entries[0]);
        Assert.Equal(new LootEntry(Water, 1, 1, 1), table.Entries[1]);
        Assert.Equal(11, table.TotalWeight);
    }

    [Theory]
    [InlineData("""{ "id": "a:loot/x", "rolls": { "min": 1, "max": 2 }, "entries": [] }""")]
    [InlineData("""{ "id": "a:loot/x", "rolls": { "min": 3, "max": 2 }, "entries": [{ "item": "base:item/canned_beans" }] }""")]
    [InlineData("""{ "id": "a:loot/x", "rolls": { "min": 1, "max": 2 }, "entries": [{ "item": "Bad Item" }] }""")]
    [InlineData("""{ "id": "a:loot/x", "rolls": { "min": 1, "max": 2 }, "entries": [{ "item": "base:item/canned_beans", "weight": 0 }] }""")]
    [InlineData("""{ "id": "a:loot/x", "rolls": { "min": 1, "max": 2 }, "entries": [{ "item": "base:item/canned_beans", "count": { "min": 0, "max": 1 } }] }""")]
    [InlineData("""{ "id": "a:loot/x", "entries": [{ "item": "base:item/canned_beans" }] }""")]
    [InlineData("""{ "rolls": { "min": 1, "max": 2 }, "entries": [{ "item": "base:item/canned_beans" }] }""")]
    [InlineData("""{ "id": "a:loot/x", "rolls": { "min": "1", "max": 2 }, "entries": [{ "item": "base:item/canned_beans" }] }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Json_RejectsInvalidTables(string json)
    {
        Assert.Throws<LootTableException>(() => LootTableJson.Parse(json));
    }

    [Fact]
    public void UnknownItems_ListsEntriesTheCatalogDoesNotDefine()
    {
        var table = Table(1, 1, new LootEntry(Beans, 1, 1, 1), new LootEntry(Ghost, 1, 1, 1), new LootEntry(Ghost, 2, 1, 1));

        Assert.Equal([Ghost], table.UnknownItems(Items));
    }

    [Fact]
    public void Fill_WithUnknownTable_Fails()
    {
        var result = ServiceFor(Table(1, 1, new LootEntry(Beans, 1, 1, 1))).Fill("nope:loot/x", 1, new CappedSink(10));

        Assert.Equal(LootError.UnknownTable, result.Error);
        Assert.Empty(result.Rolled);
    }

    [Fact]
    public void Fill_SameSeedFillsTwoContainersIdentically()
    {
        var service = ServiceFor(Table(2, 5, new LootEntry(Beans, 3, 1, 3), new LootEntry(Water, 1, 1, 2)));
        var first = NewBag(Mass.FromKilograms(50), Volume.FromLiters(50));
        var second = NewBag(Mass.FromKilograms(50), Volume.FromLiters(50));

        var a = service.Fill("test:loot/t", 99, first.Sink);
        var b = service.Fill("test:loot/t", 99, second.Sink);

        Assert.True(a.IsSuccess);
        Assert.NotEmpty(first.Container.Stacks);
        Assert.Equal(first.Container.Stacks, second.Container.Stacks);
        Assert.Equal(a.Placed, b.Placed);
        Assert.Empty(a.Discarded);
    }

    [Fact]
    public void Fill_RespectsTheMassLimit()
    {
        var service = ServiceFor(Table(1, 1, new LootEntry(Beans, 1, 5, 5)));
        var bag = NewBag(Mass.FromKilograms(1.2), Volume.FromLiters(100));

        var result = service.Fill("test:loot/t", 1, bag.Sink);

        Assert.Equal(new LootDrop(Beans, 3), Assert.Single(result.Placed));
        Assert.Equal(new LootDrop(Beans, 2), Assert.Single(result.Discarded));
        Assert.Equal(3, bag.Container.CountOf(Beans));
        Assert.True(bag.Container.TotalMass.Kilograms <= 1.2 + 1e-9);
    }

    [Fact]
    public void Fill_RespectsTheVolumeLimit()
    {
        var service = ServiceFor(Table(1, 1, new LootEntry(Beans, 1, 5, 5)));
        var bag = NewBag(Mass.FromKilograms(100), Volume.FromLiters(1));

        var result = service.Fill("test:loot/t", 1, bag.Sink);

        Assert.Equal(new LootDrop(Beans, 2), Assert.Single(result.Placed));
        Assert.Equal(new LootDrop(Beans, 3), Assert.Single(result.Discarded));
        Assert.True(bag.Container.TotalVolume.Liters <= 1 + 1e-9);
    }

    [Fact]
    public void Fill_DiscardsItemsTheInventoryDoesNotKnowWithoutFailing()
    {
        var service = ServiceFor(Table(1, 1, new LootEntry(Ghost, 1, 2, 2)));
        var bag = NewBag(Mass.FromKilograms(10), Volume.FromLiters(10));

        var result = service.Fill("test:loot/t", 1, bag.Sink);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Placed);
        Assert.Equal(new LootDrop(Ghost, 2), Assert.Single(result.Discarded));
        Assert.Empty(bag.Container.Stacks);
    }

    [Fact]
    public void Fill_PlacedPlusDiscardedAlwaysEqualsRolled()
    {
        var service = ServiceFor(Table(3, 6, new LootEntry(Beans, 1, 1, 6), new LootEntry(Water, 1, 1, 6)));

        for (ulong seed = 0; seed < 100; seed++)
        {
            var result = service.Fill("test:loot/t", seed, new CappedSink(2));

            Assert.All(result.Placed, d => Assert.InRange(d.Count, 1, 2));
            Assert.Equal(result.Rolled.Sum(d => d.Count), result.Placed.Sum(d => d.Count) + result.Discarded.Sum(d => d.Count));
        }
    }

    [Fact]
    public void Fill_NeverPlacesMoreThanWasRolled_EvenIfASinkOverReports()
    {
        var service = ServiceFor(Table(3, 6, new LootEntry(Beans, 1, 1, 6), new LootEntry(Water, 1, 1, 6)));

        for (ulong seed = 0; seed < 100; seed++)
        {
            var result = service.Fill("test:loot/t", seed, new OverReportingSink());

            Assert.Equal(result.Rolled, result.Placed);
            Assert.Empty(result.Discarded);
        }
    }

    [Fact]
    public void ContainerItemSink_AcceptsWhatFitsAndCollectsEvents()
    {
        var bag = NewBag(Mass.FromKilograms(1.2), Volume.FromLiters(100));

        var accepted = bag.Sink.Offer(Beans, 5);

        Assert.Equal(3, accepted);
        Assert.Equal(new ItemsAdded(Bag, Beans, 3), Assert.Single(bag.Sink.Events));
        Assert.Equal(0, bag.Sink.Offer(Water, 1));
        Assert.Equal(0, new ContainerItemSink(bag.Inventory, new ContainerId(99)).Offer(Beans, 1));
    }
}
