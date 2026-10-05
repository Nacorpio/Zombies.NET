using Zombies.Domain.Items;
using Zombies.Domain.Survival;

namespace Zombies.Domain.Tests;

public sealed class MoraleTests
{
    private static readonly StatName AimSpread = new("aim_spread");
    private static readonly StatName TeammateDied = new("teammate_died");
    private static readonly StatName HotMeal = new("ate_hot_meal");
    private static readonly StatName GruesomeSight = new("gruesome_sight");
    private static readonly ItemId Chocolate = new("test:item/chocolate");

    private static readonly MoraleCatalog Catalog = new(
        new[]
        {
            """{ "id": "test:morale_source/teammate_death", "trigger": { "event": "teammate_died" }, "size": -30, "duration": 1000 }""",
            """{ "id": "test:morale_source/hot_meal", "trigger": { "event": "ate_hot_meal" }, "size": 20, "duration": 500 }""",
            """{ "id": "test:morale_source/gruesome_sight", "trigger": { "event": "gruesome_sight" }, "size": -10, "duration": 400, "maxStacks": 3 }""",
            """{ "id": "test:morale_source/comfort", "trigger": { "item": "test:item/chocolate" }, "size": 15, "duration": 300 }""",
            """{ "id": "test:morale_source/windfall", "trigger": { "event": "windfall" }, "size": 500, "duration": 100 }""",
        }.Select(MoraleSourceJson.Parse),
        new[]
        {
            """{ "id": "test:morale_band/low", "min": -100, "modifiers": [ { "stat": "aim_spread", "operation": "multiply", "value": 1.5 } ] }""",
            """{ "id": "test:morale_band/steady", "min": -20 }""",
            """{ "id": "test:morale_band/high", "min": 20, "modifiers": [ { "stat": "aim_spread", "operation": "add", "value": -2 } ] }""",
        }.Select(MoraleBandJson.Parse));

    private readonly Morale _morale = new(Catalog);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void ANewMorale_IsNeutralWithNoSources()
    {
        Assert.Equal(0, _morale.Value);
        Assert.Equal("test:morale_band/steady", _morale.Band?.Id);
        Assert.Empty(_morale.Sources);
        Assert.Empty(_morale.Modifiers);
    }

    [Fact]
    public void AGameEvent_TriggersTheSourceThatNamesIt()
    {
        var result = _morale.OnEvent(TeammateDied);

        Assert.True(result.IsSuccess);
        Assert.Contains(new MoraleSourceTriggered("test:morale_source/teammate_death", 1), result.Events);
        Assert.Equal(-30, _morale.Value);
    }

    [Fact]
    public void UsingAnItem_TriggersTheSourceThatNamesIt()
    {
        _morale.OnItemUsed(Chocolate);

        Assert.Equal(15, _morale.Value);
        Assert.Equal("test:morale_source/comfort", Assert.Single(_morale.Sources).Source);
    }

    [Fact]
    public void AnEventOrItemNoSourceNames_ChangesNothing()
    {
        var event1 = _morale.OnEvent(new StatName("nothing_happened"));
        var event2 = _morale.OnItemUsed(new ItemId("test:item/rock"));

        Assert.Empty(event1.Events);
        Assert.Empty(event2.Events);
        Assert.Equal(0, _morale.Value);
    }

    [Fact]
    public void Sources_AddUp()
    {
        _morale.OnEvent(HotMeal);
        _morale.OnItemUsed(Chocolate);
        _morale.OnEvent(GruesomeSight);

        Assert.Equal(20 + 15 - 10, _morale.Value);
    }

    [Fact]
    public void ASourceTriggeredAgain_StacksUpToItsLimit_AndStartsItsDurationOver()
    {
        _morale.OnEvent(GruesomeSight);
        _morale.Advance(S(200));

        var again = _morale.OnEvent(GruesomeSight);

        Assert.Contains(new MoraleSourceTriggered("test:morale_source/gruesome_sight", 2), again.Events);
        Assert.Equal(-20, _morale.Value);

        _morale.OnEvent(GruesomeSight);
        var atLimit = _morale.OnEvent(GruesomeSight);

        Assert.Contains(new MoraleSourceTriggered("test:morale_source/gruesome_sight", 3), atLimit.Events);
        Assert.Equal(-30, _morale.Value);
        Assert.Equal(S(400), Assert.Single(_morale.Sources).Remaining);
    }

    [Fact]
    public void ASource_FadesInAStraightLineAndEndsAtItsDuration()
    {
        _morale.OnEvent(TeammateDied);

        _morale.Advance(S(250));
        Assert.Equal(-22.5, _morale.Value, 6);

        _morale.Advance(S(500));
        Assert.Equal(-7.5, _morale.Value, 6);

        var end = _morale.Advance(S(250));

        Assert.Contains(new MoraleSourceEnded("test:morale_source/teammate_death"), end.Events);
        Assert.Equal(0, _morale.Value);
        Assert.Empty(_morale.Sources);
    }

