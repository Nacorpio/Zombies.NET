using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;

namespace Zombies.Domain.Tests;

public sealed class SubstanceTests
{
    private const string Stimulated = "test:status_effect/stimulated";
    private const string Withdrawal = "test:status_effect/stimulant_withdrawal";
    private const string Drunk = "test:status_effect/drunk";
    private const string Stimulant = "test:substance/stimulant";
    private const string Alcohol = "test:substance/alcohol";

    private static readonly CreatureId Bob = new(1);
    private static readonly ItemId Pills = new("test:item/pills");
    private static readonly ItemId Beer = new("test:item/beer");
    private static readonly ItemId Rock = new("test:item/rock");
    private static readonly StatName MoveSpeed = new("move_speed");
    private static readonly StatName AimSpread = new("aim_spread");

    private static readonly StatusEffectCatalog Effects = new(
        new[]
        {
            """{ "id": "test:status_effect/stimulated", "category": "buff", "duration": 600, "stacking": "stack", "maxStacks": 3, "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 1.1 } ] }""",
            """
            { "id": "test:status_effect/stimulant_withdrawal", "category": "ailment",
              "stages": [
                { "name": "craving", "after": 0, "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 0.9 } ] },
                { "name": "aching", "after": 100, "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 0.8 } ] },
                { "name": "severe", "after": 300, "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 0.6 } ] }
              ] }
            """,
            """
            { "id": "test:status_effect/drunk", "category": "ailment", "duration": 1000, "stacking": "stack", "maxStacks": 5,
              "stages": [
                { "name": "drunk", "after": 0, "modifiers": [ { "stat": "aim_spread", "operation": "add", "value": 3 } ] },
                { "name": "tipsy", "after": 400, "modifiers": [ { "stat": "aim_spread", "operation": "add", "value": 1 } ] }
              ] }
            """,
        }.Select(StatusEffectJson.Parse));

    private static readonly SubstanceCatalog Catalog = new(
        new[]
        {
            """
            { "id": "test:substance/stimulant", "items": [ { "item": "test:item/pills", "doses": 2 } ], "statusEffect": "test:status_effect/stimulated",
              "addictionChance": 0.4, "toleranceGrowth": 0.25, "withdrawal": { "statusEffect": "test:status_effect/stimulant_withdrawal", "delay": 1000 } }
            """,
            """{ "id": "test:substance/alcohol", "items": [ { "item": "test:item/beer" } ], "statusEffect": "test:status_effect/drunk", "toleranceGrowth": 0.02 }""",
        }.Select(SubstanceJson.Parse),
        Effects);

    private readonly CreatureEffects _effects = new(Bob, Effects);
    private readonly CreatureSubstances _substances;

    public SubstanceTests() => _substances = new CreatureSubstances(Catalog, _effects);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private ActiveEffect Active(string effect) => _effects.Effects.Single(e => e.Effect == effect);

    [Fact]
    public void Consume_RejectsAnItemThatIsNoSubstance_AndARollOutOfRange()
    {
        Assert.Equal(EffectError.NotASubstance, _substances.Consume(Rock, 0.5).Error);
        Assert.Equal(EffectError.InvalidRoll, _substances.Consume(Pills, 1).Error);
        Assert.Equal(EffectError.InvalidRoll, _substances.Consume(Pills, -0.1).Error);
        Assert.Equal(EffectError.InvalidRoll, _substances.Consume(Pills, double.NaN).Error);
        Assert.Empty(_effects.Effects);
        Assert.Equal(0, _substances.Tolerance(Stimulant));
    }

    [Fact]
    public void Consume_AppliesTheEffectOncePerDose_AndRaisesWhatHappened()
    {
        var result = _substances.Consume(Pills, 0.99);

        Assert.Equal(2, Active(Stimulated).Stacks);
        Assert.Contains(new EffectStacked(Bob, Stimulated, 2), result.Events);
        Assert.Contains(new SubstanceConsumed(Bob, Stimulant, 2, 0.5), result.Events);
    }

