using Zombies.Domain.Items;

namespace Zombies.Engine.Net;

/// <summary>
/// Takes a weapon the sender carries into their hands, or empties the hands when <see cref="Item"/> is null. The Server
/// only checks that the sender carries it and that it is a weapon; the Item state it holds is the Server's own.
/// </summary>
public readonly record struct HoldWeapon(ItemId? Item) : INetCommand<HoldWeapon>
{
    public static ushort CommandId => 10;

    public static HoldWeapon Read(ref NetReader reader)
    {
        var value = reader.ReadString();
        if (value.Length == 0)
        {
            return new HoldWeapon(null);
        }

        return ItemId.TryParse(value, out var item) ? new HoldWeapon(item) : throw new MalformedMessageException($"'{value}' is not a Content ID.");
    }

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Item?.Value ?? string.Empty);
    }

    internal static CommandResult Handle(in HoldWeapon command, CommandContext context) => context.Server.Hold(context.Player, command.Item);
}

/// <summary>
/// Asks to use the Held weapon once, along the way the sender is looking. It names no target: the Server finds what the ray hits,
/// spends the round and the Condition, and decides the damage.
/// </summary>
public readonly record struct UseWeapon : INetCommand<UseWeapon>
{
    public static ushort CommandId => 11;

    public static UseWeapon Read(ref NetReader reader) => default;

    public void Write(NetWriter writer) => ArgumentNullException.ThrowIfNull(writer);

    internal static CommandResult Handle(in UseWeapon command, CommandContext context) => context.Server.Use(context.Player);
}

/// <summary>Puts every round of the weapon's ammo item the sender carries into the Held weapon. There are no magazines.</summary>
public readonly record struct LoadWeapon : INetCommand<LoadWeapon>
{
    public static ushort CommandId => 12;

    public static LoadWeapon Read(ref NetReader reader) => default;

    public void Write(NetWriter writer) => ArgumentNullException.ThrowIfNull(writer);

    internal static CommandResult Handle(in LoadWeapon command, CommandContext context) => context.Server.Load(context.Player);
}

/// <summary>Applies a Treatment, such as a bandage, to one of the sender's own body parts, using up the Item it consumes.</summary>
public readonly record struct TreatWounds(BodyPart Part, string Treatment) : INetCommand<TreatWounds>
{
    public static ushort CommandId => 13;

    public static TreatWounds Read(ref NetReader reader)
    {
        var part = reader.ReadByte();
        return Enum.IsDefined((BodyPart)part) ? new TreatWounds((BodyPart)part, reader.ReadString()) : throw new MalformedMessageException($"{part} is not a body part.");
    }

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte((byte)Part);
        writer.WriteString(Treatment);
    }

    internal static CommandResult Handle(in TreatWounds command, CommandContext context) => context.Server.Treat(context.Player, command.Part, command.Treatment);
}
