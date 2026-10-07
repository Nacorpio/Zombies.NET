using Zombies.Domain.Items;

namespace Zombies.Domain.StatusEffects;

/// <summary>A creature used a substance. <paramref name="Tolerance"/> is its tolerance afterwards.</summary>
public sealed record SubstanceConsumed(CreatureId Creature, string Substance, int Doses, double Tolerance) : IDomainEvent;

/// <summary>A creature became addicted to a substance.</summary>
public sealed record AddictionDeveloped(CreatureId Creature, string Substance) : IDomainEvent;

/// <summary>Everything stored about one creature's use of one substance, enough to rebuild it.</summary>
public sealed record SubstanceUseSnapshot(string Substance, double Tolerance, bool IsAddicted, TimeSpan SinceLastUse);

/// <summary>Stores each creature's use of substances, keyed by the creature's id.</summary>
public interface ISubstanceUseRepository
{
    /// <summary>What is stored for this owner, or nothing when they have never used a substance.</summary>
    IReadOnlyList<SubstanceUseSnapshot> Load(long owner);

    /// <summary>Stores the use, replacing what was stored for this owner.</summary>
    void Save(long owner, IReadOnlyList<SubstanceUseSnapshot> use);
}

/// <summary>
/// One creature's use of substances: a tolerance and an addiction for each, built on its <see cref="CreatureEffects"/>.
/// Using a substance applies its effect, once per dose that tolerance has not cancelled, so the effect's stacking rule makes
/// impairment scale with the amount taken. An addicted creature that goes without gets the substance's withdrawal effect after
/// the delay; using the substance again takes it back a stage.
/// </summary>
/// <remarks>
/// Tolerance is also exposed as a Modifier on the substance's tolerance Stat, with the source <c>tolerance:</c> and the
/// substance's Content ID. Call <see cref="Advance"/> instead of advancing the effects directly, so withdrawal starts at the right moment.
/// </remarks>
public sealed class CreatureSubstances(SubstanceCatalog catalog, CreatureEffects effects)
{
    private sealed class Use(SubstanceDefinition substance)
    {
        public SubstanceDefinition Substance { get; } = substance;

        public double Tolerance { get; set; }

        public bool IsAddicted { get; set; }

        public TimeSpan SinceLastUse { get; set; }
    }

    private readonly List<Use> _uses = [];

    public CreatureId Id => effects.Id;

    /// <summary>How much tolerance the creature has built to a substance, from 0 to 1.</summary>
    public double Tolerance(string substance) => _uses.Find(u => u.Substance.Id == substance)?.Tolerance ?? 0;

    public bool IsAddicted(string substance) => _uses.Find(u => u.Substance.Id == substance)?.IsAddicted ?? false;

    /// <summary>The tolerance to each substance, as Modifiers that add it to the substance's tolerance Stat.</summary>
    public IReadOnlyList<Modifier> Modifiers =>
        [.. _uses.Where(u => u.Tolerance > 0).Select(u => new Modifier(u.Substance.ToleranceStat, ModifierOperation.Add, u.Tolerance, new ModifierSource($"tolerance:{u.Substance.Id}")))];

    public IReadOnlyList<SubstanceUseSnapshot> ToSnapshot() => [.. _uses.Select(u => new SubstanceUseSnapshot(u.Substance.Id, u.Tolerance, u.IsAddicted, u.SinceLastUse))];

