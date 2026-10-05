namespace Zombies.Engine.Net;

/// <summary>
/// Goes to sleep or gets up. The Server decides whether the sender may: they must be tired enough and standing still to fall
/// asleep, and where they are sets how well they rest. Asleep, they cannot act, and everyone who sees them is told.
/// </summary>
/// <param name="Asleep">True to go to sleep, false to wake.</param>
public readonly record struct SleepCommand(bool Asleep) : INetCommand<SleepCommand>
{
    /// <summary>The fastest a player may move, in blocks per second, and still lie down.</summary>
    public const float MaxSpeedToSleep = 0.5f;

    public static ushort CommandId => 21;

    public static SleepCommand Read(ref NetReader reader) => new(reader.ReadBool());

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBool(Asleep);
    }

    internal static CommandResult Handle(in SleepCommand command, CommandContext context) =>
        command.Asleep ? context.Server.Sleep(context.Player) : context.Server.Wake(context.Player);
}
