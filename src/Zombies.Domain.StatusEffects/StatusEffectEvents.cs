using Zombies.Domain.Items;

namespace Zombies.Domain.StatusEffects;

public enum CureCause
{
    Item,
    Effect,
}

public enum EffectError
{
    UnknownEffect,
    NotActive,
    NothingToCure,
    InvalidDuration,
    NotConsumable,
}

public sealed record EffectApplied(CreatureId Creature, string Effect, int Stacks) : IDomainEvent;

public sealed record EffectRefreshed(CreatureId Creature, string Effect, TimeSpan? Remaining) : IDomainEvent;

public sealed record EffectExtended(CreatureId Creature, string Effect, TimeSpan Remaining) : IDomainEvent;

public sealed record EffectStacked(CreatureId Creature, string Effect, int Stacks) : IDomainEvent;

public sealed record EffectIgnored(CreatureId Creature, string Effect) : IDomainEvent;

public sealed record EffectStageChanged(CreatureId Creature, string Effect, string Stage) : IDomainEvent;

/// <summary>A periodic change is due. Whoever owns the thing it changes, such as Combat or Survival, applies it. The amount is already scaled by stacks.</summary>
public sealed record EffectPeriodicChange(CreatureId Creature, string Effect, StatName Change, double Amount) : IDomainEvent;

/// <summary>The effect ran its full duration, which is how time cures it.</summary>
public sealed record EffectExpired(CreatureId Creature, string Effect) : IDomainEvent;

public sealed record EffectRemoved(CreatureId Creature, string Effect) : IDomainEvent;

/// <summary>The effect was cured. <paramref name="By"/> is the Content ID of the item or the effect that cured it.</summary>
public sealed record EffectCured(CreatureId Creature, string Effect, CureCause Cause, string By) : IDomainEvent;

/// <summary>Read-only view of an effect active on a creature.</summary>
public sealed record ActiveEffect(string Effect, int Stacks, TimeSpan Elapsed, TimeSpan? Remaining, string? Stage);

/// <summary>Outcome of a Domain command: either an error with no state change, or the events raised.</summary>
public sealed class EffectResult
{
    private static readonly IDomainEvent[] NoEvents = [];

    private EffectResult(EffectError? error, IReadOnlyList<IDomainEvent> events)
    {
        Error = error;
        Events = events;
    }

    public bool IsSuccess => Error is null;

    public EffectError? Error { get; }

    public IReadOnlyList<IDomainEvent> Events { get; }

    public static EffectResult Success(IReadOnlyList<IDomainEvent> events) => new(null, events);

    /// <summary>A shared success that raised no events, so a command that changed nothing allocates nothing.</summary>
    internal static EffectResult NoChange { get; } = new(null, NoEvents);

    public static EffectResult Failure(EffectError error) => new(error, NoEvents);
}
