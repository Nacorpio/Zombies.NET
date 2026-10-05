using UnitsNet;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class AreaTypeTests
{
    private static readonly ItemId Beans = new("test:item/beans");
    private static readonly ItemId Bandage = new("test:item/bandage");

    private static readonly LootTable Kitchen = new("test:loot/kitchen", 1, 1, [new LootEntry(Beans, 1, 1, 1)]);
    private static readonly LootTable Medical = new("test:loot/medical", 1, 1, [new LootEntry(Bandage, 1, 1, 1)]);
    private static readonly LootTable Military = new("test:loot/military", 1, 1, [new LootEntry(Bandage, 1, 2, 2)]);

    private static readonly AreaTypeCatalog Areas = new(
        new[]
        {
            """{ "id": "test:area_type/kitchen", "rules": [ { "containerKind": "cabinet", "tables": [ { "table": "test:loot/kitchen", "weight": 10 } ] }, { "containerKind": "fridge", "tables": [ { "table": "test:loot/kitchen", "weight": 8 }, { "table": "test:loot/medical", "weight": 2 } ] } ] }""",
            """{ "id": "test:area_type/garage", "rules": [ { "containerKind": "shelf", "tables": [ { "table": "test:loot/kitchen", "weight": 10 }, { "table": "test:loot/military", "weight": 1, "dangerWeightShift": 2 } ] } ] }""",
            """{ "id": "test:area_type/empty", "rules": [ { "containerKind": "shelf", "tables": [ { "table": "test:loot/kitchen", "weight": 1, "dangerWeightShift": -1 } ] } ] }""",
        }.Select(AreaTypeJson.Parse));

    private static readonly AreaLootService Service = new(Areas, new LootService(new LootTableCatalog([Kitchen, Medical, Military])));

    private static ItemSink Sink() => new();

    private sealed class ItemSink : IItemSink
    {
        public List<LootDrop> Offered { get; } = [];

        public int Offer(ItemId item, int count)
        {
            Offered.Add(new LootDrop(item, count));
            return count;
        }
    }

    [Fact]
    public void ChooseTable_IsDeterministicForTheSameInputs()
    {
        Assert.True(Service.TryChooseTable("test:area_type/kitchen", "fridge", 0, 42, out var first) is null);
        Assert.True(Service.TryChooseTable("test:area_type/kitchen", "fridge", 0, 42, out var second) is null);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ChooseTable_OnlyEverPicksTablesTheRuleNames()
    {
        var seen = new HashSet<string>();
        for (ulong seed = 0; seed < 200; seed++)
        {
            Assert.True(Service.TryChooseTable("test:area_type/kitchen", "fridge", 0, seed, out var table) is null);
            seen.Add(table);
        }

        Assert.Equal(["test:loot/kitchen", "test:loot/medical"], seen.Order());
    }

    [Fact]
    public void DangerLevel_ShiftsTheWeights()
    {
        var low = Count("test:area_type/garage", "shelf", 0, "test:loot/military");
        var high = Count("test:area_type/garage", "shelf", 10, "test:loot/military");

        Assert.True(high > low, $"expected danger to favour the military table, got {low} then {high}");
    }

    [Fact]
    public void DangerLevel_CanRuleOutATableEntirely()
    {
        Assert.True(Service.TryChooseTable("test:area_type/empty", "shelf", 0, 1, out var table) is null);
        Assert.Equal("test:loot/kitchen", table);
        Assert.Equal(AreaLootError.NoTableAvailable, Service.TryChooseTable("test:area_type/empty", "shelf", 1, 1, out _));
    }

    [Fact]
    public void MissingRule_IsReportedRatherThanIgnored()
    {
        Assert.Equal(AreaLootError.UnknownAreaType, Service.TryChooseTable("test:area_type/missing", "cabinet", 0, 1, out _));
        Assert.Equal(AreaLootError.NoRuleForContainerKind, Service.TryChooseTable("test:area_type/kitchen", "shelf", 0, 1, out _));
    }

    [Fact]
    public void Fill_WithNoRule_GetsNothingAndReportsWhy()
    {
        var sink = Sink();

        var result = Service.Fill("test:area_type/kitchen", "shelf", 0, 1, sink);

        Assert.False(result.IsSuccess);
        Assert.Equal(AreaLootError.NoRuleForContainerKind, result.Error);
        Assert.Empty(sink.Offered);
    }

    [Fact]
    public void Fill_IsDeterministicForTheSameInputs()
    {
        var first = Sink();
        var second = Sink();

        var a = Service.Fill("test:area_type/kitchen", "fridge", 3, 7, first);
        var b = Service.Fill("test:area_type/kitchen", "fridge", 3, 7, second);

        Assert.Equal(a.Table, b.Table);
        Assert.Equal(first.Offered, second.Offered);
        Assert.NotEmpty(first.Offered);
    }

    [Fact]
    public void Fill_ReportsTheChosenTableAndWhatItPlaced()
    {
        var sink = Sink();

        var result = Service.Fill("test:area_type/kitchen", "cabinet", 0, 5, sink);

        Assert.True(result.IsSuccess);
        Assert.Equal("test:loot/kitchen", result.Table);
        Assert.Equal([new LootDrop(Beans, 1)], result.Fill!.Placed);
        Assert.Equal(result.Fill.Placed, sink.Offered);
    }

    [Fact]
    public void Fill_ReportsAnUnknownTable()
    {
        var areas = new AreaTypeCatalog([AreaTypeJson.Parse("""{ "id": "test:area_type/x", "rules": [ { "containerKind": "shelf", "tables": [ { "table": "test:loot/missing", "weight": 1 } ] } ] }""")]);
        var service = new AreaLootService(areas, new LootService(new LootTableCatalog([Kitchen])));

        Assert.Equal(AreaLootError.UnknownTable, service.Fill("test:area_type/x", "shelf", 0, 1, Sink()).Error);
    }

    [Fact]
    public void Json_ParsesADefinition()
    {
        var definition = AreaTypeJson.Parse("""{ "id": "m:area_type/x", "rules": [ { "containerKind": "shelf", "tables": [ { "table": "m:loot/a", "weight": 3, "dangerWeightShift": -1 } ] } ] }""");

        var rule = Assert.Single(definition.Rules);
        Assert.Equal("shelf", rule.ContainerKind);
        var table = Assert.Single(rule.Tables);
        Assert.Equal(("m:loot/a", 3, -1), (table.Table, table.Weight, table.DangerWeightShift));
        Assert.Equal(2, table.EffectiveWeight(1));
        Assert.Equal(0, table.EffectiveWeight(3));
    }

    [Theory]
    [InlineData("""{ "id": "m:area_type/x" }""")]
    [InlineData("""{ "id": "Bad", "rules": [] }""")]
    [InlineData("""{ "id": "m:area_type/x", "rules": [ { "containerKind": "Bad Kind", "tables": [ { "table": "m:loot/a" } ] } ] }""")]
    [InlineData("""{ "id": "m:area_type/x", "rules": [ { "containerKind": "shelf", "tables": [] } ] }""")]
    [InlineData("""{ "id": "m:area_type/x", "rules": [ { "containerKind": "shelf", "tables": [ { "table": "nope" } ] } ] }""")]
    [InlineData("""{ "id": "m:area_type/x", "rules": [ { "containerKind": "shelf", "tables": [ { "table": "m:loot/a", "weight": 0 } ] } ] }""")]
    [InlineData("""{ "id": "m:area_type/x", "rules": [ { "containerKind": "shelf", "tables": [ { "table": "m:loot/a" } ] }, { "containerKind": "shelf", "tables": [ { "table": "m:loot/b" } ] } ] }""")]
    [InlineData("not json")]
    public void Json_RejectsInvalidDefinitions(string json)
    {
        Assert.Throws<AreaTypeDefinitionException>(() => AreaTypeJson.Parse(json));
    }

    [Fact]
    public void Catalog_RejectsDuplicates()
    {
        var definition = new AreaTypeDefinition("m:area_type/x", [new AreaLootRule("shelf", [new WeightedLootTable("m:loot/a", 1)])]);
        Assert.Throws<ArgumentException>(() => new AreaTypeCatalog([definition, definition]));
    }

    private static int Count(string areaType, string containerKind, int danger, string table)
    {
        var count = 0;
        for (ulong seed = 0; seed < 500; seed++)
        {
            if (Service.TryChooseTable(areaType, containerKind, danger, seed, out var chosen) is null && chosen == table)
            {
                count++;
            }
        }

        return count;
    }
}
