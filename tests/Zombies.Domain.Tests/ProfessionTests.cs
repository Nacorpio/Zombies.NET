using Zombies.Domain.Items;
using Zombies.Domain.Survival;

namespace Zombies.Domain.Tests;

public sealed class ProfessionTests
{
    private const string Nurse = """
        {
          "id": "test:profession/nurse",
          "items": [ { "item": "test:item/bandage", "count": 4 }, { "item": "test:item/pills" } ],
          "loot": [ "test:loot/medical" ],
          "outfit": [ "test:item/scrubs" ],
          "modifiers": [ { "stat": "treatment_speed", "operation": "multiply", "value": 1.5 } ]
        }
        """;

    [Fact]
    public void Parse_ReadsItemsLootOutfitAndModifiers()
    {
        var nurse = ProfessionJson.Parse(Nurse);

        Assert.Equal("test:profession/nurse", nurse.Id);
        Assert.Equal([new StartingItem(new ItemId("test:item/bandage"), 4), new StartingItem(new ItemId("test:item/pills"), 1)], nurse.Items);
        Assert.Equal(["test:loot/medical"], nurse.LootTables);
        Assert.Equal([new ItemId("test:item/scrubs")], nurse.Outfit);
        var modifier = Assert.Single(nurse.Modifiers).From(new ModifierSource("test:profession/nurse"));
        Assert.Equal((new StatName("treatment_speed"), ModifierOperation.Multiply, 1.5), (modifier.Stat, modifier.Operation, modifier.Value));
    }

    [Fact]
    public void Parse_AProfessionWithOnlyAnIdStartsEmptyHanded()
    {
        var profession = ProfessionJson.Parse("""{ "id": "test:profession/drifter" }""");

        Assert.Empty(profession.Items);
        Assert.Empty(profession.LootTables);
        Assert.Empty(profession.Outfit);
        Assert.Empty(profession.Modifiers);
    }

    [Theory]
    [InlineData("""{ "id": "test:item/nurse" }""")]
    [InlineData("""{ "id": "nurse" }""")]
    [InlineData("""{ "id": "test:profession/a", "items": [ { "item": "Bandage" } ] }""")]
    [InlineData("""{ "id": "test:profession/a", "items": [ { "item": "test:item/bandage", "count": 0 } ] }""")]
    [InlineData("""{ "id": "test:profession/a", "outfit": [ "test:item/scrubs", "test:item/scrubs" ] }""")]
    [InlineData("""{ "id": "test:profession/a", "loot": [ "medical" ] }""")]
    [InlineData("""{ "id": "test:profession/a", "modifiers": [ { "stat": "Speed", "operation": "add", "value": 1 } ] }""")]
    [InlineData("""{ "id": "test:profession/a", "modifiers": [ { "stat": "speed", "operation": "square", "value": 1 } ] }""")]
    [InlineData("""{ "id": "test:profession/a", "unknown": 1, "items": 3 }""")]
    [InlineData("[]")]
    public void Parse_RejectsADefinitionThatIsNotValid(string json) =>
        Assert.Throws<ProfessionException>(() => ProfessionJson.Parse(json));

    [Fact]
    public void Catalog_ListsProfessionsByContentId_AndRefusesADuplicate()
    {
        var nurse = ProfessionJson.Parse(Nurse);
        var cook = ProfessionJson.Parse("""{ "id": "test:profession/cook" }""");

        var catalog = new ProfessionCatalog([nurse, cook]);

        Assert.Equal(["test:profession/cook", "test:profession/nurse"], catalog.All.Select(p => p.Id));
        Assert.True(catalog.TryGet("test:profession/nurse", out var found));
        Assert.Same(nurse, found);
        Assert.False(catalog.TryGet("test:profession/pilot", out _));
        Assert.Throws<ArgumentException>(() => new ProfessionCatalog([nurse, nurse]));
    }
}

public sealed class ScenarioTests
{
    [Fact]
    public void Parse_ReadsStartLocationTimeOfDayAndStartingCondition()
    {
        var scenario = ScenarioJson.Parse("""
            {
              "id": "test:scenario/stranded",
              "startLocation": "wilderness",
              "timeOfDay": 0.75,
              "startingCondition": {
                "satiety": 0.5,
                "hydration": 0.25,
                "wounds": [ { "part": "leftLeg", "damageType": "cut", "damage": 6 } ]
              }
            }
            """);

        Assert.Equal("test:scenario/stranded", scenario.Id);
        Assert.Equal(StartLocationKind.Wilderness, scenario.StartLocation);
        Assert.Equal(0.75, scenario.TimeOfDay);
        Assert.Equal((0.5, 0.25), (scenario.Condition.Satiety, scenario.Condition.Hydration));
        Assert.Equal([new StartingWound(BodyPart.LeftLeg, DamageType.Cut, 6)], scenario.Condition.Wounds);
    }

