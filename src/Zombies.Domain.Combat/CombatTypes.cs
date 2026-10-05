using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

public readonly record struct BodyId(long Value)
{
    public override string ToString() => $"body-{Value}";
}

public readonly record struct WoundId(int Value)
{
    public override string ToString() => $"wound-{Value}";
}

public enum CombatError
{
    InvalidDamage,
    InvalidProtection,
    InvalidDuration,
    AlreadyDead,
    PartMissing,
    NothingToBandage,
    NothingToTreat,
    UnknownWoundKind,
}

public enum DeathCause
{
    Trauma,
    BloodLoss,
}

/// <summary>Read-only view of a Wound on a Body part. <paramref name="Kind"/> is the Content ID of its Wound kind, or null for a Wound that has none.</summary>
public sealed record Wound(WoundId Id, BodyPart Part, DamageType Type, double Severity, VolumeFlow BleedRate, bool IsBandaged, bool IsStump, string? Kind = null)
{
    public bool IsBleeding => !IsBandaged && BleedRate > VolumeFlow.Zero;
}

/// <summary>Tuning for a Body. Defaults model a healthy adult.</summary>
public sealed record BodyConfig
{
    public double PartHealth { get; init; } = 100;

    public Volume BloodVolume { get; init; } = Volume.FromLiters(5);

    /// <summary>Blood volume at or below which the body dies.</summary>
    public Volume LethalBloodVolume { get; init; } = Volume.FromLiters(2);

    /// <summary>Bleed rate of the stump left when a body part is lost.</summary>
    public VolumeFlow StumpBleedRate { get; init; } = VolumeFlow.FromMillilitersPerMinute(30);

    /// <summary>
    /// The Wound kinds that damage causes. With none, Wounds have no kind, bleed by <see cref="BleedPerDamage"/>, and never
    /// heal or worsen.
    /// </summary>
    public WoundKindCatalog? WoundKinds { get; init; }

    /// <summary>Seed that decides which Wounds worsen. The same seed and the same Wounds always give the same outcome.</summary>
    public ulong WoundSeed { get; init; }

    /// <summary>Bleed rate in millilitres per minute per point of damage, by damage type.</summary>
    public IReadOnlyDictionary<DamageType, double> BleedPerDamage { get; init; } = new Dictionary<DamageType, double>
    {
        [DamageType.Blunt] = 0.5,
        [DamageType.Cut] = 4,
        [DamageType.Pierce] = 3,
        [DamageType.Bite] = 5,
    };
}

public sealed record DamageTaken(BodyId Body, BodyPart Part, DamageType Type, double Damage) : IDomainEvent;

public sealed record DamageAbsorbed(BodyId Body, BodyPart Part, DamageType Type) : IDomainEvent;

public sealed record WoundCreated(BodyId Body, WoundId Wound, BodyPart Part, DamageType Type, VolumeFlow BleedRate) : IDomainEvent;

public sealed record BodyPartLost(BodyId Body, BodyPart Part) : IDomainEvent;

public sealed record WoundsBandaged(BodyId Body, BodyPart Part, int Count) : IDomainEvent;

public sealed record WoundsTreated(BodyId Body, BodyPart Part, string Treatment, int Removed, int Bandaged, int Added) : IDomainEvent;

public sealed record WoundWorsened(BodyId Body, WoundId Was, WoundId Now, BodyPart Part, string Kind) : IDomainEvent;

public sealed record WoundHealed(BodyId Body, WoundId Wound, BodyPart Part) : IDomainEvent;

public sealed record BloodLost(BodyId Body, Volume Amount, Volume Remaining) : IDomainEvent;

public sealed record BodyDied(BodyId Body, DeathCause Cause) : IDomainEvent;

/// <summary>Outcome of a Domain command: either an error with no state change, or the events raised.</summary>
public sealed class CombatResult
{
    private static readonly IDomainEvent[] NoEvents = [];

    private CombatResult(CombatError? error, IReadOnlyList<IDomainEvent> events)
    {
        Error = error;
        Events = events;
    }

    public bool IsSuccess => Error is null;

    public CombatError? Error { get; }

    public IReadOnlyList<IDomainEvent> Events { get; }

    public static CombatResult Success(IReadOnlyList<IDomainEvent> events) => new(null, events);

    public static CombatResult Failure(CombatError error) => new(error, NoEvents);
}
