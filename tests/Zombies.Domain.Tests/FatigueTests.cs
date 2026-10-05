using UnitsNet;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;

namespace Zombies.Domain.Tests;

public sealed class FatigueTests
{
    private static NeedsSnapshot Snapshot(double fatigue) => new(1, 1, 37, fatigue);

    [Fact]
    public void NewNeeds_StartRested()
    {
        var needs = new Needs();

        Assert.Equal(0, needs.Fatigue);
        Assert.Equal(FatigueLevel.Rested, needs.Tiredness);
        Assert.False(needs.IsSleeping);
    }

    [Fact]
    public void AdvanceFatigue_GrowsWhileAwakeAndReportsEachLevelChangeOnce()
    {
        var needs = new Needs();

        var first = needs.AdvanceFatigue(TimeSpan.FromHours(10));
        var second = needs.AdvanceFatigue(TimeSpan.FromMinutes(1));

        Assert.Equal(0.626, needs.Fatigue, 3);
        Assert.Equal(FatigueLevel.Tired, needs.Tiredness);
        Assert.Contains(first.Events, e => e is FatigueLevelChanged { From: FatigueLevel.Rested, To: FatigueLevel.Tired });
        Assert.DoesNotContain(second.Events, e => e is FatigueLevelChanged);
    }

    [Fact]
    public void AdvanceFatigue_UsesTheConfiguredRate()
    {
        var needs = new Needs(new NeedsConfig { FatiguePerHour = 0.2 });

        needs.AdvanceFatigue(TimeSpan.FromHours(2));

        Assert.Equal(0.4, needs.Fatigue, 6);
    }

    [Fact]
    public void Advance_AlsoGrowsFatigue()
    {
        var needs = new Needs();

        needs.Advance(TimeSpan.FromHours(4), Temperature.FromDegreesCelsius(28), ThermalResistance.Zero);

        Assert.Equal(0.25, needs.Fatigue, 6);
    }

    [Fact]
    public void AdvanceFatigue_WithNoTimePassing_Fails()
    {
        var result = new Needs().AdvanceFatigue(TimeSpan.Zero);

        Assert.Equal(SurvivalError.InvalidDuration, result.Error);
    }

    [Fact]
    public void Sleep_WhenRested_Fails()
    {
        var needs = new Needs();

        var result = needs.Sleep(RestPlace.Bed);

        Assert.Equal(SurvivalError.NotTired, result.Error);
        Assert.False(needs.IsSleeping);
    }

    [Fact]
    public void Sleep_WhenTired_FallsAsleep()
    {
        var needs = Needs.Restore(Snapshot(0.6));

        var result = needs.Sleep(RestPlace.Ground);

        Assert.True(result.IsSuccess);
        Assert.True(needs.IsSleeping);
        Assert.Contains(result.Events, e => e is FellAsleep { Collapsed: false });
    }

    [Fact]
    public void Sleep_WhenAlreadyAsleep_Fails()
    {
        var needs = Needs.Restore(Snapshot(0.6));
        needs.Sleep(RestPlace.Ground);

        Assert.Equal(SurvivalError.AlreadyAsleep, needs.Sleep(RestPlace.Bed).Error);
    }

    [Fact]
    public void Sleeping_RecoversFatigueInsteadOfGrowingIt()
    {
        var needs = Needs.Restore(Snapshot(0.8));
        needs.Sleep(RestPlace.Shelter);

        var result = needs.AdvanceFatigue(TimeSpan.FromHours(2));

        Assert.Equal(0.55, needs.Fatigue, 6);
        Assert.Contains(result.Events, e => e is FatigueLevelChanged { From: FatigueLevel.Exhausted, To: FatigueLevel.Tired });
    }

    [Fact]
    public void Sleeping_InAComfortablePlace_RecoversFaster()
    {
        var ground = Needs.Restore(Snapshot(0.9));
        var shelter = Needs.Restore(Snapshot(0.9));
        var bed = Needs.Restore(Snapshot(0.9));
        ground.Sleep(RestPlace.Ground);
        shelter.Sleep(RestPlace.Shelter);
        bed.Sleep(RestPlace.Bed);

        foreach (var needs in new[] { ground, shelter, bed })
        {
            needs.AdvanceFatigue(TimeSpan.FromHours(2));
        }

        Assert.True(ground.Fatigue > shelter.Fatigue);
        Assert.True(shelter.Fatigue > bed.Fatigue);
    }

    [Fact]
    public void Sleeping_UntilFullyRested_WakesUp()
    {
        var needs = Needs.Restore(Snapshot(0.3));
        needs.Sleep(RestPlace.Bed);

        var result = needs.AdvanceFatigue(TimeSpan.FromHours(8));

        Assert.Equal(0, needs.Fatigue);
        Assert.False(needs.IsSleeping);
        Assert.Contains(result.Events, e => e is WokeUp { Cause: WakeCause.Rested });
    }

