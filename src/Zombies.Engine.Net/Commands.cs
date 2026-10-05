using System.Numerics;

namespace Zombies.Engine.Net;

/// <summary>
/// A Domain command as it travels from a client to the Server. Each command type has a unique id and reads and writes
/// its own payload. The Server validates every command before it changes anything.
/// </summary>
public interface INetCommand<TSelf>
    where TSelf : INetCommand<TSelf>
{
    static abstract ushort CommandId { get; }

    void Write(NetWriter writer);

    static abstract TSelf Read(ref NetReader reader);
}

/// <summary>Why the Server rejected a Domain command.</summary>
public enum CommandRejection : byte
{
    /// <summary>No handler is registered for the command id.</summary>
    UnknownCommand = 1,

    /// <summary>The payload could not be read.</summary>
    Malformed,

    /// <summary>The command was read but breaks a rule, such as moving farther than a step.</summary>
    Invalid,

    /// <summary>The sender's Body is dead, so they are spectating and cannot act.</summary>
    Dead,
}

/// <summary>The outcome of one Domain command on the Server.</summary>
public readonly record struct CommandResult(CommandRejection? Rejection, string? Detail)
{
    public static CommandResult Accepted => default;

    public bool IsAccepted => Rejection is null;

    public static CommandResult Invalid(string detail) => new(CommandRejection.Invalid, detail);
}

/// <summary>The player who sent a command and the Server it runs on.</summary>
public readonly record struct CommandContext(GameServer Server, PlayerSession Player);

/// <summary>Validates and applies one Domain command. Validation must come first: a rejected command changes nothing.</summary>
public delegate CommandResult CommandHandler<TCommand>(in TCommand command, CommandContext context);

/// <summary>The Domain commands a Server accepts, by command id.</summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<ushort, IEntry> _handlers = [];

    private interface IEntry
    {
        CommandResult Handle(ref NetReader payload, CommandContext context);
    }

    public void Register<TCommand>(CommandHandler<TCommand> handler)
        where TCommand : INetCommand<TCommand>
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(TCommand.CommandId, new Entry<TCommand>(handler)))
        {
            throw new InvalidOperationException($"Command id {TCommand.CommandId} is already registered.");
        }
    }

    internal CommandResult Handle(ushort commandId, ref NetReader payload, CommandContext context)
    {
        if (!_handlers.TryGetValue(commandId, out var entry))
        {
            return new CommandResult(CommandRejection.UnknownCommand, $"No command has id {commandId}.");
        }

        try
        {
            return entry.Handle(ref payload, context);
        }
        catch (MalformedMessageException ex)
        {
            return new CommandResult(CommandRejection.Malformed, ex.Message);
        }
    }

    private sealed class Entry<TCommand>(CommandHandler<TCommand> handler) : IEntry
        where TCommand : INetCommand<TCommand>
    {
        public CommandResult Handle(ref NetReader payload, CommandContext context)
        {
            var command = TCommand.Read(ref payload);
            payload.EnsureEnd();
            return handler(in command, context);
        }
    }
}

/// <summary>
/// Moves the sender's player to a position and yaw. A stand-in until the predicted, input-driven player of M4 replaces it;
/// the Server only checks that the step is short and the numbers are real.
/// </summary>
public readonly record struct MovePlayer(Vector3 Position, float Yaw) : INetCommand<MovePlayer>
{
    /// <summary>The farthest one command may move a player, in blocks.</summary>
    public const float MaxStep = 2f;

    public static ushort CommandId => 1;

    public static MovePlayer Read(ref NetReader reader) => new(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle());

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteSingle(Yaw);
    }

    internal static CommandResult Handle(in MovePlayer command, CommandContext context)
    {
        var p = command.Position;
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || !float.IsFinite(command.Yaw))
        {
            return CommandResult.Invalid("Position and yaw must be finite numbers.");
        }

        var world = context.Server.World;
        var id = context.Player.EntityId;
        if (!world.TryGet(id, out var current))
        {
            return CommandResult.Invalid("The player has no entity.");
        }

        if (Vector3.DistanceSquared(current.Position, p) > MaxStep * MaxStep)
        {
            return CommandResult.Invalid($"A move may cover at most {MaxStep} blocks.");
        }

        world.Move(id, p, command.Yaw);
        return CommandResult.Accepted;
    }
}

/// <summary>
/// One tick of player input. The Server runs the same <see cref="PlayerMovement"/> step the client predicted with, so the
/// client's position is confirmed rather than corrected. The Server only checks that the input is sane and that the
/// resulting step is short enough to be a step and not a teleport.
/// </summary>
public readonly record struct PlayerInputCommand(PlayerInput Input) : INetCommand<PlayerInputCommand>
{
    public static ushort CommandId => 2;

    public static PlayerInputCommand Read(ref NetReader reader)
    {
        var axes = reader.ReadByte();
        var forward = ((axes >> 4) & 0x7) - 3;
        var strafe = (axes & 0x7) - 3;
        var flags = reader.ReadByte();
        return new PlayerInputCommand(new PlayerInput(
            forward / 3f,
            strafe / 3f,
            reader.ReadSingle(),
            reader.ReadSingle(),
            (flags & 1) != 0,
            (flags & 2) != 0,
            (flags & 4) != 0,
            (flags & 8) != 0,
            (flags & 16) != 0));
    }

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var forward = (int)MathF.Round(Math.Clamp(Input.Forward, -1f, 1f) * 3f) + 3;
        var strafe = (int)MathF.Round(Math.Clamp(Input.Strafe, -1f, 1f) * 3f) + 3;
        writer.WriteByte((byte)((forward << 4) | strafe));
        writer.WriteByte((byte)(
            (Input.Sprint ? 1 : 0)
            | (Input.Crouch ? 2 : 0)
            | (Input.LeanLeft ? 4 : 0)
            | (Input.LeanRight ? 8 : 0)
            | (Input.Jump ? 16 : 0)));
        writer.WriteSingle(Input.Yaw);
        writer.WriteSingle(Input.Pitch);
    }

    internal static CommandResult Handle(in PlayerInputCommand command, CommandContext context)
    {
        if (!command.Input.IsValid)
        {
            return CommandResult.Invalid("The input axes must be finite and within -1 to 1.");
        }

        var world = context.Server.World;
        var id = context.Player.EntityId;
        if (!world.TryGet(id, out var current))
        {
            return CommandResult.Invalid("The player has no entity.");
        }

        var input = command.Input.Sanitized();
        var state = context.Player.Movement;
        var next = PlayerMovement.Step(state, input, PlayerMovement.StepSeconds, context.Player.Collision);
        if (Vector3.DistanceSquared(state.Position, next.Position) > PlayerMovement.MaxStepPerTick * PlayerMovement.MaxStepPerTick)
        {
            return CommandResult.Invalid($"A step may cover at most {PlayerMovement.MaxStepPerTick} blocks.");
        }

        context.Player.Movement = next;
        world.Move(id, next.Position, next.Yaw);
        context.Server.Walked(context.Player, state.Position, next.Position);
        return CommandResult.Accepted;
    }
}
