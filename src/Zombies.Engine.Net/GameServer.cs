using System.Numerics;
using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Death;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;
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

    /// <summary>Every item a player can carry, which the Container they carry and a Corpse hold. Defaults to none.</summary>
    public IItemCatalog Items { get; init; } = new ItemCatalog([]);

    /// <summary>What a player can wear. Defaults to nothing.</summary>
    public IWearableCatalog Wearables { get; init; } = new WearableCatalog([]);

    /// <summary>What happens when a player's Body dies. Defaults to respawning at <see cref="SpawnPoint"/> after a delay.</summary>
    public DeathOptions Death { get; init; } = new();

    /// <summary>How much a player carries in their Container.</summary>
    public Mass CarryMass { get; init; } = Mass.FromKilograms(40);

    public Volume CarryVolume { get; init; } = Volume.FromLiters(60);

    /// <summary>How long a day lasts, which a Memorial counts a life in.</summary>
    public TimeSpan DayLength { get; init; } = TimeSpan.FromMinutes(24);

    /// <summary>The weapons a player can hold and use. Null means this Server has none, so every weapon command is refused.</summary>
    public WeaponCatalog? Weapons { get; init; }

    /// <summary>The Treatments a player can apply to their own Wounds. Null means none, so every treat command is refused.</summary>
    public TreatmentCatalog? Treatments { get; init; }

    /// <summary>Tuning for each player's Body, including the Wound kinds their hits cause. Defaults to a healthy adult with unkinded Wounds.</summary>
    public BodyConfig? PlayerBody { get; init; }
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

    /// <summary>This player's Body, replaced with a fresh one when they respawn. Set when they join.</summary>
    public Body Body { get; internal set; } = null!;

    public Needs Needs { get; internal set; } = null!;

    public Outfit Outfit { get; internal set; } = null!;

    /// <summary>What this player carries. Empty while they are dead, because it went into their Corpse.</summary>
    public Container Carried { get; internal set; } = null!;

    /// <summary>The Held weapon, which must be a Stack in <see cref="Carried"/>, or null when the hands are empty.</summary>
    public ItemId? Held { get; internal set; }

    /// <summary>The Item state of the Held weapon: its Condition and loaded rounds, which the Server alone changes.</summary>
    public ItemState? HeldState { get; internal set; }

    /// <summary>The tick from which the Held weapon may be used again, so the rate of fire is the Server's to enforce.</summary>
    internal long NextAttackTick { get; set; }

    /// <summary>Whether the player's Body or Held weapon changed since the Server last told them, so only a change is sent.</summary>
    internal bool StatusDirty { get; set; }

    /// <summary>The Inventory commands over <see cref="Carried"/>, which is how the Server takes an item out or puts one back with new Item state.</summary>
    internal InventoryService Inventory { get; set; } = null!;

    /// <summary>Whether the Body is dead, which makes the player a Spectator who cannot act.</summary>
    public bool IsDead => !Body.IsAlive;

    /// <summary>Zombies this player has killed in their current life, which a Memorial records.</summary>
    public int Kills { get; internal set; }

    /// <summary>Counts a kill toward the Memorial of this player's current life.</summary>
    public void CreditKill() => Kills++;

    internal long LifeStartTick { get; set; }

    /// <summary>When set, the tick at which a dead player respawns.</summary>
    internal long? RespawnAtTick { get; set; }

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
public sealed partial class GameServer : ITickable
{
    private const int RefusalGraceTicks = Simulation.TickRateHz;

    private readonly ITransport _transport;
    private readonly Dictionary<int, PlayerSession> _sessions = [];
    private readonly List<PlayerSession> _closing = [];
    private readonly NetWriter _writer = new(4096);
    private readonly Handler _handler;
    private readonly DeathService _death;
    private readonly Dictionary<uint, Corpse> _corpses = [];
    private readonly long _dayTicks;
    private readonly long _respawnTicks;
    private readonly WeaponService? _weapons;
    private long _tick;
    private int _snapshotAccumulator;

    /// <param name="stores">Where Corpses, their Containers, and Memorials are kept. Defaults to memory; a Server with a save passes its own, and the Corpses it holds appear in the world.</param>
    public GameServer(ITransport transport, ServerOptions options, DeathStores? stores = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        if (options.SnapshotRateHz is < 1 or > Simulation.TickRateHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Snapshot rate must be between 1 and {Simulation.TickRateHz} Hz.");
        }

        if (options.DayLength <= TimeSpan.Zero || options.Death.RespawnDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A day must last longer than nothing and a respawn delay cannot be negative.");
        }

        _transport = transport;
        Options = options;
        _handler = new Handler(this);
        stores ??= DeathStores.InMemory();
        _death = new DeathService(options.Items, stores);
        _dayTicks = Math.Max(1, (long)(options.DayLength.TotalSeconds * Simulation.TickRateHz));
        _respawnTicks = (long)Math.Ceiling(options.Death.RespawnDelay.TotalSeconds * Simulation.TickRateHz);
        Commands.Register<MovePlayer>(MovePlayer.Handle);
        Commands.Register<PlayerInputCommand>(PlayerInputCommand.Handle);
        Commands.Register<LootCorpse>(LootCorpse.Handle);
        Commands.Register<HoldWeapon>(HoldWeapon.Handle);
        Commands.Register<UseWeapon>(UseWeapon.Handle);
        Commands.Register<LoadWeapon>(LoadWeapon.Handle);
        Commands.Register<TreatWounds>(TreatWounds.Handle);
        _weapons = options.Weapons is null ? null : new WeaponService(options.Weapons);
        foreach (var corpse in stores.Corpses.All())
        {
            _corpses[World.Spawn(EntityKind.Corpse, corpse.Position, 0f)] = corpse;
        }
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

    /// <summary>
    /// Applies a hit to a player's Body, with the protection of what they wear. The Server is the only caller, as an attacking zombie
    /// or a trap would be. A hit that kills sends the player to spectate and leaves a Corpse.
    /// </summary>
    public CombatResult Damage(PlayerSession session, BodyPart part, DamageType type, double damage)
    {
        EnsureJoined(session);
        return Resolve(session, session.Body.TakeHit(part, type, damage, session.Outfit.Protection(part, type)));
    }

    /// <summary>Lets time pass on a player's Body, which bleeds and can die of it.</summary>
    public CombatResult AdvanceBody(PlayerSession session, TimeSpan elapsed)
    {
        EnsureJoined(session);
        return Resolve(session, session.Body.Advance(elapsed));
    }

    private static void EnsureJoined(PlayerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsJoined)
        {
            throw new InvalidOperationException("The player has not joined.");
        }
    }

