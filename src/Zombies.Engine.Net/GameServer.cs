using System.Numerics;
using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Death;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;
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

    /// <summary>The Movement modes player movement reads. A client must run the same ones to predict the Server. Defaults to the starting modes.</summary>
    public MovementModes MovementModes { get; init; } = PlayerMovement.DefaultModes;

    /// <summary>How long a day lasts, which a Memorial counts a life in.</summary>
    public TimeSpan DayLength { get; init; } = TimeSpan.FromMinutes(24);

    /// <summary>The rates and thresholds of every player's Needs, such as how fast fatigue grows and how well a bed rests.</summary>
    public NeedsConfig Needs { get; init; } = new();

    /// <summary>Where a player rests well. Defaults to the open ground everywhere.</summary>
    public IRestPlaces RestPlaces { get; init; } = OpenGround.Instance;

    /// <summary>The Status effects players can have, among them those that tiredness applies. Defaults to none.</summary>
    public StatusEffectCatalog StatusEffects { get; init; } = new([]);

    /// <summary>The Status effect that each level of fatigue applies to a player while they are at it, by Content ID. A level with none applies nothing.</summary>
    public IReadOnlyDictionary<FatigueLevel, string> FatigueEffects { get; init; } = new Dictionary<FatigueLevel, string>
    {
        [FatigueLevel.Tired] = "base:status_effect/tired",
        [FatigueLevel.Exhausted] = "base:status_effect/exhausted",
    };

    /// <summary>The Professions a player can pick when they join. Defaults to none, so every player starts empty-handed.</summary>
    public ProfessionCatalog Professions { get; init; } = new([]);

    /// <summary>The loot tables a Profession can roll for starting items. Defaults to none.</summary>
    public ILootTableCatalog Loot { get; init; } = new LootTableCatalog([]);

    /// <summary>Where, when and in what state players begin. Defaults to none: they start at <see cref="SpawnPoint"/> in the morning, unhurt.</summary>
    public Scenario? Scenario { get; init; }

    /// <summary>Finds where a Scenario of the given kind starts a player. Defaults to leaving every player at <see cref="SpawnPoint"/>.</summary>
    public Func<StartLocationKind, Vector3>? StartPoint { get; init; }
}

/// <summary>Decides how well a player rests at a position: on bare ground, in a shelter, or in a bed.</summary>
public interface IRestPlaces
{
    RestPlace At(Vector3 position);
}

/// <summary>Every place is the open ground.</summary>
public sealed class OpenGround : IRestPlaces
{
    public static OpenGround Instance { get; } = new();

    public RestPlace At(Vector3 position) => RestPlace.Ground;
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

    /// <summary>The Status effects on this player, among them the one their tiredness applies.</summary>
    public CreatureEffects Effects { get; internal set; } = null!;

    public Outfit Outfit { get; internal set; } = null!;

    /// <summary>What this player carries. Empty while they are dead, because it went into their Corpse.</summary>
    public Container Carried { get; internal set; } = null!;

    /// <summary>The Content ID of the Profession this player picked when they joined, or null when they picked none.</summary>
    public string? Profession { get; internal set; }

    /// <summary>The Modifiers on this player's character: what their Profession gives them, for the whole of their life on the Server.</summary>
    public ModifierSet Modifiers { get; } = new();

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
public sealed class GameServer : ITickable
{
    private const int RefusalGraceTicks = Simulation.TickRateHz;

    /// <summary>The time of day, as a fraction of a day, of a world with no Scenario: morning.</summary>
    public const double DefaultStartTimeOfDay = 0.4;

    private readonly ITransport _transport;
    private readonly Dictionary<int, PlayerSession> _sessions = [];
    private readonly List<PlayerSession> _closing = [];
    private readonly NetWriter _writer = new(4096);
    private readonly Handler _handler;
    private readonly DeathService _death;
    private readonly Dictionary<uint, Corpse> _corpses = [];
    private readonly long _dayTicks;
    private readonly long _respawnTicks;
    private readonly TimeSpan _gameTimePerTick;
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