    [Fact]
    public void Morale_IsClampedToItsRange()
    {
        _morale.OnEvent(new StatName("windfall"));
        Assert.Equal(100, _morale.Value);

        var narrow = new Morale(Catalog, new MoraleConfig { Minimum = -10, Maximum = 10 });
        narrow.OnEvent(TeammateDied);
        Assert.Equal(-10, narrow.Value);
    }

    [Fact]
    public void Advance_RejectsAStepThatIsNotForward()
    {
        Assert.Equal(SurvivalError.InvalidDuration, _morale.Advance(S(0)).Error);
        Assert.Equal(SurvivalError.InvalidDuration, _morale.Advance(S(-1)).Error);
    }

    [Fact]
    public void LowMorale_GrantsItsBandsModifiers_WithTheMoraleSourceRecorded()
    {
        _morale.OnEvent(TeammateDied);
        _morale.OnEvent(GruesomeSight);

        var result = _morale.OnEvent(GruesomeSight);

        Assert.Equal("test:morale_band/low", _morale.Band?.Id);
        var modifier = Assert.Single(_morale.Modifiers);
        Assert.Equal(new ModifierSource("morale:test:morale_band/low"), modifier.Source);
        Assert.Equal(1.5, _morale.EffectiveValue(AimSpread, 1), 6);
        Assert.Empty(result.Events.OfType<MoraleBandChanged>());
    }

    [Fact]
    public void CrossingIntoABand_RaisesAnEvent_AndSwapsTheModifiers()
    {
        var up = _morale.OnEvent(HotMeal);

        Assert.Contains(new MoraleBandChanged("test:morale_band/steady", "test:morale_band/high"), up.Events);
        Assert.Equal(new ModifierSource("morale:test:morale_band/high"), Assert.Single(_morale.Modifiers).Source);
        Assert.Equal(3, _morale.EffectiveValue(AimSpread, 5));
    }

    [Fact]
    public void ModifiersGoWhenMoraleLeavesTheBand_AsItsSourcesFade()
    {
        _morale.OnEvent(HotMeal);

        var fade = _morale.Advance(S(100));

        Assert.Contains(new MoraleBandChanged("test:morale_band/high", "test:morale_band/steady"), fade.Events);
        Assert.Empty(_morale.Modifiers);
        Assert.Equal(5, _morale.EffectiveValue(AimSpread, 5));
    }

    [Fact]
    public void MoraleBelowTheLowestBand_HasNoBand()
    {
        var catalog = new MoraleCatalog(Catalog.Sources, [MoraleBandJson.Parse("""{ "id": "test:morale_band/high", "min": 20 }""")]);

        Assert.Null(new Morale(catalog).Band);
    }

    [Fact]
    public void Json_RejectsInvalidDefinitions()
    {
        Assert.Throws<MoraleDefinitionException>(() => MoraleSourceJson.Parse("""{ "id": "test:morale_source/x", "trigger": {}, "size": 1, "duration": 10 }"""));
        Assert.Throws<MoraleDefinitionException>(() => MoraleSourceJson.Parse("""{ "id": "test:morale_source/x", "trigger": { "event": "a", "item": "test:item/b" }, "size": 1, "duration": 10 }"""));
        Assert.Throws<MoraleDefinitionException>(() => MoraleSourceJson.Parse("""{ "id": "test:morale_source/x", "trigger": { "event": "a" }, "size": 0, "duration": 10 }"""));
        Assert.Throws<MoraleDefinitionException>(() => MoraleSourceJson.Parse("""{ "id": "test:morale_source/x", "trigger": { "event": "a" }, "size": 1, "duration": 0 }"""));
        Assert.Throws<MoraleDefinitionException>(() => MoraleSourceJson.Parse("""{ "id": "test:morale_source/x", "trigger": { "event": "Not Valid" }, "size": 1, "duration": 10 }"""));
        Assert.Throws<MoraleDefinitionException>(() => MoraleBandJson.Parse("""{ "id": "test:morale_band/x", "min": 0, "modifiers": [ { "stat": "Bad Stat", "operation": "add", "value": 1 } ] }"""));
        Assert.Throws<MoraleDefinitionException>(() => MoraleBandJson.Parse("not json"));
    }

    [Fact]
    public void Catalog_RejectsDuplicatesAndBandsThatStartTogether()
    {
        var source = MoraleSourceJson.Parse("""{ "id": "test:morale_source/a", "trigger": { "event": "a" }, "size": 1, "duration": 10 }""");
        var band = MoraleBandJson.Parse("""{ "id": "test:morale_band/a", "min": 0 }""");
        var sameStart = MoraleBandJson.Parse("""{ "id": "test:morale_band/b", "min": 0 }""");

        Assert.Throws<ArgumentException>(() => new MoraleCatalog([source, source]));
        Assert.Throws<ArgumentException>(() => new MoraleCatalog([source], [band, band]));
        Assert.Throws<ArgumentException>(() => new MoraleCatalog([source], [band, sameStart]));
    }
}