    private CombatResult Resolve(PlayerSession session, CombatResult result)
    {
        session.StatusDirty = true;
        if (result.Events.OfType<BodyDied>().FirstOrDefault() is { } died)
        {
            Die(session, died.Cause);
        }

        return result;
    }

    /// <summary>The Body died: everything carried and worn goes into a Corpse where they fell, and the player spectates until they respawn.</summary>
    private void Die(PlayerSession session, DeathCause cause)
    {
        World.TryGet(session.EntityId, out var player);
        var days = (int)((_tick - session.LifeStartTick) / _dayTicks);
        var report = _death.Die(session.Carried, session.Outfit, session.Name, cause, days, session.Kills, player.Position);
        _corpses[World.Spawn(EntityKind.Corpse, report.Corpse.Position, player.Yaw)] = report.Corpse;
        World.UpdatePlayer(session.EntityId, new PlayerState(Dead: true));
        session.Held = null;
        session.HeldState = null;
        session.RespawnAtTick = Options.Death.Policy == DeathPolicy.RespawnAfterDelay ? _tick + _respawnTicks : null;
    }

    /// <summary>Gives a dead player a fresh Body and fresh Needs at the spawn point. What they carried stays in their Corpse.</summary>
    private void Respawn(PlayerSession session)
    {
        session.Body = new Body(new BodyId(session.EntityId), Options.PlayerBody);
        session.Needs = new Needs();
        session.Held = null;
        session.HeldState = null;
        session.StatusDirty = true;
        session.Kills = 0;
        session.LifeStartTick = _tick;
        session.RespawnAtTick = null;
        Teleport(session, Options.SpawnPoint, 0f);
        World.UpdatePlayer(session.EntityId, default);
    }

    internal CommandResult Loot(PlayerSession session, uint corpseEntity)
    {
        if (!_corpses.TryGetValue(corpseEntity, out var corpse) || !World.TryGet(session.EntityId, out var player))
        {
            return CommandResult.Invalid("There is no such Corpse.");
        }

        if (Vector3.DistanceSquared(player.Position, corpse.Position) > LootCorpse.Reach * LootCorpse.Reach)
        {
            return CommandResult.Invalid($"A Corpse can be looted from at most {LootCorpse.Reach} blocks away.");
        }

        if (!_death.Loot(corpse, session.Carried, out var emptied))
        {
            return CommandResult.Invalid("The Corpse holds nothing.");
        }

        if (emptied)
        {
            _corpses.Remove(corpseEntity);
            World.Despawn(corpseEntity);
        }

        return CommandResult.Accepted;
    }

    public void Tick(long tick)
    {
        _tick = tick;
        _transport.Poll(_handler);
        CloseRefused();
        foreach (var session in _sessions.Values)
        {
            if (session.RespawnAtTick <= _tick)
            {
                Respawn(session);
            }
            else if (session.IsJoined && !session.IsDead && session.Body.HasWounds)
            {
                // A Wound that bleeds keeps costing blood, so a player left bleeding eventually dies of it.
                Resolve(session, session.Body.Advance(TimeSpan.FromSeconds(1.0 / Simulation.TickRateHz)));
            }
        }

        _snapshotAccumulator += Options.SnapshotRateHz;
        if (_snapshotAccumulator >= Simulation.TickRateHz)
        {
            _snapshotAccumulator -= Simulation.TickRateHz;
            foreach (var session in _sessions.Values)
            {
                if (session.IsJoined)
                {
                    SendSnapshot(session);
                    SendStatus(session);
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
        session.Body = new Body(new BodyId(session.EntityId), Options.PlayerBody);
        session.Needs = new Needs();
        session.Outfit = new Outfit(Options.Wearables);
        session.Carried = new Container(_death.NextContainerId(), Options.CarryMass, Options.CarryVolume, Options.Items);
        session.Inventory = new InventoryService(Options.Items, new SingleContainerRepository(session.Carried));
        session.LifeStartTick = _tick;
        session.StatusDirty = true;
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
        var result = session.IsDead
            ? new CommandResult(CommandRejection.Dead, "A dead player is spectating and cannot act.")
            : Commands.Handle(commandId, ref reader, new CommandContext(this, session));
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