        StartingLoadout.Validate(options);
        _transport = transport;
        Options = options;
        _handler = new Handler(this);
        stores ??= DeathStores.InMemory();
        _death = new DeathService(options.Items, stores);
        _dayTicks = Math.Max(1, (long)(options.DayLength.TotalSeconds * Simulation.TickRateHz));
        _gameTimePerTick = TimeSpan.FromDays(1) / _dayTicks;
        _respawnTicks = (long)Math.Ceiling(options.Death.RespawnDelay.TotalSeconds * Simulation.TickRateHz);
        Commands.Register<MovePlayer>(MovePlayer.Handle);
        Commands.Register<PlayerInputCommand>(PlayerInputCommand.Handle);
        Commands.Register<LootCorpse>(LootCorpse.Handle);
        Commands.Register<SleepCommand>(SleepCommand.Handle);
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

    /// <summary>The time of day now, as a fraction of a day from 0 at midnight: the Scenario's start time plus the days that have passed.</summary>
    public double TimeOfDay
    {
        get
        {
            var elapsed = (Options.Scenario?.TimeOfDay ?? DefaultStartTimeOfDay) + ((double)_tick / _dayTicks);
            return elapsed - Math.Floor(elapsed);
        }
    }

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
        var hit = session.Body.TakeHit(part, type, damage, session.Outfit.Protection(part, type));
        if (hit.IsSuccess)
        {
            Apply(session, session.Needs.Wake(WakeCause.Hurt));
        }

        return Resolve(session, hit);
    }

