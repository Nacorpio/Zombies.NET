using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

/// <summary>
/// The survival Needs of one character: hunger, thirst, body temperature, and fatigue with the sleep that relieves it.
/// Insulation and ambient temperature are passed in by the caller, so Survival never reads another context's state.
/// </summary>
public sealed class Needs
{
    private const double MinBodyCelsius = 25;
    private const double MaxBodyCelsius = 43;

    private readonly NeedsConfig _config;
    private double _bodyCelsius;
    private RestPlace _restPlace;

    public Needs(NeedsConfig? config = null)
    {
        _config = config ?? new NeedsConfig();
        _bodyCelsius = _config.CoreTemperature.DegreesCelsius;
    }

    /// <summary>Fullness of the stomach, 1 when full and 0 when empty.</summary>
    public double Satiety { get; private set; } = 1;

    /// <summary>Water in the body, 1 when fully hydrated and 0 when empty.</summary>
    public double Hydration { get; private set; } = 1;

    /// <summary>How tired the body is, 0 when rested and 1 when it can stay awake no longer.</summary>
    public double Fatigue { get; private set; }

    /// <summary>Whether the character is asleep, which recovers fatigue instead of growing it.</summary>
    public bool IsSleeping { get; private set; }

    public Temperature BodyTemperature => Temperature.FromDegreesCelsius(_bodyCelsius);

    public NeedsSnapshot ToSnapshot() => new(Satiety, Hydration, _bodyCelsius, Fatigue);

    /// <summary>Rebuilds Needs from a snapshot. Throws <see cref="ArgumentException"/> when a value is outside what Needs can hold.</summary>
    public static Needs Restore(NeedsSnapshot snapshot, NeedsConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(snapshot.Satiety) || snapshot.Satiety is < 0 or > 1
            || !double.IsFinite(snapshot.Hydration) || snapshot.Hydration is < 0 or > 1
            || !double.IsFinite(snapshot.Fatigue) || snapshot.Fatigue is < 0 or > 1
            || !double.IsFinite(snapshot.BodyCelsius) || snapshot.BodyCelsius is < MinBodyCelsius or > MaxBodyCelsius)
        {
            throw new ArgumentException("Satiety, hydration, and fatigue must be between 0 and 1, and body temperature between 25 and 43 degrees Celsius.", nameof(snapshot));
        }

