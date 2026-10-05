namespace Zombies.Engine.Net;

/// <summary>
/// Loots a Corpse: the Server moves what the sender has room for from the Corpse's Container into what they carry, with Item
/// state intact. Any player may, the dead player's own next life included, from within <see cref="Reach"/>.
/// </summary>
/// <param name="Corpse">The id of the Corpse entity, as snapshots show it.</param>
public readonly record struct LootCorpse(uint Corpse) : INetCommand<LootCorpse>
{
    /// <summary>The farthest from a Corpse a player may loot it, in blocks.</summary>
    public const float Reach = 3f;

    public static ushort CommandId => 20;

    public static LootCorpse Read(ref NetReader reader) => new(reader.ReadVarUInt());

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt(Corpse);
    }

    internal static CommandResult Handle(in LootCorpse command, CommandContext context) => context.Server.Loot(context.Player, command.Corpse);
}