    /// <summary>Rebuilds the creature's use of substances. Throws <see cref="ArgumentException"/> when a snapshot is not valid.</summary>
    public static CreatureSubstances Restore(SubstanceCatalog catalog, CreatureEffects effects, IEnumerable<SubstanceUseSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var substances = new CreatureSubstances(catalog, effects);
        foreach (var snapshot in snapshots)
        {
            if (!catalog.TryGet(snapshot.Substance, out var substance))
            {
                throw new ArgumentException($"Unknown substance '{snapshot.Substance}'.", nameof(snapshots));
            }

            if (!double.IsFinite(snapshot.Tolerance) || snapshot.Tolerance is < 0 or > 1 || snapshot.SinceLastUse < TimeSpan.Zero || substances._uses.Exists(u => u.Substance == substance))
            {
                throw new ArgumentException($"The use of '{snapshot.Substance}' is not valid.", nameof(snapshots));
            }

            substances._uses.Add(new Use(substance) { Tolerance = snapshot.Tolerance, IsAddicted = snapshot.IsAddicted, SinceLastUse = snapshot.SinceLastUse });
        }

        return substances;
    }

    /// <summary>
    /// Uses an Item that delivers a substance. Tolerance first cancels part of the doses, then the rest apply the effect and
    /// tolerance grows. Whether the creature becomes addicted depends on <paramref name="roll"/>, a number from 0 up to but
    /// not including 1 that the caller draws, so the same roll always gives the same result.
    /// </summary>
    public EffectResult Consume(ItemId item, double roll)
    {
        if (!catalog.TryGetForItem(item, out var substance, out var doses))
        {
            return EffectResult.Failure(EffectError.NotASubstance);
        }

        if (!double.IsFinite(roll) || roll is < 0 or >= 1)
        {
            return EffectResult.Failure(EffectError.InvalidRoll);
        }

        var use = _uses.Find(u => u.Substance == substance);
        if (use is null)
        {
            use = new Use(substance);
            _uses.Add(use);
        }

        var events = new List<IDomainEvent>();
        if (use.IsAddicted && substance.Withdrawal is { } withdrawal && effects.Has(withdrawal.Effect))
        {
            events.AddRange(effects.Ease(withdrawal.Effect).Events);
        }

        var applied = (int)Math.Round(doses * (1 - use.Tolerance), MidpointRounding.AwayFromZero);
        for (var dose = 0; dose < applied; dose++)
        {
            events.AddRange(effects.Apply(substance.Effect).Events);
        }

        if (!use.IsAddicted && roll < Math.Min(1, substance.AddictionChance * (1 + use.Tolerance)))
        {
            use.IsAddicted = true;
            events.Add(new AddictionDeveloped(Id, substance.Id));
        }

        use.Tolerance = Math.Min(1, use.Tolerance + (substance.ToleranceGrowth * doses));
        use.SinceLastUse = TimeSpan.Zero;
        events.Add(new SubstanceConsumed(Id, substance.Id, doses, use.Tolerance));
        return EffectResult.Success(events);
    }

    /// <summary>
    /// Moves the creature's effects and the time since each substance was last used forward, starting a withdrawal at the
    /// moment its delay is up.
    /// </summary>
    public EffectResult Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            return EffectResult.Failure(EffectError.InvalidDuration);
        }

        var events = new List<IDomainEvent>();
        BeginDueWithdrawals(events);
        var left = elapsed;
        while (left > TimeSpan.Zero)
        {
            var step = left;
            foreach (var use in _uses.Where(u => u.IsAddicted && u.Substance.Withdrawal is { } w && u.SinceLastUse < w.Delay))
            {
                step = TimeSpan.FromTicks(Math.Min(step.Ticks, (use.Substance.Withdrawal!.Delay - use.SinceLastUse).Ticks));
            }

            events.AddRange(effects.Advance(step).Events);
            foreach (var use in _uses)
            {
                use.SinceLastUse += step;
            }

            left -= step;
            BeginDueWithdrawals(events);
        }

        return EffectResult.Success(events);
    }

    private void BeginDueWithdrawals(List<IDomainEvent> events)
    {
        foreach (var use in _uses.Where(u => u.IsAddicted && u.Substance.Withdrawal is { } w && u.SinceLastUse >= w.Delay && !effects.Has(w.Effect)))
        {
            events.AddRange(effects.Apply(use.Substance.Withdrawal!.Effect).Events);
        }
    }
}
