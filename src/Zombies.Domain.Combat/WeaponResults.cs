using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

public enum WeaponError
{
    UnknownWeapon,
    UnknownAttachment,
    MountNotOffered,
    MountOccupied,
    NotFitted,
    Broken,
    OutOfAmmo,

    /// <summary>The Attachment belongs on a different Mount from the one it was dropped on.</summary>
    WrongMount,
}

public sealed record AttachmentFitted(ItemId Weapon, ItemId Attachment, string Mount) : IDomainEvent;

public sealed record AttachmentRemoved(ItemId Weapon, ItemId Attachment, string Mount) : IDomainEvent;

/// <summary>A weapon was used. The damage and damage type are what to hand to the Body being hit.</summary>
public sealed record WeaponUsed(ItemId Weapon, double Damage, DamageType DamageType, double Noise, int Condition, int Rounds) : IDomainEvent;

public sealed record WeaponBroke(ItemId Weapon) : IDomainEvent;

/// <summary>Outcome of a weapon command: either an error with the state unchanged, or the new Item state and the events raised.</summary>
public sealed class WeaponResult
{
    private static readonly IDomainEvent[] NoEvents = [];

    private WeaponResult(WeaponError? error, ItemState? state, IReadOnlyList<IDomainEvent> events)
    {
        Error = error;
        State = state;
        Events = events;
    }

    public bool IsSuccess => Error is null;

    public WeaponError? Error { get; }

    /// <summary>The weapon's Item state after the command. Store it on the Stack that holds the weapon.</summary>
    public ItemState? State { get; }

    public IReadOnlyList<IDomainEvent> Events { get; }

    public static WeaponResult Success(ItemState state, params IDomainEvent[] events) => new(null, state, events);

    public static WeaponResult Failure(WeaponError error) => new(error, null, NoEvents);
}

/// <summary>A weapon's stats after Attachments and Condition are applied.</summary>
public sealed record WeaponStats(double Damage, double RateOfFire, double Reach, double Handling, double Noise, int HandsNeeded);
