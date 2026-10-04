using System.Numerics;
using Zombies.Engine.Core;

namespace Zombies.Engine.Net;

/// <summary>How a Server runs: who may join, which world it hosts, and how much each client sees.</summary>
public sealed record ServerOptions(GameIdentity Identity, ulong WorldSeed)
{
    public int MaxPlayers { get; init; } = 4;

    /// <summary>A client receives entities within this many chunks of its player, measured on the larger axis.</summary>
    public int InterestRadiusChunks { get; init; } = 8;

    public int SnapshotRateHz { get; init; } = 20;

    public Vector3 SpawnPoint { get; init; } = new(8, 80, 8);
}

/// <summary>One connection to the Server, from connect to disconnect, and its player once it has joined.</summary>
public sealed class PlayerSession
{
    internal PlayerSession(ConnectionId connection) => Connection = connection;

    public ConnectionId Connection { get; }

    public bool IsJoined { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public uint EntityId { get; internal set; }

    /// <summary>The Server's authoritative movement state for this player, advanced one fixed step per input command.</summary>
    public PlayerMoveState Movement { get; internal set; }

    /// <summary>This player's character controller, created when they join.</summary>
    public IPlayerCollision Collision { get; internal set; } = FlatFloorCollision.Instance;

    /// <summary>The newest snapshot the client said it decoded, or 0. Snapshots are deltas against it.</summary>
    public uint AckedSnapshot { get; internal set; }

    /// <summary>The newest player input command the Server has processed, or 0. Snapshots carry it so the client can reconcile.</summary>
    public uint AckedInput { get; internal set; }

    internal SnapshotHistory History { get; } = new();

    internal uint LastSnapshot { get; set; }

    /// <summary>When set, the tick at which a refused connection is closed, after its refusal had time to arrive.</summary>
    internal long? DisconnectAtTick { get; set; }
}

/// <summary>
/// The authoritative Server (ADR 0003). It runs on the 30 Hz <see cref="Simulation"/> tick behind an <see cref="ITransport"/>:
/// the dedicated server drives it over LiteNetLib and solo play drives the same class over the in-memory transport.
/// Each tick it reads messages, validates and applies Domain commands, and sends delta snapshots at
/// <see cref="ServerOptions.SnapshotRateHz"/> to each joined client, limited to the chunks around its player.
/// </summary>
public sealed class GameServer : ITickable
{
    private const int RefusalGraceTicks = Simulation.TickRateHz;

    private readonly ITransport _transport;
    private readonly Dictionary<int, PlayerSession> _sessions = [];
    private readonly List<PlayerSession> _closing = [];
    private readonly NetWriter _writer = new(4096);
    private readonly Handler _handler;
    private long _tick;
    private int _snapshotAccumulator;

    public GameServer(ITransport transport, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        if (options.SnapshotRateHz is < 1 or > Simulation.TickRateHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Snapshot rate must be between 1 and {Simulation.TickRateHz} Hz.");
        }

        _transport = transport;
        Options = options;
        _handler = new Handler(this);
        Commands.Register<MovePlayer>(MovePlayer.Handle);
        Commands.Register<PlayerInputCommand>(PlayerInputCommand.Handle);
    }

    public ServerOptions Options { get; }

    public ServerWorld World { get; } = new();

    /// <summary>Resolves player moves against the world. Defaults to a flat floor; a Server with terrain passes a Jolt world.</summary>
    public IPlayerCollisionSource Collision { get; set; } = FlatFloorCollision.Instance;

    public CommandRegistry Commands { get; } = new();

    public int PlayerCount { get; private set; }

    /// <summary>Snapshots sent since start, over every client.</summary>
    public long SnapshotsSent { get; private set; }

    /// <summary>Bytes of snapshot sent since start, over every client.</summary>
    public long SnapshotBytesSent { get; private set; }

    public bool TryGetPlayer(ConnectionId connection, out PlayerSession session) => _sessions.TryGetValue(connection.Value, out session!);

    /// <summary>
    /// Moves a player without an input command, as a knock-back, a respawn, or an admin teleport would. The client sees the
    /// new position in the next snapshot and reconciles to it.
    /// </summary>
    public bool Teleport(PlayerSession session, Vector3 position, float yaw)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!World.TryGet(session.EntityId, out _))
        {
            return false;
        }

