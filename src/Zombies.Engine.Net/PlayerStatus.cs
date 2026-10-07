using Zombies.Domain.Combat;
using Zombies.Domain.Items;

namespace Zombies.Engine.Net;

/// <summary>
/// The local player's own Body and Held weapon as the Server last told them, which is what the HUD reads. The Server owns the
/// truth; this is a copy that is replaced whole by each status message.
/// </summary>
public sealed class ReplicatedStatus
{
    /// <summary>The Body as of the newest status message. Until the first one arrives it is a fresh, unhurt Body.</summary>
    public Body Body { get; private set; } = new(new BodyId(0));

    public ItemId? Weapon { get; private set; }

    /// <summary>The Held weapon's Item state, which carries its Condition and rounds.</summary>
    public ItemState? WeaponState { get; private set; }

    /// <summary>How many status messages have been applied.</summary>
    public int Updates { get; private set; }

    internal void Apply(ref NetReader reader)
    {
        var (snapshot, weapon, state) = PlayerStatusCodec.Read(ref reader);
        reader.EnsureEnd();
        try
        {
            Body = Body.Restore(snapshot);
        }
        catch (ArgumentException ex)
        {
            throw new MalformedMessageException(ex.Message, ex);
        }

        Weapon = weapon;
        WeaponState = state;
        Updates++;
    }
}

internal static class PlayerStatusCodec
{
    private const int MaxEntries = 1024;

    public static void Write(NetWriter writer, BodySnapshot body, ItemId? weapon, ItemState? state)
    {
        writer.WriteUInt64((ulong)body.Id);
        writer.WriteBool(body.IsAlive);
        writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(body.BloodLiters));
        writer.WriteInt32(body.NextWoundId);
        writer.WriteVarUInt((uint)body.Parts.Count);
        foreach (var part in body.Parts)
        {
            writer.WriteByte((byte)part.Part);
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(part.Health));
            writer.WriteBool(part.IsMissing);
        }

        writer.WriteVarUInt((uint)body.Wounds.Count);
        foreach (var wound in body.Wounds)
        {
            writer.WriteInt32(wound.Id);
            writer.WriteByte((byte)wound.Part);
            writer.WriteByte((byte)wound.Type);
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(wound.Severity));
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(wound.BleedMillilitersPerMinute));
            writer.WriteBool(wound.IsBandaged);
            writer.WriteBool(wound.IsStump);
            writer.WriteString(wound.Kind ?? string.Empty);
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(wound.AgeSeconds));
        }

        writer.WriteString(weapon?.Value ?? string.Empty);
        var values = state?.Values ?? new Dictionary<string, int>();
        writer.WriteVarUInt((uint)values.Count);
        foreach (var (name, value) in values)
        {
            writer.WriteString(name);
            writer.WriteInt32(value);
        }

        var attached = state?.Attached ?? new Dictionary<ItemId, int>();
        writer.WriteVarUInt((uint)attached.Count);
        foreach (var (item, count) in attached)
        {
            writer.WriteString(item.Value);
            writer.WriteInt32(count);
        }
    }

    public static (BodySnapshot Body, ItemId? Weapon, ItemState? State) Read(ref NetReader reader)
    {
        var id = (long)reader.ReadUInt64();
        var alive = reader.ReadBool();
        var blood = Double(ref reader);
        var nextWound = reader.ReadInt32();
        var parts = new List<PartSnapshot>();
        for (var i = Count(ref reader); i > 0; i--)
        {
            parts.Add(new PartSnapshot((BodyPart)reader.ReadByte(), Double(ref reader), reader.ReadBool()));
        }

        var wounds = new List<WoundSnapshot>();
        for (var i = Count(ref reader); i > 0; i--)
        {
            var woundId = reader.ReadInt32();
            var part = (BodyPart)reader.ReadByte();
            var type = (DamageType)reader.ReadByte();
            var severity = Double(ref reader);
            var bleed = Double(ref reader);
            var bandaged = reader.ReadBool();
            var stump = reader.ReadBool();
            var kind = reader.ReadString();
            wounds.Add(new WoundSnapshot(woundId, part, type, severity, bleed, bandaged, stump, kind.Length == 0 ? null : kind, Double(ref reader)));
        }

        var weaponText = reader.ReadString();
        ItemId? weapon = null;
        if (weaponText.Length > 0)
        {
            weapon = ItemId.TryParse(weaponText, out var parsed) ? parsed : throw new MalformedMessageException($"'{weaponText}' is not a Content ID.");
        }

        var values = new List<KeyValuePair<string, int>>();
        for (var i = Count(ref reader); i > 0; i--)
        {
            values.Add(new(reader.ReadString(), reader.ReadInt32()));
        }

        var attached = new List<KeyValuePair<ItemId, int>>();
        for (var i = Count(ref reader); i > 0; i--)
        {
            var text = reader.ReadString();
            attached.Add(new(ItemId.TryParse(text, out var item) ? item : throw new MalformedMessageException($"'{text}' is not a Content ID."), reader.ReadInt32()));
        }

        ItemState? state;
        try
        {
            state = weapon is null ? null : ItemState.Create(values, attached);
        }
        catch (ArgumentException ex)
        {
            throw new MalformedMessageException(ex.Message, ex);
        }

        return (new BodySnapshot(id, alive, blood, nextWound, parts, wounds), weapon, state);
    }

    private static double Double(ref NetReader reader) => BitConverter.UInt64BitsToDouble(reader.ReadUInt64());

    private static uint Count(ref NetReader reader)
    {
        var count = reader.ReadVarUInt();
        return count <= MaxEntries ? count : throw new MalformedMessageException($"A status message lists {count} entries; at most {MaxEntries} are allowed.");
    }
}