        return new Needs(config) { Satiety = snapshot.Satiety, Hydration = snapshot.Hydration, Fatigue = snapshot.Fatigue, _bodyCelsius = snapshot.BodyCelsius };
    }

    public HungerLevel Hunger => HungerOf(Satiety);

    public ThirstLevel Thirst => ThirstOf(Hydration);

    public TemperatureLevel Warmth => WarmthOf(_bodyCelsius);

    public FatigueLevel Tiredness => TirednessOf(Fatigue);

    /// <summary>The temperature the body drifts toward given ambient temperature and total insulation.</summary>
    public Temperature EquilibriumTemperature(Temperature ambient, ThermalResistance insulation)
    {
        var resistance = _config.BareResistance.SquareMeterKelvinsPerWatt + insulation.SquareMeterKelvinsPerWatt;
        var celsius = ambient.DegreesCelsius + (_config.HeatFlowWattsPerSquareMeter * resistance);
        return Temperature.FromDegreesCelsius(Math.Clamp(celsius, MinBodyCelsius, MaxBodyCelsius));
    }

    /// <summary>Lets time pass: hunger, thirst and fatigue grow, and body temperature drifts toward its equilibrium. A sleeper recovers fatigue instead.</summary>
    public SurvivalResult Advance(TimeSpan elapsed, Temperature ambient, ThermalResistance insulation)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return SurvivalResult.Failure(SurvivalError.InvalidDuration);
        }

        var before = Snapshot();
        var hours = elapsed.TotalHours;
        var sleep = AdvanceSleep(hours);

        Satiety = Math.Max(0, Satiety - (_config.HungerPerHour * hours));
        Hydration = Math.Max(0, Hydration - (_config.ThirstPerHour * hours));

        var equilibrium = EquilibriumTemperature(ambient, insulation).DegreesCelsius;
        var closed = 1 - Math.Exp(-hours / _config.ThermalTimeConstantHours);
        _bodyCelsius += (equilibrium - _bodyCelsius) * closed;

        var events = Changes(before);
        if (sleep is not null)
        {
            events.Add(sleep);
        }

        return SurvivalResult.Success(events);
    }

    /// <summary>
    /// Lets time pass for fatigue alone, for a caller that does not know the weather or what is worn. Awake, fatigue grows and
    /// the character collapses asleep when it is full. Asleep, it falls with how well they rest, and they wake when it is gone.
    /// </summary>
    public SurvivalResult AdvanceFatigue(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return SurvivalResult.Failure(SurvivalError.InvalidDuration);
        }

        var before = Snapshot();
        var sleep = AdvanceSleep(elapsed.TotalHours);
        if (sleep is null && Tiredness == before.Tiredness)
        {
            return SurvivalResult.Unchanged;
        }

        var events = Changes(before);
        if (sleep is not null)
        {
            events.Add(sleep);
        }

        return SurvivalResult.Success(events);
    }

    /// <summary>Falls asleep in a place that decides how well they rest. Only a character who is tired enough can.</summary>
    public SurvivalResult Sleep(RestPlace place)
    {
        if (IsSleeping)
        {
            return SurvivalResult.Failure(SurvivalError.AlreadyAsleep);
        }

        if (Fatigue < _config.MinSleepFatigue)
        {
            return SurvivalResult.Failure(SurvivalError.NotTired);
        }

        FallAsleep(place);
        return SurvivalResult.Success([new FellAsleep(Collapsed: false)]);
    }

    public SurvivalResult Wake(WakeCause cause)
    {
        if (!IsSleeping)
        {
            return SurvivalResult.Failure(SurvivalError.NotAsleep);
        }

        IsSleeping = false;
        return SurvivalResult.Success([new WokeUp(cause)]);
    }

    /// <summary>A noise reaches the character. A sleeper wakes when it is within what they hear, which is less than an awake character would.</summary>
    /// <param name="loudness">How far the noise carries.</param>
    /// <param name="distance">How far the character is from where it was made.</param>
    public SurvivalResult HearNoise(double loudness, double distance)
    {
        if (!double.IsFinite(loudness) || loudness < 0 || !double.IsFinite(distance) || distance < 0)
        {
            return SurvivalResult.Failure(SurvivalError.InvalidAmount);
        }

        return IsSleeping && distance <= loudness * _config.SleepingHearing ? Wake(WakeCause.Noise) : SurvivalResult.Unchanged;
    }

    /// <param name="relief">Fraction of fatigue the item takes away.</param>
    public SurvivalResult Refresh(double relief)
    {
        if (relief <= 0 || double.IsNaN(relief))
        {
            return SurvivalResult.Failure(SurvivalError.InvalidAmount);
        }

        var before = Snapshot();
        Fatigue = Math.Max(0, Fatigue - relief);
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

    private static FatigueLevel TirednessOf(double fatigue) =>
        fatigue > 0.75 ? FatigueLevel.Exhausted : fatigue > 0.5 ? FatigueLevel.Tired : FatigueLevel.Rested;

    private static TemperatureLevel WarmthOf(double celsius) => celsius switch
    {
        < 35 => TemperatureLevel.Hypothermic,
        < 36.5 => TemperatureLevel.Cold,
        <= 37.5 => TemperatureLevel.Normal,
        <= 39 => TemperatureLevel.Hot,
        _ => TemperatureLevel.Hyperthermic,
    };

    private (HungerLevel Hunger, ThirstLevel Thirst, TemperatureLevel Warmth, FatigueLevel Tiredness) Snapshot() => (Hunger, Thirst, Warmth, Tiredness);

    private void FallAsleep(RestPlace place)
    {
        IsSleeping = true;
        _restPlace = place;
    }

    /// <summary>Moves fatigue by the time passed and returns the fall asleep or wake up that follows from it, if any.</summary>
    private IDomainEvent? AdvanceSleep(double hours)
    {
        if (IsSleeping)
        {
            Fatigue = Math.Max(0, Fatigue - (_config.SleepRecoveryPerHour * _config.RestFactor(_restPlace) * hours));
            if (Fatigue == 0)
            {
                IsSleeping = false;
                return new WokeUp(WakeCause.Rested);
            }
        }
        else
        {
            Fatigue = Math.Min(1, Fatigue + (_config.FatiguePerHour * hours));
            if (Fatigue == 1)
            {
                FallAsleep(RestPlace.Ground);
                return new FellAsleep(Collapsed: true);
            }
        }

        return null;
    }

    private List<IDomainEvent> Changes((HungerLevel Hunger, ThirstLevel Thirst, TemperatureLevel Warmth, FatigueLevel Tiredness) before)
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

        if (Tiredness != before.Tiredness)
        {
            events.Add(new FatigueLevelChanged(before.Tiredness, Tiredness));
        }

        return events;
    }
}