    [Fact]
    public void Fatigue_ReachingTheLimit_MakesTheCharacterCollapseAndSleep()
    {
        var needs = new Needs();

        var result = needs.AdvanceFatigue(TimeSpan.FromHours(20));

        Assert.True(needs.IsSleeping);
        Assert.Equal(1, needs.Fatigue, 6);
        Assert.Contains(result.Events, e => e is FellAsleep { Collapsed: true });
    }

    [Fact]
    public void Wake_ByChoice_EndsTheSleepAndKeepsTheFatigueLeft()
    {
        var needs = Needs.Restore(Snapshot(0.7));
        needs.Sleep(RestPlace.Bed);
        needs.AdvanceFatigue(TimeSpan.FromHours(1));

        var result = needs.Wake(WakeCause.Chosen);

        Assert.True(result.IsSuccess);
        Assert.False(needs.IsSleeping);
        Assert.Contains(result.Events, e => e is WokeUp { Cause: WakeCause.Chosen });
        Assert.True(needs.Fatigue is > 0 and < 0.7);
    }

    [Fact]
    public void Wake_WhenAwake_Fails()
    {
        Assert.Equal(SurvivalError.NotAsleep, new Needs().Wake(WakeCause.Chosen).Error);
    }

    [Fact]
    public void HearNoise_WithinTheSleepersHearing_WakesThem()
    {
        var needs = Needs.Restore(Snapshot(0.7));
        needs.Sleep(RestPlace.Ground);

        var result = needs.HearNoise(loudness: 40, distance: 15);

        Assert.False(needs.IsSleeping);
        Assert.Contains(result.Events, e => e is WokeUp { Cause: WakeCause.Noise });
    }

    [Fact]
    public void HearNoise_BeyondTheSleepersHearing_LeavesThemAsleep()
    {
        var needs = Needs.Restore(Snapshot(0.7));
        needs.Sleep(RestPlace.Ground);

        var result = needs.HearNoise(loudness: 40, distance: 30);

        Assert.True(needs.IsSleeping);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void HearNoise_WhenAwake_ChangesNothing()
    {
        var result = new Needs().HearNoise(loudness: 40, distance: 1);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Events);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(10, -1)]
    [InlineData(double.NaN, 5)]
    public void HearNoise_WithAnImpossibleNoise_Fails(double loudness, double distance)
    {
        Assert.Equal(SurvivalError.InvalidAmount, new Needs().HearNoise(loudness, distance).Error);
    }

    [Fact]
    public void Refresh_ReducesFatigueAndReportsRecovery()
    {
        var needs = Needs.Restore(Snapshot(0.9));

        var result = needs.Refresh(0.5);

        Assert.Equal(0.4, needs.Fatigue, 6);
        Assert.Contains(result.Events, e => e is FatigueLevelChanged { From: FatigueLevel.Exhausted, To: FatigueLevel.Rested });
    }

    [Fact]
    public void Refresh_NeverGoesBelowRested()
    {
        var needs = Needs.Restore(Snapshot(0.2));

        needs.Refresh(0.5);

        Assert.Equal(0, needs.Fatigue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void Refresh_WithAnImpossibleAmount_Fails(double relief)
    {
        Assert.Equal(SurvivalError.InvalidAmount, new Needs().Refresh(relief).Error);
    }

    [Fact]
    public void ARefreshingItem_ReducesFatigueByItsDefinedRelief()
    {
        var coffee = new ItemDefinition(new ItemId("test:item/coffee"), Mass.FromKilograms(0.3), Volume.FromMilliliters(250), 4, drinkable: true, fatigueRelief: 0.25);
        var needs = Needs.Restore(Snapshot(0.6));

        needs.Refresh(coffee.FatigueRelief);

        Assert.Equal(0.35, needs.Fatigue, 6);
    }

    [Fact]
    public void ARefreshingItem_ParsedFromJson_CarriesItsRelief()
    {
        var item = ItemDefinitionJson.Parse("""{ "id": "test:item/coffee", "mass": "0.3 kg", "volume": "250 ml", "drinkable": true, "fatigueRelief": 0.25 }""");

        Assert.Equal(0.25, item.FatigueRelief);
    }

    [Fact]
    public void AnItemDefinition_WithoutRelief_HasNone()
    {
        var item = ItemDefinitionJson.Parse("""{ "id": "test:item/beans", "mass": "0.4 kg", "volume": "350 ml", "edible": true }""");

        Assert.Equal(0, item.FatigueRelief);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    public void AnItemDefinition_WithImpossibleRelief_IsRejected(string relief)
    {
        Assert.Throws<ItemDefinitionException>(() => ItemDefinitionJson.Parse($$"""{ "id": "test:item/coffee", "mass": "0.3 kg", "volume": "250 ml", "drinkable": true, "fatigueRelief": {{relief}} }"""));
    }

    [Fact]
    public void Snapshot_KeepsFatigue()
    {
        var needs = Needs.Restore(Snapshot(0.42));

        var restored = Needs.Restore(needs.ToSnapshot());

        Assert.Equal(0.42, restored.Fatigue, 6);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Restore_WithImpossibleFatigue_Throws(double fatigue)
    {
        Assert.Throws<ArgumentException>(() => Needs.Restore(Snapshot(fatigue)));
    }
}