    [Fact]
    public void AlcoholImpairment_ScalesWithTheAmountConsumed()
    {
        _substances.Consume(Beer, 0.5);
        var oneDrink = _effects.EffectiveValue(AimSpread, 0);

        _substances.Consume(Beer, 0.5);
        _substances.Consume(Beer, 0.5);
        var threeDrinks = _effects.EffectiveValue(AimSpread, 0);

        Assert.Equal(3, oneDrink);
        Assert.Equal(9, threeDrinks);
    }

    [Fact]
    public void AlcoholImpairment_FadesWithTimeAndEndsWhenItHasWornOff()
    {
        _substances.Consume(Beer, 0.5);
        _substances.Consume(Beer, 0.5);
        Assert.Equal(6, _effects.EffectiveValue(AimSpread, 0));

        _substances.Advance(S(400));
        Assert.Equal(2, _effects.EffectiveValue(AimSpread, 0));

        var end = _substances.Advance(S(600));

        Assert.Contains(new EffectExpired(Bob, Drunk), end.Events);
        Assert.Equal(0, _effects.EffectiveValue(AimSpread, 0));
    }

    [Fact]
    public void Tolerance_GrowsWithEveryDose_AndStopsAtOne()
    {
        _substances.Consume(Pills, 0.99);
        Assert.Equal(0.5, _substances.Tolerance(Stimulant), 9);

        _substances.Consume(Pills, 0.99);
        _substances.Consume(Pills, 0.99);
        Assert.Equal(1, _substances.Tolerance(Stimulant), 9);
        Assert.Equal(0, _substances.Tolerance(Alcohol));
    }

    [Fact]
    public void Tolerance_CancelsDosesSoTheSameItemDoesLess()
    {
        _substances.Consume(Pills, 0.99);
        _effects.Remove(Stimulated);
        _substances.Consume(Pills, 0.99);
        Assert.Equal(1, Active(Stimulated).Stacks);

        _effects.Remove(Stimulated);
        var spent = _substances.Consume(Pills, 0.99);

        Assert.False(_effects.Has(Stimulated));
        Assert.Contains(new SubstanceConsumed(Bob, Stimulant, 2, 1), spent.Events);
    }

    [Fact]
    public void Tolerance_IsRecordedAsAModifierWithItsSource()
    {
        _substances.Consume(Pills, 0.99);

        var modifier = Assert.Single(_substances.Modifiers);

        Assert.Equal(new Modifier(new StatName("stimulant_tolerance"), ModifierOperation.Add, 0.5, new ModifierSource($"tolerance:{Stimulant}")), modifier);
    }

    [Fact]
    public void Addiction_DevelopsWhenTheRollIsUnderTheChance_AndToleranceRaisesTheChance()
    {
        Assert.DoesNotContain(_substances.Consume(Pills, 0.45).Events, e => e is AddictionDeveloped);
        Assert.False(_substances.IsAddicted(Stimulant));

        var second = _substances.Consume(Pills, 0.45);

        Assert.Contains(new AddictionDeveloped(Bob, Stimulant), second.Events);
        Assert.True(_substances.IsAddicted(Stimulant));
        Assert.DoesNotContain(_substances.Consume(Pills, 0).Events, e => e is AddictionDeveloped);
    }

    [Fact]
    public void ASubstanceWithNoAddictionChance_NeverAddicts()
    {
        _substances.Consume(Beer, 0);

        Assert.False(_substances.IsAddicted(Alcohol));
    }

    [Fact]
    public void Withdrawal_BeginsAfterTheDelay_AndProgressesThroughItsStages()
    {
        _substances.Consume(Pills, 0);

        _substances.Advance(S(999));
        Assert.False(_effects.Has(Withdrawal));

        var begin = _substances.Advance(S(1));
        Assert.Contains(new EffectApplied(Bob, Withdrawal, 1), begin.Events);
        Assert.Equal("craving", Active(Withdrawal).Stage);

        _substances.Advance(S(100));
        Assert.Equal("aching", Active(Withdrawal).Stage);

        _substances.Advance(S(200));
        Assert.Equal("severe", Active(Withdrawal).Stage);
        Assert.Equal(0.6, _effects.EffectiveValue(MoveSpeed, 1), 9);
    }

