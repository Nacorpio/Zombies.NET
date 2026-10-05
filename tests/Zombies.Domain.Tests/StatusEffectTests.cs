using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;

namespace Zombies.Domain.Tests;

public sealed class StatusEffectTests
{
    private const string Infection = "test:status_effect/infection";
    private const string Painkiller = "test:status_effect/painkiller";
    private const string Adrenaline = "test:status_effect/adrenaline";
    private const string Poison = "test:status_effect/poison";
    private const string Antidote = "test:status_effect/antidote";
    private const string Focus = "test:status_effect/focus";

    private static readonly CreatureId Bob = new(1);
    private static readonly ItemId Antibiotics = new("test:item/antibiotics");
    private static readonly StatName MoveSpeed = new("move_speed");

    private static readonly StatusEffectCatalog Catalog = new(
        new[]
        {
            """
            { "id": "test:status_effect/infection", "category": "ailment",
              "stages": [
                { "name": "mild", "after": 0, "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 0.9 } ] },
                { "name": "severe", "after": 3600, "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 0.6 } ],
                  "periodic": [ { "change": "infection_damage", "every": 600, "amount": 2 } ] }
              ],
              "curedBy": { "items": [ "test:item/antibiotics" ] } }
            """,
            """{ "id": "test:status_effect/painkiller", "category": "buff", "duration": 300, "stacking": "refresh", "modifiers": [ { "stat": "pain_tolerance", "operation": "add", "value": 20 } ] }""",
            """{ "id": "test:status_effect/adrenaline", "category": "buff", "duration": 60, "stacking": "extend" }""",
            """
            { "id": "test:status_effect/poison", "category": "ailment", "duration": 100, "stacking": "stack", "maxStacks": 3,
              "modifiers": [ { "stat": "move_speed", "operation": "add", "value": -1 } ],
              "periodic": [ { "change": "poison_damage", "every": 10, "amount": 1 } ],
              "curedBy": { "effects": [ "test:status_effect/antidote" ] } }
            """,
            """{ "id": "test:status_effect/antidote", "category": "buff", "duration": 10 }""",
            """{ "id": "test:status_effect/focus", "category": "buff", "duration": 50, "stacking": "ignore" }""",
        }.Select(StatusEffectJson.Parse));

    private readonly CreatureEffects _effects = new(Bob, Catalog);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private ActiveEffect Active(string effect) => _effects.Effects.Single(e => e.Effect == effect);

    [Fact]
    public void Apply_RaisesEventAndTracksTheEffect()
    {
        var result = _effects.Apply(Painkiller);

        Assert.Equal(new EffectApplied(Bob, Painkiller, 1), Assert.Single(result.Events));
        Assert.True(_effects.Has(Painkiller));
        Assert.Equal(S(300), Active(Painkiller).Remaining);
    }

    [Fact]
    public void Commands_ReturnErrorsInsteadOfThrowing()
    {
        Assert.Equal(EffectError.UnknownEffect, _effects.Apply("test:status_effect/missing").Error);
        Assert.Equal(EffectError.NotActive, _effects.Remove(Painkiller).Error);
        Assert.Equal(EffectError.NothingToCure, _effects.CureWithItem(Antibiotics).Error);
        Assert.Equal(EffectError.InvalidDuration, _effects.Advance(S(-1)).Error);
        Assert.Empty(_effects.Effects);
    }

    [Fact]
    public void Refresh_StartsTheDurationOver()
    {
        _effects.Apply(Painkiller);
        _effects.Advance(S(200));

        var result = _effects.Apply(Painkiller);

        Assert.Equal(new EffectRefreshed(Bob, Painkiller, S(300)), Assert.Single(result.Events));
        Assert.Equal(S(300), Active(Painkiller).Remaining);
        Assert.Single(_effects.Modifiers);
    }

    [Fact]
    public void Extend_AddsTheDurationToWhatRemains()
    {
        _effects.Apply(Adrenaline);
        _effects.Advance(S(20));

        var result = _effects.Apply(Adrenaline);

        Assert.Equal(new EffectExtended(Bob, Adrenaline, S(100)), Assert.Single(result.Events));
    }

    [Fact]
    public void Stack_AddsStacksUpToTheLimitThenRefreshes()
    {
        _effects.Apply(Poison);
        _effects.Advance(S(5));
        Assert.Equal(new EffectStacked(Bob, Poison, 2), Assert.Single(_effects.Apply(Poison).Events));
        Assert.Equal(new EffectStacked(Bob, Poison, 3), Assert.Single(_effects.Apply(Poison).Events));

        var atLimit = _effects.Apply(Poison);

        Assert.Equal(new EffectRefreshed(Bob, Poison, S(100)), Assert.Single(atLimit.Events));
        Assert.Equal(3, Active(Poison).Stacks);
    }

