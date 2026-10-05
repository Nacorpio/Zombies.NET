using Zombies.Domain.Items;

namespace Zombies.Domain.StatusEffects;

/// <summary>
/// The Status effects on one creature. Commands change them and return the events raised; a rule violation is an error result, never an exception.
/// Effects only announce periodic changes as events, so they never reach into Combat or Survival.
/// </summary>
/// <remarks>
/// While time advances, an effect's events at the same moment come in this order: stage change, periodic changes, then expiry.
/// A periodic change due exactly when the stage changes belongs to the old stage and is dropped.
/// </remarks>
public sealed class CreatureEffects(CreatureId id, StatusEffectCatalog catalog)
{
    private sealed class PeriodicState(PeriodicChange change, TimeSpan due, bool stageScoped)
    {
        public PeriodicChange Change { get; } = change;

        public TimeSpan Due { get; set; } = due;

        public bool StageScoped { get; } = stageScoped;
    }

    private sealed class Active(StatusEffectDefinition definition)
    {
        public StatusEffectDefinition Definition { get; } = definition;

        public int Stacks { get; set; } = 1;

        public TimeSpan Elapsed { get; set; }

        public TimeSpan? Remaining { get; set; } = definition.Duration;

        public int StageIndex { get; set; } = -1;

        public List<PeriodicState> Periodic { get; } = [];
    }

    private readonly List<Active> _active = [];
    private ModifierSet _modifiers = new();

    public CreatureId Id { get; } = id;

    public IReadOnlyList<ActiveEffect> Effects =>
    [
        .. _active.Select(a => new ActiveEffect(
            a.Definition.Id,
            a.Stacks,
            a.Elapsed,
            a.Remaining,
            a.StageIndex >= 0 ? a.Definition.Stages[a.StageIndex].Name : null)),
    ];

    /// <summary>The Modifiers granted by active effects, one set per stack. They disappear when the effect ends.</summary>
    public IReadOnlyList<Modifier> Modifiers => _modifiers.All;

    public bool Has(string effect) => _active.Exists(a => a.Definition.Id == effect);

    public double EffectiveValue(StatName stat, double baseValue) => _modifiers.EffectiveValue(stat, baseValue);

    public EffectResult Apply(string effect)
    {
        if (!catalog.TryGet(effect, out var definition))
        {
            return EffectResult.Failure(EffectError.UnknownEffect);
        }

        var events = new List<IDomainEvent>();
        foreach (var cured in _active.Where(a => a.Definition.CuredByEffects.Contains(effect)).ToList())
        {
            _active.Remove(cured);
            events.Add(new EffectCured(Id, cured.Definition.Id, CureCause.Effect, effect));
        }

        var existing = _active.Find(a => a.Definition.Id == effect);
        if (existing is null)
        {
            var added = new Active(definition);
            foreach (var periodic in definition.Periodic)
            {
                added.Periodic.Add(new PeriodicState(periodic, periodic.Every, stageScoped: false));
            }

            _active.Add(added);
            events.Add(new EffectApplied(Id, effect, 1));
            EnterDueStages(added, events);
        }
        else
        {
            Restack(existing, events);
        }

        RebuildModifiers();
        return EffectResult.Success(events);
    }

    public EffectResult Remove(string effect)
    {
        var active = _active.Find(a => a.Definition.Id == effect);
        if (active is null)
        {
            return EffectResult.Failure(EffectError.NotActive);
        }

        _active.Remove(active);
        RebuildModifiers();
        return EffectResult.Success([new EffectRemoved(Id, effect)]);
    }

    /// <summary>
    /// Takes an effect back one stage, as when a withdrawal is eased by taking the substance again. An effect in its first
    /// stage, or with no stages, ends instead. Stacks and any remaining duration are not changed.
    /// </summary>
    public EffectResult Ease(string effect)
    {
        var active = _active.Find(a => a.Definition.Id == effect);
        if (active is null)
        {
            return EffectResult.Failure(EffectError.NotActive);
        }

        var events = new List<IDomainEvent>();
        if (active.StageIndex <= 0)
        {
            _active.Remove(active);
            events.Add(new EffectEased(Id, effect, null));
        }
        else
        {
            var stages = active.Definition.Stages;
            var shift = active.Elapsed - stages[active.StageIndex - 1].After;
            active.Elapsed -= shift;
            foreach (var periodic in active.Periodic.Where(p => !p.StageScoped))
            {
                periodic.Due -= shift;
            }

            active.StageIndex -= 2;
            EnterDueStages(active, events);
            events.Add(new EffectEased(Id, effect, stages[active.StageIndex].Name));
        }

        RebuildModifiers();
        return EffectResult.Success(events);
    }