    [Fact]
    public void Withdrawal_CountsTimeAfterTheDelayWhenOneStepCrossesIt()
    {
        _substances.Consume(Pills, 0);

        _substances.Advance(S(1150));

        Assert.Equal(S(150), Active(Withdrawal).Elapsed);
        Assert.Equal("aching", Active(Withdrawal).Stage);
    }

    [Fact]
    public void Withdrawal_NeverStartsForSomeoneWhoIsNotAddicted()
    {
        _substances.Consume(Pills, 0.99);

        _substances.Advance(S(100000));

        Assert.False(_effects.Has(Withdrawal));
    }

    [Fact]
    public void UsingTheSubstanceAgain_EasesWithdrawalOneStage_AndEndsItFromTheFirst()
    {
        _substances.Consume(Pills, 0);
        _substances.Advance(S(1000 + 350));
        Assert.Equal("severe", Active(Withdrawal).Stage);

        var eased = _substances.Consume(Pills, 0.99);

        Assert.Contains(new EffectEased(Bob, Withdrawal, "aching"), eased.Events);
        Assert.Equal("aching", Active(Withdrawal).Stage);

        var again = _substances.Consume(Pills, 0.99);
        Assert.Contains(new EffectEased(Bob, Withdrawal, "craving"), again.Events);

        var last = _substances.Consume(Pills, 0.99);

        Assert.Contains(new EffectEased(Bob, Withdrawal, null), last.Events);
        Assert.False(_effects.Has(Withdrawal));
    }

    [Fact]
    public void AfterUse_TheDelayStartsOver_SoWithdrawalComesBackLater()
    {
        _substances.Consume(Pills, 0);
        _substances.Advance(S(1000));
        _substances.Consume(Pills, 0.99);
        Assert.False(_effects.Has(Withdrawal));

        _substances.Advance(S(999));
        Assert.False(_effects.Has(Withdrawal));

        _substances.Advance(S(1));
        Assert.Equal("craving", Active(Withdrawal).Stage);
    }

    [Fact]
    public void Snapshot_RestoresToleranceAddictionAndTimeSinceLastUse()
    {
        _substances.Consume(Pills, 0);
        _substances.Consume(Beer, 0.5);
        _substances.Advance(S(700));

        var restored = CreatureSubstances.Restore(Catalog, new CreatureEffects(Bob, Effects), _substances.ToSnapshot());

        Assert.Equal(_substances.ToSnapshot(), restored.ToSnapshot());
        Assert.True(restored.IsAddicted(Stimulant));
        Assert.Equal(0.5, restored.Tolerance(Stimulant), 9);
        Assert.Equal(0.02, restored.Tolerance(Alcohol), 9);
    }

    [Fact]
    public void ARestoredAddict_PastTheDelay_GetsWithdrawalOnTheNextAdvance()
    {
        var effects = new CreatureEffects(Bob, Effects);
        var restored = CreatureSubstances.Restore(Catalog, effects, [new SubstanceUseSnapshot(Stimulant, 0.25, true, S(5000))]);

        restored.Advance(S(150));

        Assert.True(effects.Has(Withdrawal));
    }