    [Fact]
    public void Ignore_LeavesAnActiveEffectAlone()
    {
        _effects.Apply(Focus);
        _effects.Advance(S(20));

        var result = _effects.Apply(Focus);

        Assert.Equal(new EffectIgnored(Bob, Focus), Assert.Single(result.Events));
        Assert.Equal(S(30), Active(Focus).Remaining);
    }

    [Fact]
    public void Stages_ProgressWithTime()
    {
        var applied = _effects.Apply(Infection);
        Assert.Equal(new EffectStageChanged(Bob, Infection, "mild"), applied.Events[1]);
        Assert.Equal("mild", Active(Infection).Stage);

        Assert.Empty(_effects.Advance(S(3599)).Events);

        var worse = _effects.Advance(S(1));
        Assert.Equal(new EffectStageChanged(Bob, Infection, "severe"), Assert.Single(worse.Events));
        Assert.Equal("severe", Active(Infection).Stage);
    }

    [Fact]
    public void Modifiers_FollowTheStageScaleWithStacksAndGoWhenTheEffectEnds()
    {
        _effects.Apply(Infection);
        Assert.Equal(90, _effects.EffectiveValue(MoveSpeed, 100), 6);

        _effects.Advance(S(3600));
        Assert.Equal(60, _effects.EffectiveValue(MoveSpeed, 100), 6);

        _effects.Apply(Poison);
        _effects.Apply(Poison);
        Assert.Equal(98 * 0.6, _effects.EffectiveValue(MoveSpeed, 100), 6);
        Assert.Contains(_effects.Modifiers, m => m.Source.Value == $"status_effect:{Poison}");
        Assert.Equal(2, _effects.Modifiers.Count(m => m.Source.Value == $"status_effect:{Poison}"));

        _effects.Remove(Poison);
        _effects.CureWithItem(Antibiotics);
        Assert.Empty(_effects.Modifiers);
        Assert.Equal(100, _effects.EffectiveValue(MoveSpeed, 100));
    }

    [Fact]
    public void Modifiers_DisappearWhenTheEffectExpires()
    {
        _effects.Apply(Painkiller);
        Assert.Single(_effects.Modifiers);

        var result = _effects.Advance(S(300));

        Assert.Equal(new EffectExpired(Bob, Painkiller), Assert.Single(result.Events));
        Assert.Empty(_effects.Effects);
        Assert.Empty(_effects.Modifiers);
    }

    [Fact]
    public void PeriodicChanges_AreEventsScaledByStacks()
    {
        _effects.Apply(Poison);
        _effects.Apply(Poison);

        var result = _effects.Advance(S(25));

        Assert.Equal(
            [new EffectPeriodicChange(Bob, Poison, new StatName("poison_damage"), 2), new EffectPeriodicChange(Bob, Poison, new StatName("poison_damage"), 2)],
            result.Events);
    }

