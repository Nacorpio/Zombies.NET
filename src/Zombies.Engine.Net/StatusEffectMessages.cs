using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;

namespace Zombies.Engine.Net;

/// <summary>
/// The wire form of the Status effects on one player. The Server sends it to that player alone (never in a snapshot, which
/// other players receive) each time their effects change; the client keeps the newest list.
/// </summary>
internal static class StatusEffectMessages
{
    /// <summary>More effects than this in one message means a broken or hostile Server.</summary>
    public const int MaxEffects = 64;

    private const float NoRemaining = -1f;

    public static void Write(NetWriter writer, IReadOnlyList<ActiveEffect> effects)
    {
        writer.WriteByte((byte)MessageType.StatusEffects);
        writer.WriteVarUInt((uint)effects.Count);
        foreach (var effect in effects)
        {
            writer.WriteString(effect.Effect);
            writer.WriteVarUInt((uint)effect.Stacks);
            writer.WriteString(effect.Stage ?? string.Empty);
            writer.WriteSingle((float)effect.Elapsed.TotalSeconds);
            writer.WriteSingle(effect.Remaining is { } remaining ? (float)remaining.TotalSeconds : NoRemaining);
        }
    }

    /// <summary>Reads the effects after the message type.</summary>
    public static IReadOnlyList<ActiveEffect> Read(ref NetReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > MaxEffects)
        {
            throw new MalformedMessageException($"A message lists {count} Status effects; at most {MaxEffects} are allowed.");
        }

        var effects = new List<ActiveEffect>((int)count);
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadString();
            if (!ItemId.TryParse(id, out _))
            {
                throw new MalformedMessageException($"'{id}' is not a Content ID.");
            }

            var stacks = reader.ReadVarUInt();
            var stage = reader.ReadString();
            var elapsed = reader.ReadSingle();
            var remaining = reader.ReadSingle();
            if (stacks is 0 or > 1000 || !float.IsFinite(elapsed) || !float.IsFinite(remaining))
            {
                throw new MalformedMessageException($"The Status effect '{id}' has impossible numbers.");
            }

            effects.Add(new ActiveEffect(
                id,
                (int)stacks,
                TimeSpan.FromSeconds(Math.Max(0, elapsed)),
                remaining < 0 ? null : TimeSpan.FromSeconds(remaining),
                stage.Length == 0 ? null : stage));
        }

        reader.EnsureEnd();
        return effects;
    }
}

/// <summary>
/// Uses an Item the sender carries: eating, drinking, or taking it. The Server checks that the Item is consumable and that the
/// sender has one, then removes it and applies and cures the Status effects its data says. The command names only the Item;
/// what it does is the Server's to decide.
/// </summary>
public readonly record struct UseItem(ItemId Item) : INetCommand<UseItem>
{
    public static ushort CommandId => 30;

    public static UseItem Read(ref NetReader reader)
    {
        var id = reader.ReadString();
        return ItemId.TryParse(id, out var item) ? new UseItem(item) : throw new MalformedMessageException($"'{id}' is not a Content ID.");
    }

    public void Write(NetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Item.Value);
    }

    internal static CommandResult Handle(in UseItem command, CommandContext context) => context.Server.Use(context.Player, command.Item);
}
