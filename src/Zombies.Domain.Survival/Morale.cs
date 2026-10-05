using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

/// <summary>A Morale source became active, or was triggered again.</summary>
public sealed record MoraleSourceTriggered(string Source, int Stacks) : IDomainEvent;

/// <summary>A Morale source has faded away.</summary>
public sealed record MoraleSourceEnded(string Source) : IDomainEvent;

/// <summary>Morale moved into another band. A band is null when Morale is below the lowest one.</summary>
public sealed record MoraleBandChanged(string? From, string? To) : IDomainEvent;

/// <summary>Read-only view of an active Morale source: how many stacks it has and how much it moves Morale right now.</summary>
public sealed record ActiveMoraleSource(string Source, int Stacks, double Amount, TimeSpan Remaining);

/// <summary>Tuning for Morale.</summary>
public sealed record MoraleConfig
{
    public double Minimum { get; init; } = -100;

    public double Maximum { get; init; } = 100;
}

/// <summary>
/// How one character feels. Morale is the sum of the Morale sources that are active, clamped to the configured range. Each
/// source fades away in a straight line over its duration. The band Morale is in grants Modifiers, recorded with the source
/// <c>morale:</c> followed by the band's Content ID, so they can be shown and go when Morale leaves the band.
/// </summary>
public sealed class Morale
{
    private sealed class Active(MoraleSourceDefinition definition)
    {
        public MoraleSourceDefinition Definition { get; } = definition;

        public int Stacks { get; set; } = 1;

        public TimeSpan Remaining { get; set; } = definition.Duration;

        public double Amount => Definition.Size * Stacks * (Remaining / Definition.Duration);
    }

    private readonly MoraleCatalog _catalog;
    private readonly MoraleConfig _config;
    private readonly List<Active> _active = [];
    private ModifierSet _modifiers = new();

    public Morale(MoraleCatalog catalog, MoraleConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _config = config ?? new MoraleConfig();
        Rebuild();
    }

    /// <summary>The sum of every active source, clamped to the configured range.</summary>
    public double Value => Math.Clamp(_active.Sum(a => a.Amount), _config.Minimum, _config.Maximum);

    /// <summary>The band Morale is in, or null when it is below the lowest band.</summary>
    public MoraleBandDefinition? Band => _catalog.BandAt(Value);

    public IReadOnlyList<ActiveMoraleSource> Sources => [.. _active.Select(a => new ActiveMoraleSource(a.Definition.Id, a.Stacks, a.Amount, a.Remaining))];

    /// <summary>The Modifiers granted by the current band.</summary>
    public IReadOnlyList<Modifier> Modifiers => _modifiers.All;

    public double EffectiveValue(StatName stat, double baseValue) => _modifiers.EffectiveValue(stat, baseValue);

    /// <summary>Triggers every source that a game event gives.</summary>
    public SurvivalResult OnEvent(StatName gameEvent) => Trigger(s => s.Trigger.Event == gameEvent);

    /// <summary>Triggers every source that using this Item gives.</summary>
    public SurvivalResult OnItemUsed(ItemId item) => Trigger(s => s.Trigger.Item == item);

    /// <summary>Lets time pass, so every source fades and the ones that have run out end.</summary>
    public SurvivalResult Advance(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return SurvivalResult.Failure(SurvivalError.InvalidDuration);
        }

        var before = Band;
        var events = new List<IDomainEvent>();
        foreach (var active in _active.ToList())
        {
            active.Remaining -= elapsed;
            if (active.Remaining <= TimeSpan.Zero)
            {
                _active.Remove(active);
                events.Add(new MoraleSourceEnded(active.Definition.Id));
            }
        }

        return Changed(before, events);
    }

    private SurvivalResult Trigger(Func<MoraleSourceDefinition, bool> matches)
    {
        var before = Band;
        var events = new List<IDomainEvent>();
        foreach (var source in _catalog.Sources.Where(matches))
        {
            var active = _active.Find(a => a.Definition == source);
            if (active is null)
            {
                active = new Active(source);
                _active.Add(active);
            }
            else
            {
                active.Remaining = source.Duration;
                active.Stacks = Math.Min(active.Stacks + 1, source.MaxStacks);
            }

            events.Add(new MoraleSourceTriggered(source.Id, active.Stacks));
        }

        return Changed(before, events);
    }

    private SurvivalResult Changed(MoraleBandDefinition? before, List<IDomainEvent> events)
    {
        var after = Band;
        if (after != before)
        {
            events.Add(new MoraleBandChanged(before?.Id, after?.Id));
            Rebuild();
        }

        return SurvivalResult.Success(events);
    }

    private void Rebuild()
    {
        var modifiers = new ModifierSet();
        if (Band is { } band)
        {
            var source = new ModifierSource($"morale:{band.Id}");
            foreach (var modifier in band.Modifiers)
            {
                modifiers.Add(new Modifier(modifier.Stat, modifier.Operation, modifier.Value, source));
            }
        }

        _modifiers = modifiers;
    }
}
