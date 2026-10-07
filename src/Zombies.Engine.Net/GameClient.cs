using System.Numerics;
using Zombies.Domain.StatusEffects;
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

    /// <summary>The newest player input the Server had processed when it took the newest snapshot, or 0.</summary>
    public uint AckedInput => _latest?.AckedInput ?? 0;

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
        var (sequence, tick, ackedInput, baselineSequence) = SnapshotCodec.ReadHeader(ref reader);
        if (sequence <= Sequence)
        {
            return false;
        }

        var baseline = _history.Find(baselineSequence);
        if (baselineSequence != 0 && baseline is null)
        {
            return false;
        }

        var frame = _history.Begin(sequence, tick, ackedInput);
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

/// <summary>One input the client sent and has not yet seen confirmed, kept so it can be replayed after a correction.</summary>
public readonly record struct PredictedInput(uint Sequence, PlayerInput Input);

/// <summary>
/// The local player as the client sees it: the position it predicted, the inputs still in flight, and the lean amount.
/// The Server's snapshots confirm or correct it; see <see cref="GameClient"/>.
/// </summary>
public sealed class LocalPlayer
{
    /// <summary>How many predicted states are kept so a correction can rewind to the acknowledged input.</summary>
    private const int HistoryCapacity = 128;

    private readonly List<PredictedInput> _pending = new(64);
    private readonly uint[] _historySequence = new uint[HistoryCapacity];
    private readonly PlayerMoveState[] _historyState = new PlayerMoveState[HistoryCapacity];
    private PlayerMoveState _initial;

    /// <summary>Where the client currently draws the player, after prediction and any reconciliation.</summary>
    public PlayerMoveState State { get; private set; }

    /// <summary>How far the camera leans sideways, in blocks. Cosmetic, so it is never reconciled.</summary>
    public float Lean { get; private set; }

    /// <summary>Inputs sent but not yet confirmed by a snapshot.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>How many times the Server's state disagreed with the prediction and the client had to replay.</summary>
    public int ReconciliationCount { get; private set; }

    /// <summary>The largest correction applied, in blocks. Zero means the prediction matched the Server exactly.</summary>
    public float LastCorrectionDistance { get; private set; }

    /// <summary>The largest correction ever applied, in blocks. Useful for spotting a prediction that keeps drifting.</summary>
    public float MaxCorrectionDistance { get; private set; }

    /// <summary>How many inputs were replayed by the last reconciliation.</summary>
    public int LastReplayedInputs { get; private set; }

    public void Reset(in PlayerMoveState state)
    {
        State = state;
        _initial = state;
        Lean = 0f;
        LastCorrectionDistance = 0f;
        MaxCorrectionDistance = 0f;
        ReconciliationCount = 0;
        _pending.Clear();
        Array.Clear(_historySequence);
    }

    /// <summary>
    /// Predicts one step from an input, records it as in flight under the command sequence it will be sent with, and
    /// returns that sequence. The Server names the same sequence when it acknowledges the input.
    /// </summary>
    public uint Predict(uint sequence, in PlayerInput input, IPlayerCollision collision)
    {
        State = PlayerMovement.Step(State, input, PlayerMovement.StepSeconds, collision);
        Lean = PlayerMovement.StepLean(Lean, input, PlayerMovement.StepSeconds);
        _pending.Add(new PredictedInput(sequence, input));
        _historySequence[sequence % HistoryCapacity] = sequence;
        _historyState[sequence % HistoryCapacity] = State;
        return sequence;
    }

    /// <summary>
    /// Takes the Server's authoritative position for the newest input it has processed and replays every input after it.
    /// The replay starts from the state the client itself predicted at that input, so the velocity and ground state are
    /// the ones the Server also had; only the position is corrected. A prediction that matched is left untouched.
    /// </summary>
    public void Reconcile(Vector3 serverPosition, float serverYaw, uint acknowledgedSequence, IPlayerCollision collision)
    {
        while (_pending.Count > 0 && _pending[0].Sequence <= acknowledgedSequence)
        {
            _pending.RemoveAt(0);
        }

        var predicted = State.Position;
        var baseState = StateAt(acknowledgedSequence) with { Position = serverPosition, Yaw = serverYaw };
        var state = baseState;
        foreach (var pending in _pending)
        {
            state = PlayerMovement.Step(state, pending.Input, PlayerMovement.StepSeconds, collision);
        }

        State = state;

        // The correction is how far the replay moved the player from where it had predicted it would be. When the
        // prediction was right this is zero, even though the Server's snapshot was several ticks behind.
        LastCorrectionDistance = Vector3.Distance(predicted, state.Position);
        MaxCorrectionDistance = MathF.Max(MaxCorrectionDistance, LastCorrectionDistance);
        LastReplayedInputs = _pending.Count;
        if (LastCorrectionDistance > 1e-4f)
        {
            ReconciliationCount++;
        }
    }