    [Fact]
    public void Restore_RejectsUnknownSubstancesAndImpossibleValues()
    {
        Assert.Throws<ArgumentException>(() => CreatureSubstances.Restore(Catalog, _effects, [new SubstanceUseSnapshot("test:substance/nope", 0, false, S(0))]));
        Assert.Throws<ArgumentException>(() => CreatureSubstances.Restore(Catalog, _effects, [new SubstanceUseSnapshot(Stimulant, 1.5, false, S(0))]));
        Assert.Throws<ArgumentException>(() => CreatureSubstances.Restore(Catalog, _effects, [new SubstanceUseSnapshot(Stimulant, 0.5, false, S(-1))]));
        Assert.Throws<ArgumentException>(() => CreatureSubstances.Restore(Catalog, _effects, [new SubstanceUseSnapshot(Stimulant, 0.5, false, S(0)), new SubstanceUseSnapshot(Stimulant, 0.5, false, S(0))]));
    }

    [Fact]
    public void Advance_RejectsANegativeStep()
    {
        Assert.Equal(EffectError.InvalidDuration, _substances.Advance(S(-1)).Error);
    }

    [Fact]
    public void Json_RejectsInvalidDefinitions()
    {
        const string Prefix = """{ "id": "test:substance/x", "statusEffect": "test:status_effect/stimulated", """;
        Assert.Throws<SubstanceDefinitionException>(() => SubstanceJson.Parse(Prefix + """ "items": [] }"""));
        Assert.Throws<SubstanceDefinitionException>(() => SubstanceJson.Parse(Prefix + """ "items": [ { "item": "test:item/x", "doses": 0 } ] }"""));
        Assert.Throws<SubstanceDefinitionException>(() => SubstanceJson.Parse(Prefix + """ "items": [ { "item": "test:item/x" }, { "item": "test:item/x" } ] }"""));
        Assert.Throws<SubstanceDefinitionException>(() => SubstanceJson.Parse(Prefix + """ "items": [ { "item": "test:item/x" } ], "addictionChance": 1.5 }"""));
        Assert.Throws<SubstanceDefinitionException>(() => SubstanceJson.Parse(Prefix + """ "items": [ { "item": "test:item/x" } ], "toleranceGrowth": -0.1 }"""));
        Assert.Throws<SubstanceDefinitionException>(() => SubstanceJson.Parse(Prefix + """ "items": [ { "item": "test:item/x" } ], "addictionChance": 0.1 }"""));
        Assert.Throws<SubstanceDefinitionException>(() => SubstanceJson.Parse(Prefix + """ "items": [ { "item": "test:item/x" } ], "addictionChance": 0.1, "withdrawal": { "statusEffect": "test:status_effect/stimulant_withdrawal", "delay": 0 } }"""));
        Assert.Throws<SubstanceDefinitionException>(() => SubstanceJson.Parse("not json"));
    }

    [Fact]
    public void Catalog_RejectsDuplicatesSharedItemsAndUnknownEffects()
    {
        var alcohol = SubstanceJson.Parse("""{ "id": "test:substance/alcohol", "items": [ { "item": "test:item/beer" } ], "statusEffect": "test:status_effect/drunk" }""");
        var sameItem = SubstanceJson.Parse("""{ "id": "test:substance/booze", "items": [ { "item": "test:item/beer" } ], "statusEffect": "test:status_effect/drunk" }""");
        var unknownEffect = SubstanceJson.Parse("""{ "id": "test:substance/ghost", "items": [ { "item": "test:item/ghost" } ], "statusEffect": "test:status_effect/nope" }""");
        var unknownWithdrawal = SubstanceJson.Parse("""{ "id": "test:substance/ghost", "items": [ { "item": "test:item/ghost" } ], "statusEffect": "test:status_effect/drunk", "addictionChance": 0.1, "withdrawal": { "statusEffect": "test:status_effect/nope", "delay": 10 } }""");

        Assert.Throws<ArgumentException>(() => new SubstanceCatalog([alcohol, alcohol], Effects));
        Assert.Throws<ArgumentException>(() => new SubstanceCatalog([alcohol, sameItem], Effects));
        Assert.Throws<ArgumentException>(() => new SubstanceCatalog([unknownEffect], Effects));
        Assert.Throws<ArgumentException>(() => new SubstanceCatalog([unknownWithdrawal], Effects));
    }
}
