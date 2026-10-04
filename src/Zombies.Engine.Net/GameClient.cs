using Zombies.Engine.Core;

namespace Zombies.Engine.Net;

public enum ClientState
{
    Connecting,
    Joined,
    Refused,
    Disconnected,
}

/// <summary>The newest Domain command the Server rejected, by the sequence <see cref="GameClient.Send{TCommand}"/> returned.</summary>
public sealed record CommandRejected(uint Sequence, CommandRejection Reason, string Detail);

/// <summary>What a client knows of the world: the entities in its newest decoded snapshot.</summary>
public sealed class ReplicatedWorld
{
    private readonly SnapshotHistory _history = new();
    private SnapshotFrame? _latest;

    public uint Sequence => _latest?.Sequence ?? 0;

    /// <summary>The Server tick the newest snapshot was taken on.</summary>
    public ulong ServerTick => _latest?.Tick ?? 0;

    public ReadOnlySpan<EntityState> Entities => _latest is null ? [] : _latest.Entities;

    public bool TryGet(uint id, out EntityState state)
    {
        foreach (ref readonly var entity in Entities)
        {
            if (entity.Id == id)
            {
                state = entity;
                return true;
            }
        }

        state = default;
        return false;
    }

    /// <summary>Decodes one snapshot message after its type byte. Returns false when it is stale or its baseline is gone.</summary>
    internal bool Apply(ref NetReader reader)
    {
        var (sequence, tick, baselineSequence) = SnapshotCodec.ReadHeader(ref reader);
        if (sequence <= Sequence)
        {
            return false;
        }

        var baseline = _history.Find(baselineSequence);
        if (baselineSequence != 0 && baseline is null)
        {
            return false;
        }

        var frame = _history.Begin(sequence, tick);
        try
        {
            SnapshotCodec.ReadEntries(ref reader, baseline, frame);
            reader.EnsureEnd();
        }
        catch (MalformedMessageException)
        {
            frame.Invalidate();
            throw;
        }

        _latest = frame;
        return true;
    }
}

/// <summary>
/// A client of the Server: it joins with its <see cref="GameIdentity"/>, sends Domain commands, and keeps the
/// <see cref="ReplicatedWorld"/> up to date from delta snapshots, acknowledging each one it decodes.
/// </summary>
public sealed class GameClient : ITickable
{
    private readonly ITransport _transport;
    private readonly GameIdentity _identity;
    private readonly string _playerName;
    private readonly NetWriter _writer = new(1024);
    private readonly Handler _handler;
    private uint _nextCommand = 1;

    public GameClient(ITransport transport, GameIdentity identity, string playerName)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(playerName);
        _transport = transport;
        _identity = identity;
        _playerName = playerName;
        _handler = new Handler(this);
    }

    public ClientState State { get; private set; }

    public JoinRefusal? Refusal { get; private set; }

    public string? RefusalDetail { get; private set; }

    /// <summary>This client's own player entity, once joined.</summary>
    public uint PlayerEntityId { get; private set; }

    public ulong WorldSeed { get; private set; }

    public ReplicatedWorld World { get; } = new();

    public long SnapshotsReceived { get; private set; }

    public CommandRejected? LastRejection { get; private set; }

    public int RejectionCount { get; private set; }

    /// <summary>Reads everything the Server sent since the last tick.</summary>
    public void Tick(long tick) => Poll();

    public void Poll() => _transport.Poll(_handler);

    /// <summary>Sends a Domain command and returns its sequence, which a rejection names.</summary>
    public uint Send<TCommand>(in TCommand command)
        where TCommand : INetCommand<TCommand>
    {
        if (State != ClientState.Joined)
        {
            throw new InvalidOperationException("Join before sending commands.");
        }

        var sequence = _nextCommand++;
        _writer.Clear();
        _writer.WriteByte((byte)MessageType.Command);
        _writer.WriteUInt32(sequence);
        _writer.WriteUInt16(TCommand.CommandId);
        command.Write(_writer);
        _transport.Send(ConnectionId.Server, _writer.Written, Delivery.ReliableOrdered);
        return sequence;
    }

    /// <summary>Sends raw bytes as a message, for tests that play a broken or hostile client.</summary>
    public void SendRaw(ReadOnlySpan<byte> message) => _transport.Send(ConnectionId.Server, message, Delivery.ReliableOrdered);

    private void OnConnected()
    {
        _writer.Clear();
        _writer.WriteByte((byte)MessageType.JoinRequest);
        _identity.Write(_writer);
        _writer.WriteString(_playerName);
        _transport.Send(ConnectionId.Server, _writer.Written, Delivery.ReliableOrdered);
    }

    private void OnReceived(ReadOnlySpan<byte> payload)
    {
        var reader = new NetReader(payload);
        try
        {
            switch ((MessageType)reader.ReadByte())
            {
                case MessageType.JoinAccepted when State == ClientState.Connecting:
                    PlayerEntityId = reader.ReadUInt32();
                    WorldSeed = reader.ReadUInt64();
                    reader.ReadUInt64();
                    reader.ReadByte();
                    reader.ReadByte();
                    State = ClientState.Joined;
                    break;
                case MessageType.JoinRefused when State == ClientState.Connecting:
                    Refusal = (JoinRefusal)reader.ReadByte();
                    RefusalDetail = reader.ReadString();
                    State = ClientState.Refused;
                    break;
                case MessageType.CommandRejected when State == ClientState.Joined:
                    LastRejection = new CommandRejected(reader.ReadUInt32(), (CommandRejection)reader.ReadByte(), reader.ReadString());
                    RejectionCount++;
                    break;
                case MessageType.Snapshot when State == ClientState.Joined:
                    if (World.Apply(ref reader))
                    {
                        SnapshotsReceived++;
                        _writer.Clear();
                        _writer.WriteByte((byte)MessageType.SnapshotAck);
                        _writer.WriteUInt32(World.Sequence);
                        _transport.Send(ConnectionId.Server, _writer.Written, Delivery.Unreliable);
                    }

                    break;
            }
        }
        catch (MalformedMessageException)
        {
            // A broken Server message is dropped; a snapshot that fails is replaced by the next one.
        }
    }

    private void OnDisconnected()
    {
        if (State != ClientState.Refused)
        {
            State = ClientState.Disconnected;
        }
    }

    private sealed class Handler(GameClient client) : ITransportHandler
    {
        public void OnConnected(ConnectionId connection) => client.OnConnected();

        public void OnReceived(ConnectionId connection, ReadOnlySpan<byte> payload, Delivery delivery) => client.OnReceived(payload);

        public void OnDisconnected(ConnectionId connection) => client.OnDisconnected();
    }
}