    /// <summary>The state the client predicted after the given input, or the state it started from when that is older.</summary>
    private PlayerMoveState StateAt(uint sequence)
    {
        var slot = sequence % HistoryCapacity;
        return _historySequence[slot] == sequence ? _historyState[slot] : _initial;
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

    /// <summary>This player's own Body and Held weapon, as the Server last told them.</summary>
    public ReplicatedStatus Status { get; } = new();

    /// <summary>The local player, predicted from input and reconciled against the Server's snapshots.</summary>
    public LocalPlayer Local { get; } = new();

    /// <summary>Resolves the local player's moves. Defaults to a flat floor; the game passes a Jolt world.</summary>
    public IPlayerCollision Collision { get; set; } = FlatFloorCollision.Instance;

    public long SnapshotsReceived { get; private set; }

    /// <summary>
    /// Whether this client's own player is dead and spectating, as the newest snapshot says. A spectator sends no input, since the
    /// Server rejects every command from a dead player.
    /// </summary>
    public bool IsSpectating => World.TryGet(PlayerEntityId, out var entity) && entity.Player.Dead;

    /// <summary>
    /// The Status effects on this client's own player, as the Server last told them. Other players' effects are never sent, so there is
    /// no way to see them.
    /// </summary>
    public IReadOnlyList<ActiveEffect> Effects { get; private set; } = [];

    public CommandRejected? LastRejection { get; private set; }

    public int RejectionCount { get; private set; }

    /// <summary>Reads everything the Server sent since the last tick.</summary>
    public void Tick(long tick) => Poll();

    public void Poll() => _transport.Poll(_handler);

    /// <summary>
    /// Predicts one fixed step from the player's input, sends it, and returns its sequence. The Server runs the same step,
    /// so the next snapshot confirms the prediction instead of correcting it. A spectator sends nothing and gets 0.
    /// </summary>
    public uint SendInput(in PlayerInput input)
    {
        if (IsSpectating)
        {
            return 0;
        }

        var sequence = _nextCommand++;
        Local.Predict(sequence, input, Collision);
        SendWithSequence(sequence, new PlayerInputCommand(input));
        return sequence;
    }

    /// <summary>Sends a Domain command and returns its sequence, which a rejection names.</summary>
    public uint Send<TCommand>(in TCommand command)
        where TCommand : INetCommand<TCommand>
    {
        var sequence = _nextCommand++;
        SendWithSequence(sequence, command);
        return sequence;
    }

    private void SendWithSequence<TCommand>(uint sequence, in TCommand command)
        where TCommand : INetCommand<TCommand>
    {
        if (State != ClientState.Joined)
        {
            throw new InvalidOperationException("Join before sending commands.");
        }

        _writer.Clear();
        _writer.WriteByte((byte)MessageType.Command);
        _writer.WriteUInt32(sequence);
        _writer.WriteUInt16(TCommand.CommandId);
        command.Write(_writer);
        _transport.Send(ConnectionId.Server, _writer.Written, Delivery.ReliableOrdered);
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
                    var spawn = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    var yaw = reader.ReadSingle();
                    Local.Reset(PlayerMoveState.At(spawn, yaw));
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
                case MessageType.PlayerStatus when State == ClientState.Joined:
                    Status.Apply(ref reader);
                    break;
                case MessageType.StatusEffects when State == ClientState.Joined:
                    Effects = StatusEffectMessages.Read(ref reader);
                    break;
                case MessageType.Snapshot when State == ClientState.Joined:
                    if (World.Apply(ref reader))
                    {
                        SnapshotsReceived++;
                        Reconcile();
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

    /// <summary>
    /// Takes the Server's state for this client's own player and replays the inputs it has not confirmed yet. The Server
    /// runs the same movement step, so a prediction that matched is confirmed and the local position does not move.
    /// </summary>
    private void Reconcile()
    {
        if (!World.TryGet(PlayerEntityId, out var entity))
        {
            return;
        }

        // A dead player is not moving: drop the inputs the Server rejected instead of replaying them.
        if (entity.Player.Dead)
        {
            Local.Reset(PlayerMoveState.At(entity.Position, entity.Yaw));
            return;
        }

        // The Server's snapshot is the truth for position and yaw; the rest of the state is the client's own.
        Local.Reconcile(entity.Position, entity.Yaw, World.AckedInput, Collision);
    }

    private sealed class Handler(GameClient client) : ITransportHandler
    {
        public void OnConnected(ConnectionId connection) => client.OnConnected();

        public void OnReceived(ConnectionId connection, ReadOnlySpan<byte> payload, Delivery delivery) => client.OnReceived(payload);

        public void OnDisconnected(ConnectionId connection) => client.OnDisconnected();
    }
}