        session.Movement = session.Movement with { Position = position, Velocity = Vector3.Zero, Yaw = yaw };
        World.Move(session.EntityId, position, yaw);
        return true;
    }

    public void Tick(long tick)
    {
        _tick = tick;
        _transport.Poll(_handler);
        CloseRefused();

        _snapshotAccumulator += Options.SnapshotRateHz;
        if (_snapshotAccumulator >= Simulation.TickRateHz)
        {
            _snapshotAccumulator -= Simulation.TickRateHz;
            foreach (var session in _sessions.Values)
            {
                if (session.IsJoined)
                {
                    SendSnapshot(session);
                }
            }
        }
    }

    private void SendSnapshot(PlayerSession session)
    {
        if (!World.TryGet(session.EntityId, out var player))
        {
            return;
        }

        var sequence = session.LastSnapshot + 1;
        session.LastSnapshot = sequence;

        // The baseline is the newest snapshot the client decoded, if it is still in history.
        var baseline = sequence - session.AckedSnapshot < SnapshotHistory.Capacity ? session.History.Find(session.AckedSnapshot) : null;
        var frame = session.History.Begin(sequence, (ulong)_tick, session.AckedInput);
        var radius = Options.InterestRadiusChunks;
        int cx = player.ChunkX, cz = player.ChunkZ;
        foreach (ref readonly var entity in World.Entities)
        {
            if (Math.Abs(entity.ChunkX - cx) <= radius && Math.Abs(entity.ChunkZ - cz) <= radius)
            {
                frame.Add(entity);
            }
        }

        _writer.Clear();
        SnapshotCodec.Write(_writer, baseline, frame);
        _transport.Send(session.Connection, _writer.Written, Delivery.Unreliable);
        SnapshotsSent++;
        SnapshotBytesSent += _writer.Length;
    }

    private void OnReceived(ConnectionId connection, ReadOnlySpan<byte> payload)
    {
        if (!_sessions.TryGetValue(connection.Value, out var session) || session.DisconnectAtTick is not null)
        {
            return;
        }

        var reader = new NetReader(payload);
        try
        {
            var type = (MessageType)reader.ReadByte();
            switch (type)
            {
                case MessageType.JoinRequest when !session.IsJoined:
                    Join(session, ref reader);
                    break;
                case MessageType.Command when session.IsJoined:
                    RunCommand(session, ref reader);
                    break;
                case MessageType.SnapshotAck when session.IsJoined:
                    var acked = reader.ReadUInt32();
                    reader.EnsureEnd();
                    if (acked > session.AckedSnapshot && acked <= session.LastSnapshot)
                    {
                        session.AckedSnapshot = acked;
                    }

                    break;
                default:
                    // Anything out of turn means a broken or hostile client.
                    Drop(session);
                    break;
            }
        }
        catch (MalformedMessageException)
        {
            Drop(session);
        }
    }

    private void Join(PlayerSession session, ref NetReader reader)
    {
        var identity = GameIdentity.Read(ref reader);
        var name = reader.ReadString();
        reader.EnsureEnd();

        var refusal = Options.Identity.Check(identity);
        if (refusal is null && PlayerCount >= Options.MaxPlayers)
        {
            refusal = (JoinRefusal.ServerFull, $"The Server is full ({Options.MaxPlayers} players).");
        }

        _writer.Clear();
        if (refusal is { } r)
        {
            _writer.WriteByte((byte)MessageType.JoinRefused);
            _writer.WriteByte((byte)r.Reason);
            _writer.WriteString(r.Detail);
            _transport.Send(session.Connection, _writer.Written, Delivery.ReliableOrdered);
            session.DisconnectAtTick = _tick + RefusalGraceTicks;
            return;
        }

        session.IsJoined = true;
        session.Name = name;
        session.EntityId = World.Spawn(EntityKind.Player, Options.SpawnPoint, 0f);
        session.Movement = PlayerMoveState.At(Options.SpawnPoint);
        session.Collision = Collision.Create(Options.SpawnPoint, PlayerMovement.StandingHeight);
        PlayerCount++;

        _writer.WriteByte((byte)MessageType.JoinAccepted);
        _writer.WriteUInt32(session.EntityId);
        _writer.WriteUInt64(Options.WorldSeed);
        _writer.WriteUInt64((ulong)_tick);
        _writer.WriteByte(Simulation.TickRateHz);
        _writer.WriteByte((byte)Options.SnapshotRateHz);
        _writer.WriteSingle(Options.SpawnPoint.X);
        _writer.WriteSingle(Options.SpawnPoint.Y);
        _writer.WriteSingle(Options.SpawnPoint.Z);
        _writer.WriteSingle(0f);
        _transport.Send(session.Connection, _writer.Written, Delivery.ReliableOrdered);
    }

    private void RunCommand(PlayerSession session, ref NetReader reader)
    {
        var sequence = reader.ReadUInt32();
        var commandId = reader.ReadUInt16();
        var result = Commands.Handle(commandId, ref reader, new CommandContext(this, session));
        if (result.IsAccepted)
        {
            if (commandId == PlayerInputCommand.CommandId)
            {
                session.AckedInput = sequence;
            }

            return;
        }

        _writer.Clear();
        _writer.WriteByte((byte)MessageType.CommandRejected);
        _writer.WriteUInt32(sequence);
        _writer.WriteByte((byte)result.Rejection!.Value);
        _writer.WriteString(result.Detail ?? string.Empty);
        _transport.Send(session.Connection, _writer.Written, Delivery.ReliableOrdered);
    }

    private void CloseRefused()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.DisconnectAtTick <= _tick)
            {
                _closing.Add(session);
            }
        }

        foreach (var session in _closing)
        {
            Drop(session);
        }

        _closing.Clear();
    }

    private void Drop(PlayerSession session)
    {
        _transport.Disconnect(session.Connection);
        Forget(session.Connection);
    }

    private void Forget(ConnectionId connection)
    {
        if (_sessions.Remove(connection.Value, out var session) && session.IsJoined)
        {
            World.Despawn(session.EntityId);
            PlayerCount--;
        }
    }

    private sealed class Handler(GameServer server) : ITransportHandler
    {
        public void OnConnected(ConnectionId connection) => server._sessions[connection.Value] = new PlayerSession(connection);

        public void OnReceived(ConnectionId connection, ReadOnlySpan<byte> payload, Delivery delivery) => server.OnReceived(connection, payload);

        public void OnDisconnected(ConnectionId connection) => server.Forget(connection);
    }
}
