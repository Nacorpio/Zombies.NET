using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.Zombies;

namespace Zombies.Domain.Tests;

/// <summary>Consumables apply and cure Status effects through a Domain command, from what their data says.</summary>
public sealed class ConsumableTests
{
    private const string Infection = "test:status_effect/infection";
    private const string Painkiller = "test:status_effect/painkiller";
    private const string Poison = "test:status_effect/food_poisoning";

    private static readonly CreatureId Bob = new(1);

    private static readonly StatusEffectCatalog Catalog = new(
        new[]
        {
            """
            { "id": "test:status_effect/infection", "category": "ailment",
              "stages": [ { "name": "mild", "after": 0 }, { "name": "severe", "after": 3600 } ],
              "curedBy": { "items": [ "test:item/antibiotics" ] } }
            """,
            """{ "id": "test:status_effect/painkiller", "category": "buff", "duration": 300 }""",
            """{ "id": "test:status_effect/food_poisoning", "category": "ailment", "duration": 120 }""",
        }.Select(StatusEffectJson.Parse));

    private static ItemDefinition Item(string json) => ItemDefinitionJson.Parse(json);

    private static readonly ItemDefinition Antibiotics = Item("""{ "id": "test:item/antibiotics", "mass": "30 g", "volume": "120 ml", "edible": true }""");

    private static readonly ItemDefinition Pills = Item("""
        { "id": "test:item/painkillers", "mass": "20 g", "volume": "60 ml", "edible": true,
          "onConsume": [ { "effect": "test:status_effect/painkiller" } ] }
        """);

    private static readonly ItemDefinition Spoiled = Item("""
        { "id": "test:item/spoiled_food", "mass": "0.4 kg", "volume": "350 ml", "edible": true,
          "onConsume": [ { "effect": "test:status_effect/food_poisoning", "chance": 0.75 } ] }
        """);

    private static readonly ItemDefinition Beans = Item("""{ "id": "test:item/beans", "mass": "0.4 kg", "volume": "350 ml", "edible": true }""");

    private static readonly ItemDefinition Crowbar = Item("""{ "id": "test:item/crowbar", "mass": "2 kg", "volume": "1 l" }""");

    private static CreatureEffects Creature() => new(Bob, Catalog);

    private static bool Always(int basis) => true;

    private static bool Never(int basis) => false;

    [Fact]
    public void ConsumingAnItemThatAppliesAnEffect_AppliesIt()
    {
        var effects = Creature();

        var result = effects.Consume(Pills, Always);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Events, e => e is EffectApplied { Effect: Painkiller });
        Assert.True(effects.Has(Painkiller));
    }

    [Fact]
    public void TheChanceInTheItemsData_DecidesWhetherItsEffectApplies()
    {
        var applied = Creature();
        var spared = Creature();
        var asked = new List<int>();

        applied.Consume(Spoiled, basis =>
        {
            asked.Add(basis);
            return true;
        });
        spared.Consume(Spoiled, Never);

        Assert.Equal([7500], asked);
        Assert.True(applied.Has(Poison));
        Assert.False(spared.Has(Poison));
    }

    [Fact]
    public void ConsumingAMedicine_CuresTheEffectItCures_AndLeavesTheOthers()
    {
        var effects = Creature();
        effects.Apply(Infection);
        effects.Apply(Painkiller);

        var result = effects.Consume(Antibiotics, Never);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Events, e => e is EffectCured { Effect: Infection, Cause: CureCause.Item });
        Assert.False(effects.Has(Infection));
        Assert.True(effects.Has(Painkiller));
    }

    [Fact]
    public void AMedicineWithNothingToCure_IsRefusedSoItIsNotWasted()
    {
        var effects = Creature();

        var result = effects.Consume(Antibiotics, Always);

        Assert.Equal(EffectError.NothingToCure, result.Error);
        Assert.Equal(0, effects.Count);
    }

    [Fact]
    public void PlainFood_IsConsumedWithoutAnyEffect()
    {
        var result = Creature().Consume(Beans, Always);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void AnItemThatIsNeitherEdibleNorDrinkable_CannotBeConsumed()
    {
        var result = Creature().Consume(Crowbar, Always);

        Assert.Equal(EffectError.NotConsumable, result.Error);
    }

    [Fact]
    public void AnItemNamingAnUnknownEffect_ChangesNothing()
    {
        var effects = Creature();
        effects.Apply(Infection);
        var strange = Item("""
            { "id": "test:item/strange", "mass": "10 g", "volume": "10 ml", "edible": true,
              "onConsume": [ { "effect": "test:status_effect/nonexistent" } ] }
            """);

        var result = effects.Consume(strange, Always);

        Assert.Equal(EffectError.UnknownEffect, result.Error);
        Assert.True(effects.Has(Infection));
    }

    [Fact]
    public void ItemData_ParsesTheEffectsAConsumableApplies()
    {
        var effect = Assert.Single(Spoiled.OnConsume);

        Assert.Equal("test:status_effect/food_poisoning", effect.Effect);
        Assert.Equal(7500, effect.ChanceBasis);
        Assert.Equal(ConsumeEffect.BasisPoints, Assert.Single(Pills.OnConsume).ChanceBasis);
    }

    [Theory]
    [InlineData("""{ "id": "test:item/x", "mass": "1 g", "volume": "1 ml", "onConsume": [ { "effect": "test:status_effect/painkiller" } ] }""")]
    [InlineData("""{ "id": "test:item/x", "mass": "1 g", "volume": "1 ml", "edible": true, "onConsume": [ { "effect": "test:status_effect/painkiller", "chance": 1.5 } ] }""")]
    [InlineData("""{ "id": "test:item/x", "mass": "1 g", "volume": "1 ml", "edible": true, "onConsume": [ { "effect": "painkiller" } ] }""")]
    public void ItemData_RejectsEffectsThatCannotWork(string json)
    {
        Assert.Throws<ItemDefinitionException>(() => ItemDefinitionJson.Parse(json));
    }

    [Fact]
    public void ZombieTypeData_ParsesTheChanceOfItsBiteCausingAnEffect()
    {
        var zombie = ZombieTypeJson.Parse("""
            {
              "id": "test:zombie/biter",
              "stats": { "partHealth": 40, "damage": 12, "speed": 1.4 },
              "senses": { "sight": 20, "hearing": 30 },
              "appearance": { "skinTones": ["#8fa07a"] },
              "bite": { "effect": "test:status_effect/infection", "chance": 0.3 }
            }
            """);

        Assert.Equal(new ZombieBite(Infection, 3000), zombie.Bite);
    }

    [Fact]
    public void ZombieTypeData_RejectsABiteChanceOutOfRange()
    {
        Assert.Throws<ZombieTypeDefinitionException>(() => ZombieTypeJson.Parse("""
            {
              "id": "test:zombie/biter",
              "stats": { "partHealth": 40, "damage": 12, "speed": 1.4 },
              "senses": { "sight": 20, "hearing": 30 },
              "appearance": { "skinTones": ["#8fa07a"] },
              "bite": { "effect": "test:status_effect/infection", "chance": 2 }
            }
            """));
    }
}
