using UnitsNet;
using Zombies.Domain.Survival;

namespace Zombies.Domain.Tests;

public sealed class SurvivalTests
{
    private static readonly Temperature Comfortable = Temperature.FromDegreesCelsius(28);
    private static readonly ThermalResistance None = ThermalResistance.Zero;

    [Fact]
    public void NewNeeds_StartFullAndAtNormalTemperature()
    {
        var needs = new Needs();

        Assert.Equal(1, needs.Satiety);
        Assert.Equal(1, needs.Hydration);
        Assert.Equal(37, needs.BodyTemperature.DegreesCelsius, 6);
        Assert.Equal(HungerLevel.Satiated, needs.Hunger);
        Assert.Equal(ThirstLevel.Hydrated, needs.Thirst);
        Assert.Equal(TemperatureLevel.Normal, needs.Warmth);
    }

    [Fact]
    public void Advance_GrowsHungerAndThirstOverTime()
    {
        var needs = new Needs();

        var result = needs.Advance(TimeSpan.FromHours(10), Comfortable, None);

        Assert.Equal(0.5, needs.Satiety, 6);
        Assert.Equal(0, needs.Hydration, 6);
        Assert.Equal(HungerLevel.Satiated, needs.Hunger);
        Assert.Contains(result.Events, e => e is ThirstLevelChanged { From: ThirstLevel.Hydrated, To: ThirstLevel.Dehydrated });
        Assert.DoesNotContain(result.Events, e => e is HungerLevelChanged);
    }

    [Fact]
    public void Advance_EventsReportEachLevelChangeOnce()
    {
        var needs = new Needs();

        var first = needs.Advance(TimeSpan.FromHours(7), Comfortable, None);
        var second = needs.Advance(TimeSpan.FromMinutes(1), Comfortable, None);

        Assert.Contains(first.Events, e => e is ThirstLevelChanged);
        Assert.DoesNotContain(second.Events, e => e is ThirstLevelChanged);
    }

    [Fact]
    public void Advance_ColdWithoutInsulation_CoolsTheBody()
    {
        var needs = new Needs();

        var result = needs.Advance(TimeSpan.FromHours(6), Temperature.FromDegreesCelsius(0), None);

        Assert.True(needs.BodyTemperature.DegreesCelsius < 35);
        Assert.Equal(TemperatureLevel.Hypothermic, needs.Warmth);
        Assert.Contains(result.Events, e => e is TemperatureLevelChanged { From: TemperatureLevel.Normal, To: TemperatureLevel.Hypothermic });
    }

    [Fact]
    public void Advance_ColdWithEnoughInsulation_HoldsNormalTemperature()
    {
        var needs = new Needs();
        var warm = ThermalResistance.FromSquareMeterKelvinsPerWatt(0.31);

        var result = needs.Advance(TimeSpan.FromHours(12), Temperature.FromDegreesCelsius(0), warm);

        Assert.Equal(TemperatureLevel.Normal, needs.Warmth);
        Assert.DoesNotContain(result.Events, e => e is TemperatureLevelChanged);
    }

    [Fact]
    public void Advance_MoreInsulationMeansWarmer()
    {
        var bare = new Needs();
        var dressed = new Needs();
        var cold = Temperature.FromDegreesCelsius(10);

        bare.Advance(TimeSpan.FromHours(2), cold, None);
        dressed.Advance(TimeSpan.FromHours(2), cold, ThermalResistance.FromSquareMeterKelvinsPerWatt(0.2));

        Assert.True(dressed.BodyTemperature > bare.BodyTemperature);
    }

    [Fact]
    public void Advance_HotWithoutRelief_OverheatsTheBody()
    {
        var needs = new Needs();

        var result = needs.Advance(TimeSpan.FromHours(6), Temperature.FromDegreesCelsius(40), None);

        Assert.Equal(TemperatureLevel.Hyperthermic, needs.Warmth);
        Assert.Contains(result.Events, e => e is TemperatureLevelChanged { To: TemperatureLevel.Hyperthermic });
    }

    [Fact]
    public void Drink_RestoresHydrationAndReportsRecovery()
    {
        var needs = new Needs();
        needs.Advance(TimeSpan.FromHours(10), Comfortable, None);

        var result = needs.Drink(Volume.FromLiters(3));

        Assert.Equal(1, needs.Hydration, 6);
        Assert.Contains(result.Events, e => e is ThirstLevelChanged { From: ThirstLevel.Dehydrated, To: ThirstLevel.Hydrated });
    }

    [Fact]
    public void Eat_RestoresSatietyWithoutExceedingFull()
    {
        var needs = new Needs();
        needs.Advance(TimeSpan.FromHours(19), Comfortable, None);
        Assert.Equal(HungerLevel.Starving, needs.Hunger);

        var result = needs.Eat(0.6);

        Assert.Equal(HungerLevel.Satiated, needs.Hunger);
        Assert.Contains(result.Events, e => e is HungerLevelChanged { From: HungerLevel.Starving, To: HungerLevel.Satiated });

        needs.Eat(5);

        Assert.Equal(1, needs.Satiety);
    }

    [Fact]
    public void Commands_RejectInvalidAmountsAndDurations()
    {
        var needs = new Needs();

        Assert.Equal(SurvivalError.InvalidAmount, needs.Eat(0).Error);
        Assert.Equal(SurvivalError.InvalidAmount, needs.Eat(-1).Error);
        Assert.Equal(SurvivalError.InvalidAmount, needs.Drink(Volume.Zero).Error);
        Assert.Equal(SurvivalError.InvalidDuration, needs.Advance(TimeSpan.Zero, Comfortable, None).Error);
        Assert.Equal(1, needs.Satiety);
    }
}
