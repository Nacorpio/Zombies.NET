using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

public enum SurvivalError
{
    InvalidDuration,
    InvalidAmount,
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

public sealed record HungerLevelChanged(HungerLevel From, HungerLevel To) : IDomainEvent;

public sealed record ThirstLevelChanged(ThirstLevel From, ThirstLevel To) : IDomainEvent;

public sealed record TemperatureLevelChanged(TemperatureLevel From, TemperatureLevel To) : IDomainEvent;

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

    public static SurvivalResult Success(IReadOnlyList<IDomainEvent> events) => new(null, events);

    public static SurvivalResult Failure(SurvivalError error) => new(error, NoEvents);
}
