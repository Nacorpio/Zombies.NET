using System.Numerics;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.World;
using Zombies.Engine.Core;

namespace Zombies.Engine.Net;

public sealed partial class GameServer
{
    private readonly Dictionary<int, ContainerId> _openContainers = [];
    private readonly Dictionary<ContainerId, WorldContainer> _worldContainers = [];
    private readonly Dictionary<ContainerId, Container> _openedContainers = [];
    private readonly HashSet<int> _containersDirty = [];

    /// <summary>Where the Server finds the world's Containers. Defaults to a world with none.</summary>
    public IWorldContainerSource WorldContainers { get; set; } = NoWorldContainers.Instance;

    /// <summary>Fills a world Container from its Area type the first time it is opened. Null means no Container can be filled.</summary>
    public WorldContainerOpener? ContainerOpener { get; set; }

    /// <summary>How far a player can reach to open a Container, in blocks.</summary>
    public float ContainerReach { get; set; } = ContainerTargeting.DefaultReach;

    /// <summary>Opens the world Container at a block, filling it the first time, and marks the sender's view changed.</summary>
    internal CommandResult OpenWorldContainer(PlayerSession session, int x, int y, int z)
    {
        if (ContainerOpener is null)
        {
            return CommandResult.Invalid("This Server has no world Containers.");
        }

        if (!World.TryGet(session.EntityId, out var player))
        {
            return CommandResult.Invalid("The player has no entity.");
        }

        var eye = PlayerMovement.EyePosition(session.Movement);
        var direction = LookDirection(session.Movement);
        var near = WorldContainers.Near(eye, ContainerReach + 1f);
        var target = ContainerTargeting.Find(near, eye, direction, ContainerReach);
        if (target is null || target.X != x || target.Y != y || target.Z != z)
        {
            return CommandResult.Invalid("There is no Container there to open.");
        }

        var opened = ContainerOpener.Open(target);
        if (!opened.IsSuccess)
        {
            return CommandResult.Invalid(opened.Error switch
            {
                AreaLootError.NoRuleForContainerKind => $"Nothing is stored in a {target.AreaType} {target.ContainerKind}.",
                AreaLootError.UnknownAreaType => $"The {target.AreaType} area has no loot rules.",
                AreaLootError.NoTableAvailable => $"The {target.AreaType} {target.ContainerKind} has no loot table.",
                _ => "The Container could not be opened.",
            });
        }

        var id = opened.Container!.Id;
        _worldContainers[id] = target;
        _openedContainers[id] = opened.Container;
        _openContainers[session.Connection.Value] = id;
        _containersDirty.Add(session.Connection.Value);
        return CommandResult.Accepted;
    }

    /// <summary>Closes the world Container the sender has open.</summary>
    internal CommandResult CloseWorldContainer(PlayerSession session, ContainerId container)
    {
        if (_openContainers.TryGetValue(session.Connection.Value, out var open) && open == container)
        {
            _openContainers.Remove(session.Connection.Value);
            _containersDirty.Add(session.Connection.Value);
        }

        return CommandResult.Accepted;
    }

    /// <summary>Moves a Stack between the sender's own Container and one they have open, or between two open ones.</summary>
    internal CommandResult MoveStack(PlayerSession session, ContainerId from, StackId stack, ContainerId to, int count)
    {
        if (count < 1)
        {
            return CommandResult.Invalid("A move needs a positive count.");
        }

        if (!TryResolveContainer(session, from, out var source) || !TryResolveContainer(session, to, out var destination))
        {
            return CommandResult.Invalid("That Container is not yours to move things in or out of.");
        }

        var result = session.Inventory.MoveItems(source.Id, stack, destination.Id, count);
        if (!result.IsSuccess)
        {
            return CommandResult.Invalid($"The Stack could not be moved: {result.Error}.");
        }

        MarkContainersDirty(session);
        return CommandResult.Accepted;
    }

    /// <summary>Splits a Stack in a Container the sender owns or has open.</summary>
    internal CommandResult SplitStack(PlayerSession session, ContainerId container, StackId stack, int count)
    {
        if (count < 1)
        {
            return CommandResult.Invalid("A split needs a positive count.");
        }

        if (!TryResolveContainer(session, container, out var resolved))
        {
            return CommandResult.Invalid("That Container is not yours to split in.");
        }

        var result = session.Inventory.SplitStack(resolved.Id, stack, count);
        if (!result.IsSuccess)
        {
            return CommandResult.Invalid($"The Stack could not be split: {result.Error}.");
        }

        MarkContainersDirty(session);
        return CommandResult.Accepted;
    }

