using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

public enum SurvivalError
{
    InvalidDuration,
    InvalidAmount,
    AlreadyAsleep,
    NotAsleep,
    NotTired,
}

public enum HungerLevel
{
    Satiated,
    Hungry,
    Starving,
}

public enum ThirstLevel
{
    Hydrated,
    Thirsty,
    Dehydrated,
}

public enum TemperatureLevel
{
    Hypothermic,
    Cold,
    Normal,
    Hot,
    Hyperthermic,
}

public enum FatigueLevel
{
    Rested,
    Tired,
    Exhausted,
}

/// <summary>Where a character sleeps, which sets how well they rest.</summary>
public enum RestPlace
{
    Ground,
    Shelter,
    Bed,
}

/// <summary>What ended a character's sleep.</summary>
public enum WakeCause
{
    /// <summary>Fatigue ran out.</summary>
    Rested,

    /// <summary>A noise within the sleeper's hearing.</summary>
    Noise,

    /// <summary>The character took damage.</summary>
    Hurt,

    /// <summary>The player chose to get up.</summary>
    Chosen,
}

public sealed record HungerLevelChanged(HungerLevel From, HungerLevel To) : IDomainEvent;

public sealed record ThirstLevelChanged(ThirstLevel From, ThirstLevel To) : IDomainEvent;

public sealed record TemperatureLevelChanged(TemperatureLevel From, TemperatureLevel To) : IDomainEvent;

public sealed record FatigueLevelChanged(FatigueLevel From, FatigueLevel To) : IDomainEvent;

/// <param name="Collapsed">True when exhaustion forced the sleep, false when the character chose it.</param>
public sealed record FellAsleep(bool Collapsed) : IDomainEvent;

public sealed record WokeUp(WakeCause Cause) : IDomainEvent;

/// <summary>Tuning for Needs. Defaults model a healthy adult.</summary>
public sealed record NeedsConfig
{
    /// <summary>Fraction of satiety lost per hour (0.05 empties a full stomach in 20 hours).</summary>
    public double HungerPerHour { get; init; } = 0.05;

    /// <summary>Fraction of hydration lost per hour.</summary>
    public double ThirstPerHour { get; init; } = 0.12;

    /// <summary>Water that restores hydration from empty to full.</summary>
    public Volume WaterCapacity { get; init; } = Volume.FromLiters(3);

    public Temperature CoreTemperature { get; init; } = Temperature.FromDegreesCelsius(37);

    /// <summary>Thermal resistance of bare skin and the air layer next to it.</summary>
    public ThermalResistance BareResistance { get; init; } = ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1);

    /// <summary>Net heat flow per square metre that holds the core at its normal temperature when bare at 28 degrees Celsius.</summary>
    public double HeatFlowWattsPerSquareMeter { get; init; } = 90;

    /// <summary>Hours for body temperature to close about 63 percent of the gap to its equilibrium.</summary>
    public double ThermalTimeConstantHours { get; init; } = 1;

    /// <summary>Fraction of fatigue gained per hour awake (0.0625 reaches collapse after 16 hours).</summary>
    public double FatiguePerHour { get; init; } = 0.0625;

    /// <summary>Fraction of fatigue lost per hour asleep, before the factor of where the character sleeps.</summary>
    public double SleepRecoveryPerHour { get; init; } = 0.125;

    /// <summary>How well one rests on bare ground, as a multiple of <see cref="SleepRecoveryPerHour"/>.</summary>
    public double GroundRestFactor { get; init; } = 0.6;

    /// <summary>How well one rests in a shelter, as a multiple of <see cref="SleepRecoveryPerHour"/>.</summary>
    public double ShelterRestFactor { get; init; } = 1;

    /// <summary>How well one rests in a bed, as a multiple of <see cref="SleepRecoveryPerHour"/>.</summary>
    public double BedRestFactor { get; init; } = 1.5;

    /// <summary>The least fatigue at which a character can fall asleep by choice.</summary>
    public double MinSleepFatigue { get; init; } = 0.25;

    /// <summary>How far a sleeper hears, as a fraction of how far a noise carries.</summary>
    public double SleepingHearing { get; init; } = 0.5;

    public double RestFactor(RestPlace place) => place switch
    {
        RestPlace.Bed => BedRestFactor,
        RestPlace.Shelter => ShelterRestFactor,
        _ => GroundRestFactor,
    };
}

/// <summary>Outcome of a Domain command: either an error with no state change, or the events raised.</summary>
public sealed class SurvivalResult
{
    private static readonly IDomainEvent[] NoEvents = [];

    private SurvivalResult(SurvivalError? error, IReadOnlyList<IDomainEvent> events)
    {
        Error = error;
        Events = events;
    }

    public bool IsSuccess => Error is null;

    public SurvivalError? Error { get; }

    public IReadOnlyList<IDomainEvent> Events { get; }

    /// <summary>A success that raised nothing, shared so that a Need that has not changed costs no allocation.</summary>
    public static SurvivalResult Unchanged { get; } = new(null, NoEvents);

    public static SurvivalResult Success(IReadOnlyList<IDomainEvent> events) => new(null, events);

    public static SurvivalResult Failure(SurvivalError error) => new(error, NoEvents);
}
