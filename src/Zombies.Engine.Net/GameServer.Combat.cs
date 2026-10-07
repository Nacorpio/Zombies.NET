using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Zombies;
using Zombies.Engine.Core;

namespace Zombies.Engine.Net;

/// <summary>A <see cref="IContainerRepository"/> over the one Container a player carries, so Inventory commands can change it in place.</summary>
internal sealed class SingleContainerRepository(Container container) : IContainerRepository
{
    public bool TryGet(ContainerId id, out Container found)
    {
        found = container;
        return id == container.Id;
    }

    public bool TryAdd(Container added) => false;

    public void Save(Container saved)
    {
    }

    public IReadOnlyList<ContainerId> Ids() => [container.Id];
}

public sealed partial class GameServer
{
    /// <summary>The zombies a player's weapon can hit. Null on a Server with none, where a weapon hits nothing.</summary>
    public ZombieSystem? Zombies { get; set; }

    internal CommandResult Hold(PlayerSession session, ItemId? item)
    {
        if (item is not { } wanted)
        {
            session.Held = null;
            session.HeldState = null;
            session.StatusDirty = true;
            return CommandResult.Accepted;
        }

        if (Options.Weapons is null || !Options.Weapons.TryGetWeapon(wanted, out _))
        {
            return CommandResult.Invalid($"'{wanted}' is not a weapon.");
        }

        var stack = session.Carried.Stacks.FirstOrDefault(s => s.Item == wanted);
        if (stack is null)
        {
            return CommandResult.Invalid($"The player does not carry '{wanted}'.");
        }

        session.Held = wanted;
        session.HeldState = stack.State;
        session.StatusDirty = true;
        return CommandResult.Accepted;
    }

    internal CommandResult Load(PlayerSession session)
    {
        if (CheckHeld(session) is { } problem)
        {
            return problem;
        }

        Options.Weapons!.TryGetWeapon(session.Held!.Value, out var definition);
        if (definition.AmmoItem is not { } ammo)
        {
            return CommandResult.Invalid("This weapon needs no ammo.");
        }

        var stack = session.Carried.Stacks.FirstOrDefault(s => s.Item == ammo);
        var count = session.Carried.CountOf(ammo);
        if (stack is null || count < 1)
        {
            return CommandResult.Invalid($"The player carries no '{ammo}'.");
        }

        var loaded = (session.HeldState ?? ItemState.Create()).With(WeaponService.RoundsValue, WeaponService.RoundsOf(session.HeldState) + count);
        if (!session.Inventory.RemoveItems(session.Carried.Id, ammo, count, stack.State).IsSuccess)
        {
            return CommandResult.Invalid("The ammo could not be taken from what the player carries.");
        }

        return Replace(session, loaded) ? CommandResult.Accepted : CommandResult.Invalid("The weapon could not be updated.");
    }

    /// <summary>
    /// Uses the Held weapon once. A firearm spends a round and shoots along the look ray up to its range; a melee weapon spends no
    /// round and strikes only within its reach. Either way the Weapons domain decides the damage and the Condition it loses, and a
    /// Broken weapon or an empty firearm is refused with nothing changed.
    /// </summary>
    internal CommandResult Use(PlayerSession session)
    {
        if (CheckHeld(session) is { } problem)
        {
            return problem;
        }

        var item = session.Held!.Value;
        if (_tick < session.NextAttackTick)
        {
            return CommandResult.Invalid("The weapon is not ready to be used again.");
        }

        _weapons!.TryGetEffectiveStats(item, session.HeldState, out var stats);
        Options.Weapons!.TryGetWeapon(item, out var definition);
        var used = _weapons.Use(item, session.HeldState);
        if (!used.IsSuccess)
        {
            return CommandResult.Invalid(used.Error switch
            {
                WeaponError.Broken => "The weapon is broken.",
                WeaponError.OutOfAmmo => "The weapon is out of ammo.",
                _ => "The weapon cannot be used.",
            });
        }

        if (!Replace(session, used.State!))
        {
            return CommandResult.Invalid("The weapon could not be updated.");
        }

        session.NextAttackTick = _tick + (long)Math.Ceiling(Simulation.TickRateHz / Math.Max(stats.RateOfFire, 0.01));
        var weapon = used.Events.OfType<WeaponUsed>().Single();
        var yaw = session.Movement.Yaw;
        var pitch = session.Movement.Pitch;
        var direction = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
        var origin = PlayerMovement.EyePosition(session.Movement);
        var attack = definition.AmmoItem is null ? AttackKind.Melee : AttackKind.Ranged;
        if (Zombies?.Hit(origin, direction, (float)stats.Reach, weapon.DamageType, weapon.Damage, attack) is { Killed: true })
        {
            session.CreditKill();
        }

        return CommandResult.Accepted;
    }