    /// <summary>Drops a Stack from the sender's own Container onto the ground where they stand.</summary>
    internal CommandResult DropStack(PlayerSession session, ContainerId container, StackId stack, int count)
    {
        if (count < 1)
        {
            return CommandResult.Invalid("A drop needs a positive count.");
        }

        if (container != session.Carried.Id)
        {
            return CommandResult.Invalid("Only what you carry can be dropped.");
        }

        var result = session.Inventory.RemoveItems(session.Carried.Id, StackItem(session.Carried, stack), count, StackState(session.Carried, stack));
        if (!result.IsSuccess)
        {
            return CommandResult.Invalid($"The Stack could not be dropped: {result.Error}.");
        }

        MarkContainersDirty(session);
        return CommandResult.Accepted;
    }

    /// <summary>The Containers a client can see: the one it carries, and any world Container it has open.</summary>
    private ContainerView ViewFor(PlayerSession session)
    {
        var containers = new List<ReplicatedContainer>
        {
            Describe(session.Carried, "inv.backpack"),
        };

        if (_openContainers.TryGetValue(session.Connection.Value, out var open) && _openedContainers.TryGetValue(open, out var world))
        {
            var title = _worldContainers.TryGetValue(open, out var source) ? $"{source.AreaType} {source.ContainerKind}" : "inv.ground";
            containers.Add(Describe(world, title));
        }

        return new ContainerView(containers);
    }

    private static ReplicatedContainer Describe(Container container, string title) => new(
        container.Id,
        title,
        container.Stacks,
        container.MassLimit.Kilograms <= 0 ? 0 : container.TotalMass.Kilograms / container.MassLimit.Kilograms,
        container.VolumeLimit.CubicMeters <= 0 ? 0 : container.TotalVolume.CubicMeters / container.VolumeLimit.CubicMeters);

    private bool TryResolveContainer(PlayerSession session, ContainerId id, out Container container)
    {
        if (id == session.Carried.Id)
        {
            container = session.Carried;
            return true;
        }

        if (_openContainers.TryGetValue(session.Connection.Value, out var open) && open == id && _openedContainers.TryGetValue(id, out var world))
        {
            container = world;
            return true;
        }

        container = null!;
        return false;
    }

    /// <summary>
    /// The Containers a player's Inventory commands may touch: the one they carry and any world Container they have open. The
    /// Inventory service reads and writes through this, so a move between the two is one validated operation.
    /// </summary>
    private sealed class SessionContainers(PlayerSession session, GameServer server) : IContainerRepository
    {
        public bool TryGet(ContainerId id, out Container found) => server.TryResolveContainer(session, id, out found);

        public bool TryAdd(Container added) => false;

        public void Save(Container saved)
        {
        }

        public IReadOnlyList<ContainerId> Ids()
        {
            var ids = new List<ContainerId> { session.Carried.Id };
            if (server._openContainers.TryGetValue(session.Connection.Value, out var open))
            {
                ids.Add(open);
            }

            return ids;
        }
    }

    /// <summary>Marks a client's Container view as changed, so the next tick sends it the Server's current view.</summary>
    private void MarkContainersDirty(PlayerSession session) => _containersDirty.Add(session.Connection.Value);

    private static ItemId StackItem(Container container, StackId stack) =>
        container.Stacks.FirstOrDefault(s => s.Id == stack)?.Item ?? throw new InvalidOperationException($"No Stack {stack.Value} in the Container.");

    private static ItemState? StackState(Container container, StackId stack) =>
        container.Stacks.FirstOrDefault(s => s.Id == stack)?.State;

    private static Vector3 LookDirection(in PlayerMoveState movement)
    {
        var yaw = movement.Yaw;
        var pitch = movement.Pitch;
        return new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
    }

    /// <summary>Sends the Container view to every client whose view changed, and clears the dirty marks.</summary>
    private void SendContainerViews()
    {
        if (_containersDirty.Count == 0)
        {
            return;
        }

        foreach (var connection in _containersDirty)
        {
            if (!_sessions.TryGetValue(connection, out var session) || !session.IsJoined)
            {
                continue;
            }

            _writer.Clear();
            _writer.WriteByte((byte)MessageType.ContainerView);
            ContainerViewCodec.Write(_writer, ViewFor(session));
            _transport.Send(session.Connection, _writer.Written, Delivery.ReliableOrdered);
        }

        _containersDirty.Clear();
    }
}