    /// <summary>Cures every active effect that this item cures.</summary>
    public EffectResult CureWithItem(ItemId item)
    {
        var cured = _active.Where(a => a.Definition.CuredByItems.Contains(item)).ToList();
        if (cured.Count == 0)
        {
            return EffectResult.Failure(EffectError.NothingToCure);
        }

        _active.RemoveAll(cured.Contains);
        RebuildModifiers();
        return EffectResult.Success([.. cured.Select(a => new EffectCured(Id, a.Definition.Id, CureCause.Item, item.Value))]);
    }

    /// <summary>Moves every effect forward in time, raising stage changes, periodic changes, and expiries as they come due.</summary>
    public EffectResult Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            return EffectResult.Failure(EffectError.InvalidDuration);
        }

        var events = new List<IDomainEvent>();
        if (elapsed == TimeSpan.Zero)
        {
            return EffectResult.Success(events);
        }

        foreach (var active in _active.ToList())
        {
            if (!AdvanceOne(active, elapsed, events))
            {
                _active.Remove(active);
            }
        }

        RebuildModifiers();
        return EffectResult.Success(events);
    }

    private void Restack(Active existing, List<IDomainEvent> events)
    {
        var definition = existing.Definition;
        switch (definition.Stacking)
        {
            case StackingRule.Refresh:
                existing.Remaining = definition.Duration;
                events.Add(new EffectRefreshed(Id, definition.Id, existing.Remaining));
                break;
            case StackingRule.Extend when definition.Duration is { } extra && existing.Remaining is { } left:
                existing.Remaining = left + extra;
                events.Add(new EffectExtended(Id, definition.Id, existing.Remaining.Value));
                break;
            case StackingRule.Stack:
                existing.Remaining = definition.Duration;
                if (existing.Stacks < definition.MaxStacks)
                {
                    existing.Stacks++;
                    events.Add(new EffectStacked(Id, definition.Id, existing.Stacks));
                }
                else
                {
                    events.Add(new EffectRefreshed(Id, definition.Id, existing.Remaining));
                }

                break;
            default:
                events.Add(new EffectIgnored(Id, definition.Id));
                break;
        }
    }

    private void EnterDueStages(Active active, List<IDomainEvent> events)
    {
        var stages = active.Definition.Stages;
        while (active.StageIndex + 1 < stages.Count && stages[active.StageIndex + 1].After <= active.Elapsed)
        {
            active.StageIndex++;
            var stage = stages[active.StageIndex];
            active.Periodic.RemoveAll(p => p.StageScoped);
            foreach (var periodic in stage.Periodic)
            {
                active.Periodic.Add(new PeriodicState(periodic, active.Elapsed + periodic.Every, stageScoped: true));
            }

            events.Add(new EffectStageChanged(Id, active.Definition.Id, stage.Name));
        }
    }

    /// <returns>False when the effect ended.</returns>
    private bool AdvanceOne(Active active, TimeSpan elapsed, List<IDomainEvent> events)
    {
        var stages = active.Definition.Stages;
        var left = elapsed;
        while (left > TimeSpan.Zero)
        {
            var step = left;
            if (active.Remaining is { } remaining && remaining < step)
            {
                step = remaining;
            }

            if (active.StageIndex + 1 < stages.Count)
            {
                step = TimeSpan.FromTicks(Math.Min(step.Ticks, (stages[active.StageIndex + 1].After - active.Elapsed).Ticks));
            }

            foreach (var periodic in active.Periodic)
            {
                step = TimeSpan.FromTicks(Math.Min(step.Ticks, (periodic.Due - active.Elapsed).Ticks));
            }

            active.Elapsed += step;
            left -= step;
            if (active.Remaining is { } before)
            {
                active.Remaining = before - step;
            }

            EnterDueStages(active, events);
            foreach (var periodic in active.Periodic.ToList().Where(p => p.Due <= active.Elapsed))
            {
                events.Add(new EffectPeriodicChange(Id, active.Definition.Id, periodic.Change.Change, periodic.Change.Amount * active.Stacks));
                periodic.Due += periodic.Change.Every;
            }

            if (active.Remaining is { } after && after <= TimeSpan.Zero)
            {
                events.Add(new EffectExpired(Id, active.Definition.Id));
                return false;
            }
        }

        return true;
    }

    private void RebuildModifiers()
    {
        var modifiers = new ModifierSet();
        foreach (var active in _active)
        {
            var source = new ModifierSource($"status_effect:{active.Definition.Id}");
            var granted = active.Definition.Modifiers.Concat(active.StageIndex >= 0 ? active.Definition.Stages[active.StageIndex].Modifiers : []);
            foreach (var modifier in granted)
            {
                for (var stack = 0; stack < active.Stacks; stack++)
                {
                    modifiers.Add(new Modifier(modifier.Stat, modifier.Operation, modifier.Value, source));
                }
            }
        }

        _modifiers = modifiers;
    }
}