    [Fact]
    public void StagePeriodicChanges_StartWhenTheStageStarts()
    {
        _effects.Apply(Infection);

        var result = _effects.Advance(S(3600 + 1250));

        var changes = result.Events.OfType<EffectPeriodicChange>().ToList();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal((new StatName("infection_damage"), 2d), (c.Change, c.Amount)));
    }

    [Fact]
    public void Advance_InOneBigStepMatchesManySmallSteps()
    {
        var other = new CreatureEffects(Bob, Catalog);
        foreach (var effects in new[] { _effects, other })
        {
            effects.Apply(Infection);
            effects.Apply(Poison);
            effects.Apply(Painkiller);
        }

        var big = _effects.Advance(S(5000)).Events;
        var small = Enumerable.Range(0, 5000).SelectMany(_ => other.Advance(S(1)).Events).ToList();

        Assert.Equal(
            big.Select(e => e.ToString()).Order(StringComparer.Ordinal),
            small.Select(e => e.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(_effects.Effects, other.Effects);
    }

    [Fact]
    public void Expiry_EndsAnEffectWithItsFinalPeriodicChange()
    {
        _effects.Apply(Poison);

        var result = _effects.Advance(S(100));

        Assert.Equal(10, result.Events.OfType<EffectPeriodicChange>().Count());
        Assert.Equal(new EffectExpired(Bob, Poison), result.Events[^1]);
    }

    [Fact]
    public void Cure_ByItemRemovesOnlyTheEffectsItCures()
    {
        _effects.Apply(Infection);
        _effects.Apply(Painkiller);

        var result = _effects.CureWithItem(Antibiotics);

        Assert.Equal(new EffectCured(Bob, Infection, CureCause.Item, Antibiotics.Value), Assert.Single(result.Events));
        Assert.False(_effects.Has(Infection));
        Assert.True(_effects.Has(Painkiller));
    }

    [Fact]
    public void Cure_ByApplyingAnotherEffect()
    {
        _effects.Apply(Poison);

        var result = _effects.Apply(Antidote);

        Assert.Equal(new EffectCured(Bob, Poison, CureCause.Effect, Antidote), result.Events[0]);
        Assert.Equal(new EffectApplied(Bob, Antidote, 1), result.Events[1]);
        Assert.False(_effects.Has(Poison));
        Assert.Empty(_effects.Modifiers);
    }

    [Fact]
    public void Remove_EndsTheEffectAndRaisesAnEvent()
    {
        _effects.Apply(Painkiller);

        Assert.Equal(new EffectRemoved(Bob, Painkiller), Assert.Single(_effects.Remove(Painkiller).Events));
        Assert.Empty(_effects.Effects);
    }

    [Fact]
    public void Creatures_HaveIndependentEffects()
    {
        var other = new CreatureEffects(new CreatureId(2), Catalog);

        _effects.Apply(Painkiller);

        Assert.False(other.Has(Painkiller));
        Assert.Empty(other.Modifiers);
    }

    [Fact]
    public void Json_ParsesAFullDefinition()
    {
        var definition = StatusEffectJson.Parse("""
            { "id": "m:status_effect/x", "category": "environmental", "duration": 12.5, "stacking": "stack", "maxStacks": 4,
              "stages": [ { "name": "one", "after": 0 }, { "name": "two", "after": 5, "periodic": [ { "change": "chill", "every": 1, "amount": 0.5 } ] } ],
              "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 0.5 } ],
              "periodic": [ { "change": "cold", "every": 2, "amount": 1 } ],
              "curedBy": { "items": [ "m:item/blanket" ], "effects": [] } }
            """);

        Assert.Equal((EffectCategory.Environmental, S(12.5), StackingRule.Stack, 4), (definition.Category, definition.Duration, definition.Stacking, definition.MaxStacks));
        Assert.Equal(["one", "two"], definition.Stages.Select(s => s.Name));
        Assert.Equal(S(2), Assert.Single(definition.Periodic).Every);
        Assert.Equal(new ItemId("m:item/blanket"), Assert.Single(definition.CuredByItems));
    }

    [Theory]
    [InlineData("""{ "id": "m:status_effect/x" }""")]
    [InlineData("""{ "id": "Bad", "category": "buff" }""")]
    [InlineData("""{ "id": "m:status_effect/x", "category": "curse" }""")]
    [InlineData("""{ "id": "m:status_effect/x", "category": "buff", "stacking": "pile" }""")]
    [InlineData("""{ "id": "m:status_effect/x", "category": "buff", "duration": 0 }""")]
    [InlineData("""{ "id": "m:status_effect/x", "category": "buff", "maxStacks": 0 }""")]
    [InlineData("""{ "id": "m:status_effect/x", "category": "buff", "periodic": [ { "change": "x", "every": 0, "amount": 1 } ] }""")]
    [InlineData("""{ "id": "m:status_effect/x", "category": "buff", "modifiers": [ { "stat": "Bad Stat", "operation": "add", "value": 1 } ] }""")]
    [InlineData("""{ "id": "m:status_effect/x", "category": "buff", "stages": [ { "name": "a", "after": 5 }, { "name": "b", "after": 5 } ] }""")]
    [InlineData("""{ "id": "m:status_effect/x", "category": "buff", "curedBy": { "items": [ "nope" ] } }""")]
    [InlineData("not json")]
    public void Json_RejectsInvalidDefinitions(string json)
    {
        Assert.Throws<StatusEffectDefinitionException>(() => StatusEffectJson.Parse(json));
    }

    [Fact]
    public void Catalog_RejectsDuplicatesAndUnknownCures()
    {
        var effect = new StatusEffectDefinition("m:status_effect/a", EffectCategory.Buff);
        Assert.Throws<ArgumentException>(() => new StatusEffectCatalog([effect, effect]));
        Assert.Throws<ArgumentException>(() => new StatusEffectCatalog([new StatusEffectDefinition("m:status_effect/b", EffectCategory.Buff, curedByEffects: ["m:status_effect/missing"])]));
    }
}