    internal CommandResult Treat(PlayerSession session, BodyPart part, string treatmentId)
    {
        if (Options.Treatments is null || !Options.Treatments.TryGet(treatmentId, out var treatment))
        {
            return CommandResult.Invalid($"There is no Treatment '{treatmentId}'.");
        }

        var stack = session.Carried.Stacks.FirstOrDefault(s => s.Item == treatment.Consumes);
        if (stack is null)
        {
            return CommandResult.Invalid($"The player carries no '{treatment.Consumes}'.");
        }

        if (!session.Body.CanTreat(part, treatment))
        {
            return CommandResult.Invalid($"'{treatmentId}' would do nothing to the {part}.");
        }

        if (!session.Body.Treat(part, treatment).IsSuccess)
        {
            return CommandResult.Invalid($"'{treatmentId}' cannot be applied to the {part}.");
        }

        session.Inventory.RemoveItems(session.Carried.Id, treatment.Consumes, 1, stack.State);
        session.StatusDirty = true;
        return CommandResult.Accepted;
    }

    /// <summary>Why the Held weapon cannot be used or loaded, or null when it can: it must exist and still be carried.</summary>
    private CommandResult? CheckHeld(PlayerSession session)
    {
        if (Options.Weapons is null || _weapons is null)
        {
            return CommandResult.Invalid("This Server has no weapons.");
        }

        if (session.Held is not { } item)
        {
            return CommandResult.Invalid("The player holds no weapon.");
        }

        if (session.Carried.CountOf(item, session.HeldState) < 1)
        {
            session.Held = null;
            session.HeldState = null;
            session.StatusDirty = true;
            return CommandResult.Invalid("The player no longer carries the weapon they held.");
        }

        return null;
    }

    /// <summary>Gives the Held weapon new Item state, in the Stack that holds it.</summary>
    private static bool Replace(PlayerSession session, ItemState state)
    {
        var item = session.Held!.Value;
        var old = session.HeldState;
        var id = session.Carried.Id;
        if (!session.Inventory.RemoveItems(id, item, 1, old).IsSuccess)
        {
            return false;
        }

        if (!session.Inventory.AddItems(id, item, 1, state).IsSuccess)
        {
            session.Inventory.AddItems(id, item, 1, old);
            return false;
        }

        session.HeldState = state;
        session.StatusDirty = true;
        return true;
    }

    /// <summary>Tells a player their own Body and Held weapon, which no snapshot carries because only they see them.</summary>
    private void SendStatus(PlayerSession session)
    {
        if (!session.StatusDirty)
        {
            return;
        }

        // A bleeding Body changes every tick, so it keeps being sent; an unhurt one is sent again only when something changes.
        session.StatusDirty = session.Body.HasWounds;
        _writer.Clear();
        _writer.WriteByte((byte)MessageType.PlayerStatus);
        PlayerStatusCodec.Write(_writer, session.Body.ToSnapshot(), session.Held, session.HeldState);
        _transport.Send(session.Connection, _writer.Written, Delivery.ReliableOrdered);
    }
}
