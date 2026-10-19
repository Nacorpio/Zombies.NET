using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Engine.Net;

/// <summary>
/// Opens the world Container the sender is looking at. The client names the block; the Server finds the Container there, fills
/// it from the room's Area type the first time, and answers with the Container's contents. A block with no Container, or one
/// whose Area type has no loot rule, is refused with the reason.
/// </summary>
public readonly record struct OpenContainer(int X, int Y, int Z) : INetCommand<OpenContainer>
{
    public static ushort CommandId => 14;

    public static OpenContainer Read(ref NetReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(X);
        writer.WriteInt32(Y);
        writer.WriteInt32(Z);
    }

    internal static CommandResult Handle(in OpenContainer command, CommandContext context) =>
        context.Server.OpenWorldContainer(context.Player, command.X, command.Y, command.Z);
}

/// <summary>
/// Moves a Stack between the sender's own Container and a Container they have open, or splits it. The Server validates the move
/// against the Domain and answers with the new view, so the client's screen follows the Server rather than guessing.
/// </summary>
public readonly record struct MoveStackCommand(ContainerId From, StackId Stack, ContainerId To, int Count) : INetCommand<MoveStackCommand>
{
    public static ushort CommandId => 15;

    public static MoveStackCommand Read(ref NetReader reader) =>
        new(new ContainerId((long)reader.ReadUInt64()), new StackId(reader.ReadInt32()), new ContainerId((long)reader.ReadUInt64()), reader.ReadInt32());

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64((ulong)From.Value);
        writer.WriteInt32(Stack.Value);
        writer.WriteUInt64((ulong)To.Value);
        writer.WriteInt32(Count);
    }

    internal static CommandResult Handle(in MoveStackCommand command, CommandContext context) =>
        context.Server.MoveStack(context.Player, command.From, command.Stack, command.To, command.Count);
}

/// <summary>Splits a Stack in a Container the sender owns or has open, leaving the original and creating a new Stack of <see cref="Count"/>.</summary>
public readonly record struct SplitStackCommand(ContainerId Container, StackId Stack, int Count) : INetCommand<SplitStackCommand>
{
    public static ushort CommandId => 16;

    public static SplitStackCommand Read(ref NetReader reader) =>
        new(new ContainerId((long)reader.ReadUInt64()), new StackId(reader.ReadInt32()), reader.ReadInt32());

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64((ulong)Container.Value);
        writer.WriteInt32(Stack.Value);
        writer.WriteInt32(Count);
    }

    internal static CommandResult Handle(in SplitStackCommand command, CommandContext context) =>
        context.Server.SplitStack(context.Player, command.Container, command.Stack, command.Count);
}

/// <summary>Drops a Stack from the sender's own Container onto the ground where they stand.</summary>
public readonly record struct DropStackCommand(ContainerId Container, StackId Stack, int Count) : INetCommand<DropStackCommand>
{
    public static ushort CommandId => 17;

    public static DropStackCommand Read(ref NetReader reader) =>
        new(new ContainerId((long)reader.ReadUInt64()), new StackId(reader.ReadInt32()), reader.ReadInt32());

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64((ulong)Container.Value);
        writer.WriteInt32(Stack.Value);
        writer.WriteInt32(Count);
    }

    internal static CommandResult Handle(in DropStackCommand command, CommandContext context) =>
        context.Server.DropStack(context.Player, command.Container, command.Stack, command.Count);
}

/// <summary>Closes the world Container the sender has open, so the Server stops sending it.</summary>
public readonly record struct CloseContainer(ContainerId Container) : INetCommand<CloseContainer>
{
    public static ushort CommandId => 18;

    public static CloseContainer Read(ref NetReader reader) => new(new ContainerId((long)reader.ReadUInt64()));

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64((ulong)Container.Value);
    }

    internal static CommandResult Handle(in CloseContainer command, CommandContext context) =>
        context.Server.CloseWorldContainer(context.Player, command.Container);
}
