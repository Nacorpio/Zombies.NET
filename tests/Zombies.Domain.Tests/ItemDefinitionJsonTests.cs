using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class ItemDefinitionJsonTests
{
    [Fact]
    public void Parse_ReadsUnitStrings()
    {
        var definition = ItemDefinitionJson.Parse("""
            { "id": "base:item/canned_beans", "mass": "0.4 kg", "volume": "350 ml", "maxStack": 8 }
            """);

        Assert.Equal(new ItemId("base:item/canned_beans"), definition.Id);
        Assert.Equal(0.4, definition.UnitMass.Kilograms, 9);
        Assert.Equal(0.35, definition.UnitVolume.Liters, 9);
        Assert.Equal(8, definition.MaxStack);
    }

    [Fact]
    public void Parse_AcceptsGramsAndDefaultsMaxStackToOne()
    {
        var definition = ItemDefinitionJson.Parse("""
            { "id": "base:item/bandage", "mass": "25 g", "volume": "0.1 l" }
            """);

        Assert.Equal(25, definition.UnitMass.Grams, 9);
        Assert.Equal(1, definition.MaxStack);
    }

    [Theory]
    [InlineData("""{ "id": "Bad Id", "mass": "1 kg", "volume": "1 l" }""")]
    [InlineData("""{ "id": "base:item/x", "mass": "heavy", "volume": "1 l" }""")]
    [InlineData("""{ "id": "base:item/x", "mass": "1 kg" }""")]
    [InlineData("""{ "id": "base:item/x", "mass": "-1 kg", "volume": "1 l" }""")]
    [InlineData("""{ "id": "base:item/x", "mass": "1 kg", "volume": "1 l", "maxStack": 0 }""")]
    public void Parse_RejectsInvalidDefinitions(string json)
    {
        Assert.Throws<ItemDefinitionException>(() => ItemDefinitionJson.Parse(json));
    }
}