    /// <summary>
    /// A noise is made at a position and carries <paramref name="loudness"/> blocks. A sleeper wakes when it is within what they
    /// hear, which is less than an awake player would. The Server is the only caller, as a gunshot or a trap would be.
    /// </summary>
    public void MakeNoise(Vector3 position, double loudness)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.IsJoined && !session.IsDead && World.TryGet(session.EntityId, out var player))
            {
                Apply(session, session.Needs.HearNoise(loudness, Vector3.Distance(player.Position, position)));
            }
        }
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
        session.RespawnAtTick = Options.Death.Policy == DeathPolicy.RespawnAfterDelay ? _tick + _respawnTicks : null;
    }

    /// <summary>Gives a dead player a fresh Body and fresh Needs at the spawn point. What they carried stays in their Corpse.</summary>
    private void Respawn(PlayerSession session)
    {
        session.Body = new Body(new BodyId(session.EntityId));
        session.Needs = new Needs(Options.Needs);
        session.Effects = new CreatureEffects(new CreatureId(session.EntityId), Options.StatusEffects);
        session.Kills = 0;
        session.LifeStartTick = _tick;
        session.RespawnAtTick = null;
        Teleport(session, Options.SpawnPoint, 0f);
        session.Movement = session.Movement with { Stamina = PlayerMovement.FullStamina, Exhausted = false };
        World.UpdatePlayer(session.EntityId, default);
    }

    internal CommandResult Sleep(PlayerSession session)
    {
        var speed = new Vector2(session.Movement.Velocity.X, session.Movement.Velocity.Z).Length();
        if (speed > SleepCommand.MaxSpeedToSleep)
        {
            return CommandResult.Invalid("A player must stand still to fall asleep.");
        }

        var place = World.TryGet(session.EntityId, out var player) ? Options.RestPlaces.At(player.Position) : RestPlace.Ground;
        var result = session.Needs.Sleep(place);
        Apply(session, result);
        return result.Error switch
        {
            SurvivalError.AlreadyAsleep => CommandResult.Invalid("The player is already asleep."),
            SurvivalError.NotTired => CommandResult.Invalid("The player is not tired enough to sleep."),
            _ => CommandResult.Accepted,
        };
    }

    internal CommandResult Wake(PlayerSession session)
    {
        var result = session.Needs.Wake(WakeCause.Chosen);
        Apply(session, result);
        return result.IsSuccess ? CommandResult.Accepted : CommandResult.Invalid("The player is not asleep.");
    }

    /// <summary>Carries what happened to a player's Needs into the world: their tiredness changes the Status effect they have, and sleeping or waking is shown to everyone who sees them.</summary>
    private void Apply(PlayerSession session, SurvivalResult result)
    {
        foreach (var raised in result.Events)
        {
            switch (raised)
            {
                case FatigueLevelChanged changed:
                    ReplaceFatigueEffect(session, changed);
                    break;
                case FellAsleep or WokeUp:
                    World.UpdatePlayer(session.EntityId, new PlayerState(Dead: false, Sleeping: session.Needs.IsSleeping));
                    break;
            }
        }
    }

    private void ReplaceFatigueEffect(PlayerSession session, FatigueLevelChanged changed)
    {
        if (Options.FatigueEffects.TryGetValue(changed.From, out var old))
        {
            session.Effects.Remove(old);
        }

        if (Options.FatigueEffects.TryGetValue(changed.To, out var effect))
        {
            session.Effects.Apply(effect);
        }
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
            else if (session.IsJoined && !session.IsDead)
            {
                Apply(session, session.Needs.AdvanceFatigue(_gameTimePerTick));
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
        var vitals = new PlayerVitals(session.Movement.Stamina, session.Movement.Exhausted, (float)session.Carried.TotalMass.Kilograms);
        var frame = session.History.Begin(sequence, (ulong)_tick, session.AckedInput, vitals);
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
        var professionId = reader.ReadString();
        reader.EnsureEnd();

        var refusal = Options.Identity.Check(identity);
        if (refusal is null && PlayerCount >= Options.MaxPlayers)
        {
            refusal = (JoinRefusal.ServerFull, $"The Server is full ({Options.MaxPlayers} players).");
        }

        Profession? profession = null;
        if (refusal is null && professionId.Length > 0 && !Options.Professions.TryGet(professionId, out profession))
        {
            refusal = (JoinRefusal.UnknownProfession, $"The Server has no profession '{professionId}'.");
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

        var start = Options.Scenario is { } scenario && Options.StartPoint is { } startPoint ? startPoint(scenario.StartLocation) : Options.SpawnPoint;
        session.IsJoined = true;
        session.Name = name;
        session.EntityId = World.Spawn(EntityKind.Player, start, 0f);
        session.Movement = PlayerMoveState.At(start);
        session.Collision = Collision.Create(start, PlayerMovement.StandingHeight);
        session.Body = new Body(new BodyId(session.EntityId));
        session.Needs = new Needs(Options.Needs);
        session.Effects = new CreatureEffects(new CreatureId(session.EntityId), Options.StatusEffects);
        session.Outfit = new Outfit(Options.Wearables);
        session.Carried = new Container(_death.NextContainerId(), Options.CarryMass, Options.CarryVolume, Options.Items);
        session.LifeStartTick = _tick;
        PlayerCount++;
        if (profession is not null)
        {
            session.Profession = profession.Id;
            StartingLoadout.Grant(Options, profession, name, session.Outfit, session.Carried);
            foreach (var modifier in profession.Modifiers)
            {
                session.Modifiers.Add(modifier.From(new ModifierSource(profession.Id)));
            }
        }

        if (Options.Scenario is { } chosen)
        {
            StartIn(session, chosen.Condition);
        }

        _writer.WriteByte((byte)MessageType.JoinAccepted);
        _writer.WriteUInt32(session.EntityId);
        _writer.WriteUInt64(Options.WorldSeed);
        _writer.WriteUInt64((ulong)_tick);
        _writer.WriteByte(Simulation.TickRateHz);
        _writer.WriteByte((byte)Options.SnapshotRateHz);
        _writer.WriteSingle(start.X);
        _writer.WriteSingle(start.Y);
        _writer.WriteSingle(start.Z);
        _writer.WriteSingle(0f);
        _writer.WriteSingle((float)TimeOfDay);
        _transport.Send(session.Connection, _writer.Written, Delivery.ReliableOrdered);
    }

    /// <summary>Starts a new character in the Scenario's condition: the Needs it names, and each of its Wounds as a hit nothing worn absorbs.</summary>
    private void StartIn(PlayerSession session, StartingCondition condition)
    {
        session.Needs = Needs.Restore(session.Needs.ToSnapshot() with { Satiety = condition.Satiety, Hydration = condition.Hydration });
        foreach (var wound in condition.Wounds)
        {
            Resolve(session, session.Body.TakeHit(wound.Part, wound.DamageType, wound.Damage));
        }
    }

    private void RunCommand(PlayerSession session, ref NetReader reader)
    {
        var sequence = reader.ReadUInt32();
        var commandId = reader.ReadUInt16();
        var result = session.IsDead
            ? new CommandResult(CommandRejection.Dead, "A dead player is spectating and cannot act.")
            : session.Needs.IsSleeping && commandId != SleepCommand.CommandId && commandId != PlayerInputCommand.CommandId
                ? CommandResult.Invalid("A sleeping player cannot act.")
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