    [Fact]
    public void Parse_ALeftOutStartingConditionIsAHealthyOne()
    {
        var scenario = ScenarioJson.Parse("""{ "id": "test:scenario/calm", "startLocation": "settlement", "timeOfDay": 0.5 }""");

        Assert.Equal(StartLocationKind.Settlement, scenario.StartLocation);
        Assert.Equal((1, 1), (scenario.Condition.Satiety, scenario.Condition.Hydration));
        Assert.Empty(scenario.Condition.Wounds);
    }

    [Theory]
    [InlineData("""{ "id": "test:profession/a", "startLocation": "settlement", "timeOfDay": 0.5 }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "moon", "timeOfDay": 0.5 }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": 0, "timeOfDay": 0.5 }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "settlement" }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "settlement", "timeOfDay": 1 }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "settlement", "timeOfDay": -0.1 }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "settlement", "timeOfDay": 0.5, "startingCondition": { "satiety": 1.5 } }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "settlement", "timeOfDay": 0.5, "startingCondition": { "hydration": -1 } }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "settlement", "timeOfDay": 0.5, "startingCondition": { "wounds": [ { "part": "tail", "damageType": "cut", "damage": 1 } ] } }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "settlement", "timeOfDay": 0.5, "startingCondition": { "wounds": [ { "part": "head", "damageType": "fire", "damage": 1 } ] } }""")]
    [InlineData("""{ "id": "test:scenario/a", "startLocation": "settlement", "timeOfDay": 0.5, "startingCondition": { "wounds": [ { "part": "head", "damageType": "cut", "damage": 0 } ] } }""")]
    public void Parse_RejectsADefinitionThatIsNotValid(string json) =>
        Assert.Throws<ScenarioException>(() => ScenarioJson.Parse(json));

    [Fact]
    public void Catalog_ListsScenariosByContentId_AndRefusesADuplicate()
    {
        var calm = ScenarioJson.Parse("""{ "id": "test:scenario/calm", "startLocation": "settlement", "timeOfDay": 0.5 }""");
        var night = ScenarioJson.Parse("""{ "id": "test:scenario/night", "startLocation": "wilderness", "timeOfDay": 0 }""");

        var catalog = new ScenarioCatalog([night, calm]);

        Assert.Equal(["test:scenario/calm", "test:scenario/night"], catalog.All.Select(s => s.Id));
        Assert.True(catalog.TryGet("test:scenario/night", out var found));
        Assert.Same(night, found);
        Assert.Throws<ArgumentException>(() => new ScenarioCatalog([calm, calm]));
    }
}

public sealed class WearableJsonTests
{
    [Fact]
    public void Parse_ReadsLayerCoverageInsulationProtectionAndEncumbrance()
    {
        var jacket = WearableJson.Parse("""
            {
              "id": "test:wearable/jacket",
              "item": "test:item/jacket",
              "layer": "outer",
              "coverage": [ "torso", "leftArm" ],
              "insulation": 0.15,
              "protection": { "cut": 0.2 },
              "encumbrance": 0.05
            }
            """);

        Assert.Equal(new ItemId("test:item/jacket"), jacket.Item);
        Assert.Equal(ClothingLayer.Outer, jacket.Layer);
        Assert.Equal([BodyPart.Torso, BodyPart.LeftArm], jacket.Coverage.Order());
        Assert.Equal(0.15, jacket.Insulation.SquareMeterKelvinsPerWatt, 9);
        Assert.Equal(0.2, jacket.Protection[DamageType.Cut]);
        Assert.Equal(0.05, jacket.Encumbrance);
    }

    [Theory]
    [InlineData("""{ "id": "test:item/jacket", "item": "test:item/jacket", "layer": "outer", "coverage": [ "torso" ] }""")]
    [InlineData("""{ "id": "test:wearable/jacket", "item": "Jacket", "layer": "outer", "coverage": [ "torso" ] }""")]
    [InlineData("""{ "id": "test:wearable/jacket", "item": "test:item/jacket", "layer": "cloak", "coverage": [ "torso" ] }""")]
    [InlineData("""{ "id": "test:wearable/jacket", "item": "test:item/jacket", "layer": "outer", "coverage": [ "tail" ] }""")]
    [InlineData("""{ "id": "test:wearable/jacket", "item": "test:item/jacket", "layer": "outer", "coverage": [] }""")]
    [InlineData("""{ "id": "test:wearable/jacket", "item": "test:item/jacket", "layer": "outer", "coverage": [ "torso" ], "protection": { "fire": 0.1 } }""")]
    [InlineData("""{ "id": "test:wearable/jacket", "item": "test:item/jacket", "layer": "outer", "coverage": [ "torso" ], "protection": { "cut": 1.5 } }""")]
    public void Parse_RejectsADefinitionThatIsNotValid(string json) =>
        Assert.Throws<WearableDefinitionException>(() => WearableJson.Parse(json));
}
