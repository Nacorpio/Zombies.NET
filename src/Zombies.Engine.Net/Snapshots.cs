namespace Zombies.Engine.Net;

/// <summary>The entities one snapshot holds for one client, sorted by id so two frames diff in one pass.</summary>
internal sealed class SnapshotFrame
{
    private EntityState[] _entities = new EntityState[64];

    public uint Sequence { get; private set; }

    public ulong Tick { get; private set; }

    /// <summary>The newest player input the Server had processed when it took this snapshot, or 0.</summary>
    public uint AckedInput { get; private set; }

    public int Count { get; private set; }

    public ReadOnlySpan<EntityState> Entities => _entities.AsSpan(0, Count);

    public void Reset(uint sequence, ulong tick, uint ackedInput)
    {
        Sequence = sequence;
        Tick = tick;
        AckedInput = ackedInput;
        Count = 0;
    }

    public void Add(in EntityState state)
    {
        if (Count == _entities.Length)
        {
            Array.Resize(ref _entities, _entities.Length * 2);
        }

        _entities[Count++] = state;
    }

    /// <summary>Marks the slot empty, for a frame that failed to decode.</summary>
    public void Invalidate() => Sequence = 0;
}

/// <summary>The last <see cref="Capacity"/> frames by sequence. Sequence 0 means none, so no frame ever uses it.</summary>
internal sealed class SnapshotHistory
{
    public const int Capacity = 32;

    private readonly SnapshotFrame[] _frames = new SnapshotFrame[Capacity];

    public SnapshotHistory()
    {
        for (var i = 0; i < Capacity; i++)
        {
            _frames[i] = new SnapshotFrame();
        }
    }

    public SnapshotFrame? Find(uint sequence)
    {
        if (sequence == 0)
        {
            return null;
        }

        var frame = _frames[sequence % Capacity];
        return frame.Sequence == sequence ? frame : null;
    }

    public SnapshotFrame Begin(uint sequence, ulong tick, uint ackedInput)
    {
        var frame = _frames[sequence % Capacity];
        frame.Reset(sequence, tick, ackedInput);
        return frame;
    }
}

/// <summary>
/// Delta snapshot encoding. A snapshot names a baseline the client acknowledged, or 0 for none, and lists only entities
/// that appeared, changed, or left since that baseline. Entries are in id order.
/// </summary>
internal static class SnapshotCodec
{
    private const byte Created = 1;
    private const byte PositionChanged = 2;
    private const byte YawChanged = 4;
    private const byte Removed = 8;
    private const byte ZombieChanged = 16;
    private const byte PlayerChanged = 32;
    private const byte UpdateMask = PositionChanged | YawChanged | ZombieChanged | PlayerChanged;

    public static void Write(NetWriter writer, SnapshotFrame? baseline, SnapshotFrame current)
    {
        writer.WriteByte((byte)MessageType.Snapshot);
        writer.WriteUInt32(current.Sequence);
        writer.WriteUInt64(current.Tick);
        writer.WriteUInt32(current.AckedInput);
        writer.WriteUInt32(baseline?.Sequence ?? 0);
        var countAt = writer.ReserveUInt16();
        var count = 0;

        var before = baseline is null ? [] : baseline.Entities;
        var now = current.Entities;
        int b = 0, n = 0;
        while (b < before.Length || n < now.Length)
        {
            if (n == now.Length || (b < before.Length && before[b].Id < now[n].Id))
            {
                writer.WriteVarUInt(before[b].Id);
                writer.WriteByte(Removed);
                b++;
                count++;
            }
            else if (b == before.Length || now[n].Id < before[b].Id)
            {
                var e = now[n];
                writer.WriteVarUInt(e.Id);
                writer.WriteByte(Created);
                writer.WriteUInt16(e.Kind);
                WritePosition(writer, e);
                writer.WriteSingle(e.Yaw);
                if (e.Kind == EntityKind.Zombie)
                {
                    WriteZombie(writer, e.Zombie, whole: true);
                }
                else if (e.Kind == EntityKind.Player)
                {
                    WritePlayer(writer, e.Player);
                }

                n++;
                count++;
            }
            else
            {
                var old = before[b];
                var e = now[n];
                var flags = (byte)((old.Position != e.Position ? PositionChanged : 0) | (BitConverter.SingleToInt32Bits(old.Yaw) != BitConverter.SingleToInt32Bits(e.Yaw) ? YawChanged : 0) | (old.Zombie != e.Zombie ? ZombieChanged : 0) | (old.Player != e.Player ? PlayerChanged : 0));
                if (old.Kind != e.Kind)
                {
                    // An id is never reused for another kind; treat it as a fresh entity if it ever is.
                    flags = Created;
                }

                if (flags != 0)
                {
                    writer.WriteVarUInt(e.Id);
                    writer.WriteByte(flags);
                    if (flags == Created)
                    {
                        writer.WriteUInt16(e.Kind);
                    }

                    if ((flags & (Created | PositionChanged)) != 0)
                    {
                        WritePosition(writer, e);
                    }

                    if ((flags & (Created | YawChanged)) != 0)
                    {
                        writer.WriteSingle(e.Yaw);
                    }

                    if (e.Kind == EntityKind.Zombie && (flags & (Created | ZombieChanged)) != 0)
                    {
                        WriteZombie(writer, e.Zombie, whole: flags == Created);
                    }

                    if (e.Kind == EntityKind.Player && (flags & (Created | PlayerChanged)) != 0)
                    {
                        WritePlayer(writer, e.Player);
                    }

                    count++;
                }

                b++;
                n++;
            }
        }

        if (count > ushort.MaxValue)
        {
            throw new InvalidOperationException($"A snapshot of {count} changes does not fit the message format.");
        }

        writer.PatchUInt16(countAt, (ushort)count);
    }

