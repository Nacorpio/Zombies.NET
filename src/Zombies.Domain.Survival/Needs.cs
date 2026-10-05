using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

/// <summary>
/// The survival Needs of one character: hunger, thirst, and body temperature.
/// Insulation and ambient temperature are passed in by the caller, so Survival never reads another context's state.
/// </summary>
public sealed class Needs
{
    private const double MinBodyCelsius = 25;
    private const double MaxBodyCelsius = 43;

    private readonly NeedsConfig _config;
    private double _bodyCelsius;

    public Needs(NeedsConfig? config = null)
    {
        _config = config ?? new NeedsConfig();
        _bodyCelsius = _config.CoreTemperature.DegreesCelsius;
    }

    /// <summary>Fullness of the stomach, 1 when full and 0 when empty.</summary>
    public double Satiety { get; private set; } = 1;

    /// <summary>Water in the body, 1 when fully hydrated and 0 when empty.</summary>
    public double Hydration { get; private set; } = 1;

    public Temperature BodyTemperature => Temperature.FromDegreesCelsius(_bodyCelsius);

    public NeedsSnapshot ToSnapshot() => new(Satiety, Hydration, _bodyCelsius);

    /// <summary>Rebuilds Needs from a snapshot. Throws <see cref="ArgumentException"/> when a value is outside what Needs can hold.</summary>
    public static Needs Restore(NeedsSnapshot snapshot, NeedsConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(snapshot.Satiety) || snapshot.Satiety is < 0 or > 1
            || !double.IsFinite(snapshot.Hydration) || snapshot.Hydration is < 0 or > 1
            || !double.IsFinite(snapshot.BodyCelsius) || snapshot.BodyCelsius is < MinBodyCelsius or > MaxBodyCelsius)
        {
            throw new ArgumentException("Satiety and hydration must be between 0 and 1, and body temperature between 25 and 43 degrees Celsius.", nameof(snapshot));
        }

        return new Needs(config) { Satiety = snapshot.Satiety, Hydration = snapshot.Hydration, _bodyCelsius = snapshot.BodyCelsius };
    }

    public HungerLevel Hunger => HungerOf(Satiety);

    public ThirstLevel Thirst => ThirstOf(Hydration);

    public TemperatureLevel Warmth => WarmthOf(_bodyCelsius);

    /// <summary>The temperature the body drifts toward given ambient temperature and total insulation.</summary>
    public Temperature EquilibriumTemperature(Temperature ambient, ThermalResistance insulation)
    {
        var resistance = _config.BareResistance.SquareMeterKelvinsPerWatt + insulation.SquareMeterKelvinsPerWatt;
        var celsius = ambient.DegreesCelsius + (_config.HeatFlowWattsPerSquareMeter * resistance);
        return Temperature.FromDegreesCelsius(Math.Clamp(celsius, MinBodyCelsius, MaxBodyCelsius));
    }

    /// <summary>Lets time pass: hunger and thirst grow, and body temperature drifts toward its equilibrium.</summary>
    public SurvivalResult Advance(TimeSpan elapsed, Temperature ambient, ThermalResistance insulation)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return SurvivalResult.Failure(SurvivalError.InvalidDuration);
        }

        var before = Snapshot();
        var hours = elapsed.TotalHours;

        Satiety = Math.Max(0, Satiety - (_config.HungerPerHour * hours));
        Hydration = Math.Max(0, Hydration - (_config.ThirstPerHour * hours));

        var equilibrium = EquilibriumTemperature(ambient, insulation).DegreesCelsius;
        var closed = 1 - Math.Exp(-hours / _config.ThermalTimeConstantHours);
        _bodyCelsius += (equilibrium - _bodyCelsius) * closed;

        return SurvivalResult.Success(Changes(before));
    }

    /// <param name="nutrition">Fraction of a full stomach the food restores.</param>
    public SurvivalResult Eat(double nutrition)
    {
        if (nutrition <= 0 || double.IsNaN(nutrition))
        {
            return SurvivalResult.Failure(SurvivalError.InvalidAmount);
        }

        var before = Snapshot();
        Satiety = Math.Min(1, Satiety + nutrition);
        return SurvivalResult.Success(Changes(before));
    }

    public SurvivalResult Drink(Volume amount)
    {
        if (amount <= Volume.Zero)
        {
            return SurvivalResult.Failure(SurvivalError.InvalidAmount);
        }

        var before = Snapshot();
        Hydration = Math.Min(1, Hydration + (amount.CubicMeters / _config.WaterCapacity.CubicMeters));
        return SurvivalResult.Success(Changes(before));
    }

    private static HungerLevel HungerOf(double satiety) =>
        satiety > 0.3 ? HungerLevel.Satiated : satiety > 0.1 ? HungerLevel.Hungry : HungerLevel.Starving;

    private static ThirstLevel ThirstOf(double hydration) =>
        hydration > 0.3 ? ThirstLevel.Hydrated : hydration > 0.1 ? ThirstLevel.Thirsty : ThirstLevel.Dehydrated;

    private static TemperatureLevel WarmthOf(double celsius) => celsius switch
    {
        < 35 => TemperatureLevel.Hypothermic,
        < 36.5 => TemperatureLevel.Cold,
        <= 37.5 => TemperatureLevel.Normal,
        <= 39 => TemperatureLevel.Hot,
        _ => TemperatureLevel.Hyperthermic,
    };

    private (HungerLevel Hunger, ThirstLevel Thirst, TemperatureLevel Warmth) Snapshot() => (Hunger, Thirst, Warmth);

    private List<IDomainEvent> Changes((HungerLevel Hunger, ThirstLevel Thirst, TemperatureLevel Warmth) before)
    {
        var events = new List<IDomainEvent>();
        if (Hunger != before.Hunger)
        {
            events.Add(new HungerLevelChanged(before.Hunger, Hunger));
        }

        if (Thirst != before.Thirst)
        {
            events.Add(new ThirstLevelChanged(before.Thirst, Thirst));
        }

        if (Warmth != before.Warmth)
        {
            events.Add(new TemperatureLevelChanged(before.Warmth, Warmth));
        }

        return events;
    }
}