    /// <summary>Reads the header after the message type. Returns the sequence, tick, acknowledged input, and baseline sequence.</summary>
    public static (uint Sequence, ulong Tick, uint AckedInput, uint Baseline) ReadHeader(ref NetReader reader) =>
        (reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32());

    /// <summary>Applies the entries after the header to <paramref name="baseline"/> and writes the result into <paramref name="target"/>.</summary>
    public static void ReadEntries(ref NetReader reader, SnapshotFrame? baseline, SnapshotFrame target)
    {
        var before = baseline is null ? [] : baseline.Entities;
        var count = reader.ReadUInt16();
        var b = 0;
        uint lastId = 0;
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadVarUInt();
            if (i > 0 && id <= lastId)
            {
                throw new MalformedMessageException("Snapshot entries must be in increasing id order.");
            }

            lastId = id;
            var flags = reader.ReadByte();

            // Entities the delta does not mention carry over unchanged.
            while (b < before.Length && before[b].Id < id)
            {
                target.Add(before[b++]);
            }

            var known = b < before.Length && before[b].Id == id;
            switch (flags)
            {
                case Removed when known:
                    b++;
                    break;
                case Created:
                    var kind = reader.ReadUInt16();
                    var created = new EntityState(id, kind, ReadPosition(ref reader), reader.ReadSingle());
                    target.Add(kind switch
                    {
                        EntityKind.Zombie => created with { Zombie = ReadZombie(ref reader, default, whole: true) },
                        EntityKind.Player => created with { Player = ReadPlayer(ref reader) },
                        _ => created,
                    });
                    b += known ? 1 : 0;
                    break;
                case > 0 when known && (flags & ~UpdateMask) == 0:
                    var old = before[b++];
                    var position = (flags & PositionChanged) != 0 ? ReadPosition(ref reader) : old.Position;
                    var yaw = (flags & YawChanged) != 0 ? reader.ReadSingle() : old.Yaw;
                    var zombie = (flags & ZombieChanged) != 0 && old.Kind == EntityKind.Zombie ? ReadZombie(ref reader, old.Zombie, whole: false) : old.Zombie;
                    var player = (flags & PlayerChanged) != 0 && old.Kind == EntityKind.Player ? ReadPlayer(ref reader) : old.Player;
                    target.Add(old with { Position = position, Yaw = yaw, Zombie = zombie, Player = player });
                    break;
                default:
                    throw new MalformedMessageException($"Snapshot entry for entity {id} has flags {flags} that do not fit its baseline.");
            }
        }

        while (b < before.Length)
        {
            target.Add(before[b++]);
        }
    }

    private static void WritePlayer(NetWriter writer, in PlayerState player) =>
        writer.WriteByte((byte)((player.Dead ? 1 : 0) | (player.Sleeping ? 2 : 0)));

    private static PlayerState ReadPlayer(ref NetReader reader)
    {
        var flags = reader.ReadByte();
        return new PlayerState((flags & 1) != 0, (flags & 2) != 0);
    }

    private static void WriteZombie(NetWriter writer, in ZombieState zombie, bool whole)
    {
        if (whole)
        {
            writer.WriteUInt64(zombie.Seed);
            writer.WriteUInt16(zombie.Type);
            writer.WriteByte(zombie.Level);
        }

        writer.WriteByte(zombie.Missing);
        writer.WriteBool(zombie.Dead);
    }

    private static ZombieState ReadZombie(ref NetReader reader, ZombieState old, bool whole)
    {
        if (!whole)
        {
            return old with { Missing = reader.ReadByte(), Dead = reader.ReadBool() };
        }

        return new ZombieState(reader.ReadUInt64(), reader.ReadUInt16(), reader.ReadByte(), reader.ReadByte(), reader.ReadBool());
    }

    private static void WritePosition(NetWriter writer, in EntityState e)
    {
        writer.WriteSingle(e.Position.X);
        writer.WriteSingle(e.Position.Y);
        writer.WriteSingle(e.Position.Z);
    }

    private static System.Numerics.Vector3 ReadPosition(ref NetReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
