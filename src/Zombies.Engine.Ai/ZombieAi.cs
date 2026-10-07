using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Zombies.Domain.Zombies;
using Zombies.Engine.Animation;
using Zombies.Engine.Core;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;

namespace Zombies.Engine.Ai;

/// <summary>How the Server's zombies think. The defaults fit the slice's budget of 200 zombies and 4 players in 12 ms a tick.</summary>
public sealed record ZombieAiOptions
{
    public DetailCaps Caps { get; init; } = new();

    public NavigationOptions Navigation { get; init; } = new();

    public PathPlannerOptions Planner { get; init; } = new();

    /// <summary>How long route searches and flow fields may take in one tick. A search that needs longer continues next tick.</summary>
    public TimeSpan PathfindingBudget { get; init; } = TimeSpan.FromMilliseconds(2);

    /// <summary>How often detail levels are assigned again, in ticks.</summary>
    public int DetailIntervalTicks { get; init; } = 10;

    /// <summary>How many zombies must share a target before they follow one flow field instead of planning their own routes.</summary>
    public int HordeSize { get; init; } = 4;

    /// <summary>How many flow fields can exist at once. A horde without one plans routes zombie by zombie.</summary>
    public int MaxFlowFields { get; init; } = 8;

    /// <summary>How often, in ticks, a route or field toward a moving player is planned again.</summary>
    public int RetargetTicks { get; init; } = 15;

    public float FieldOfViewDegrees { get; init; } = 120f;

    /// <summary>A player this close is noticed whichever way the zombie faces.</summary>
    public float NoticeMeters { get; init; } = 2.5f;

    /// <summary>Height of a zombie's eyes above its feet.</summary>
    public float EyeHeight { get; init; } = 1.6f;

    /// <summary>How long a zombie keeps chasing a player it has lost sight of, before it goes to look where it last saw them.</summary>
    public int MemoryTicks { get; init; } = 5 * Simulation.TickRateHz;

    /// <summary>How long a zombie looks for the source of a sound before it gives up.</summary>
    public int InvestigateTicks { get; init; } = 30 * Simulation.TickRateHz;

    /// <summary>How near a zombie must come to where it was going to have arrived.</summary>
    public float ArriveMeters { get; init; } = 1.5f;

    /// <summary>Ticks between a zombie's blows on a door.</summary>
    public int AttackIntervalTicks { get; init; } = Simulation.TickRateHz;

    /// <summary>How far the sound of a blow on a door carries.</summary>
    public float DoorHitSoundMeters { get; init; } = 16f;

    /// <summary>A zombie missing a leg crawls at this fraction of its speed.</summary>
    public float CrawlSpeedFactor { get; init; } = 0.35f;

    /// <summary>Footsteps of a player moving faster than <see cref="WalkingSpeed"/> carry this far; crouching is silent.</summary>
    public float WalkingFootstepMeters { get; init; } = 8f;

    /// <summary>Footsteps of a player moving faster than <see cref="SprintingSpeed"/> carry this far.</summary>
    public float SprintingFootstepMeters { get; init; } = 16f;

    public float WalkingSpeed { get; init; } = 2.5f;

    public float SprintingSpeed { get; init; } = 5.5f;
}

public enum ZombieMindState
{
    /// <summary>Stands where it is, listening.</summary>
    Idle,

    /// <summary>Goes to where it heard a sound, or last saw a player.</summary>
    Investigating,

    /// <summary>Follows a player it can see, or saw a moment ago.</summary>
    Chasing,

    /// <summary>Hits a closed door that stands in its way.</summary>
    BreakingDoor,
}

/// <summary>What one zombie is doing, for tests, the debug overlay, and the harness.</summary>
public readonly record struct ZombieMindView(ZombieMindState State, DetailLevel Detail, Vector3 Goal, uint TargetPlayer, bool FollowsFlowField);

/// <summary>
/// The Server's zombie AI. Each tick it publishes the living players to the <see cref="PerceptionBus"/>, lets each zombie hear and
/// look within its Zombie type's senses, and moves it toward what it noticed. Zombies get one of three <see cref="DetailLevel"/>s
/// under hard caps per player. Zombies sharing a target follow one <see cref="FlowField"/> as a horde; the rest plan their own
/// routes with hierarchical A*. All searching happens inside <see cref="ZombieAiOptions.PathfindingBudget"/>. A zombie walks up one
/// block at most and never climbs, and a closed door in its way is hit until it breaks. The zombie's position and facing reach
/// clients through the replicated <see cref="ServerWorld"/>; nothing else about its mind is sent. A steady-state tick allocates nothing.
/// </summary>
public sealed class ZombieAi : ITickable
{
    private const long PlayerKeyFlag = 1L << 62;
    private const int MaxPlayers = 16;
    private const int FootstepIntervalTicks = 10;

    private readonly ServerWorld _world;
    private readonly ZombieSystem _zombies;
    private readonly ZombieAiOptions _options;
    private readonly DetailAssigner _detail;
    private readonly float _fieldOfViewCos;
    private readonly Dictionary<uint, Mind> _minds = [];
    private readonly List<Mind> _active = new(256);
    private readonly List<uint> _gone = [];
    private readonly Dictionary<long, Group> _groups = [];
    private readonly Dictionary<long, HordeField> _fields = [];
    private readonly Stack<HordeField> _freeFields = new();
    private readonly List<long> _staleFields = [];
    private readonly Vector3[] _players = new Vector3[MaxPlayers];
    private readonly Dictionary<uint, Vector3> _lastPlayerPositions = [];
    private Vector3[] _positions = new Vector3[256];
    private DetailLevel[] _levels = new DetailLevel[256];
    private int _playerCount;
    private int _lastAssignedCount = -1;
    private int _lastAssignedPlayers = -1;
    private long _tick;

    public ZombieAi(ServerWorld world, ZombieSystem zombies, INavigationTerrain terrain, DoorMap? doors = null, ZombieAiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(zombies);
        ArgumentNullException.ThrowIfNull(terrain);
        _world = world;
        _zombies = zombies;
        _options = options ?? new ZombieAiOptions();
        if (_options.DetailIntervalTicks < 1 || _options.HordeSize < 1 || _options.MaxFlowFields < 0 || _options.RetargetTicks < 1 || _options.AttackIntervalTicks < 1
            || _options.PathfindingBudget < TimeSpan.Zero || _options.FieldOfViewDegrees is <= 0 or > 360)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Intervals and the horde size must be at least 1, the budget not negative, and the field of view up to 360 degrees.");
        }

        Navigation = new Navigation(terrain, doors, _options.Navigation);
        Planner = new PathPlanner(Navigation, _options.Planner);
        Perception = new PerceptionBus(Navigation);
        _detail = new DetailAssigner(_options.Caps);
        _fieldOfViewCos = MathF.Cos(_options.FieldOfViewDegrees * 0.5f * (MathF.PI / 180f));
    }

    public Navigation Navigation { get; }

    public PathPlanner Planner { get; }

    public PerceptionBus Perception { get; }

    public DoorMap Doors => Navigation.Doors;

    public ZombieAiOptions Options => _options;

    /// <summary>How long the last tick took, searching included.</summary>
    public TimeSpan LastTickTime { get; private set; }

    /// <summary>Flow fields in use.</summary>
    public int FlowFieldCount => _fields.Count;

    /// <summary>Living zombies the AI moved or could move during the last tick.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>How many living zombies have the level.</summary>
    public int CountAt(DetailLevel level)
    {
        var count = 0;
        foreach (var mind in _active)
        {
            if (mind.Detail == level)
            {
                count++;
            }
        }

        return count;
    }

    public bool TryGetMind(uint zombie, out ZombieMindView view)
    {
        if (_minds.TryGetValue(zombie, out var mind))
        {
            var state = mind.Door is not null ? ZombieMindState.BreakingDoor : mind.State;
            view = new ZombieMindView(state, mind.Detail, mind.Goal, mind.TargetPlayer, mind.Field is { Field.IsReady: true });
            return true;
        }

        view = default;
        return false;
    }

    public void Tick(long tick)
    {
        var started = Stopwatch.GetTimestamp();
        _tick = tick;
        Perception.BeginTick();
        GatherPlayers();
        GatherZombies();
        if (tick % _options.DetailIntervalTicks == 0 || _active.Count != _lastAssignedCount || _playerCount != _lastAssignedPlayers)
        {
            AssignDetail();
        }

        foreach (var mind in _active)
        {
            Perceive(mind);
        }

        FormHordes();
        foreach (var mind in _active)
        {
            var interval = UpdateInterval(mind.Detail);
            if ((tick + mind.Id) % interval == 0)
            {
                Act(mind, interval / (float)Simulation.TickRateHz);
            }
        }

        Planner.Run(_options.PathfindingBudget);
        LastTickTime = Stopwatch.GetElapsedTime(started);
    }

    private static int UpdateInterval(DetailLevel level) => level switch
    {
        DetailLevel.Full => 1,
        DetailLevel.Reduced => 2,
        _ => 10,
    };

    private static int SightInterval(DetailLevel level) => level switch
    {
        DetailLevel.Full => 1,
        DetailLevel.Reduced => 6,
        _ => 0,
    };

    private void GatherPlayers()
    {
        _playerCount = 0;
        foreach (ref readonly var entity in _world.Entities)
        {
            if (entity.Kind != EntityKind.Player || entity.Player.Dead)
            {
                continue;
            }

            if (_playerCount < MaxPlayers)
            {
                _players[_playerCount] = entity.Position;
                _playerCount++;
            }

            Perception.Publish(new SightTarget(entity.Id, entity.Position + new Vector3(0, PlayerMovement.EyeHeight - 0.2f, 0)));
            ref var last = ref CollectionsMarshal.GetValueRefOrAddDefault(_lastPlayerPositions, entity.Id, out var known);
            if (known && _tick % FootstepIntervalTicks == 0)
            {
                var moved = entity.Position - last;
                var speed = new Vector2(moved.X, moved.Z).Length() * Simulation.TickRateHz / FootstepIntervalTicks;
                // Faster than anyone runs is a teleport or a respawn, which makes no sound; so is creeping.
                var reach = speed > _options.SprintingSpeed * 2 ? 0f
                    : speed > _options.SprintingSpeed ? _options.SprintingFootstepMeters
                    : speed > _options.WalkingSpeed ? _options.WalkingFootstepMeters
                    : 0f;
                if (reach > 0)
                {
                    Perception.Emit(entity.Position, reach, entity.Id);
                }
            }

            if (!known || _tick % FootstepIntervalTicks == 0)
            {
                last = entity.Position;
            }
        }
    }

    private void GatherZombies()
    {
        _active.Clear();
        foreach (ref readonly var entity in _world.Entities)
        {
            if (entity.Kind != EntityKind.Zombie || entity.Zombie.Dead)
            {
                continue;
            }

            if (!_minds.TryGetValue(entity.Id, out var mind))
            {
                mind = CreateMind(entity);
                _minds[entity.Id] = mind;
            }

            mind.Position = entity.Position;
            mind.Yaw = entity.Yaw;
            mind.Crawling = ((MissingPartSet)entity.Zombie.Missing).IsCrawling();
            mind.SeenTick = _tick;
            _active.Add(mind);
        }

        if (_minds.Count == _active.Count)
        {
            return;
        }

        foreach (var (id, mind) in _minds)
        {
            if (mind.SeenTick != _tick)
            {
                _gone.Add(id);
            }
        }

        foreach (var id in _gone)
        {
            if (_minds.Remove(id, out var mind))
            {
                Planner.Cancel(mind.Path);
            }
        }

        _gone.Clear();
    }

    private Mind CreateMind(in EntityState entity)
    {
        var type = _zombies.Catalog.At(entity.Zombie.Type);
        var speed = type.Speed;
        if (_zombies.TryGetEntity(entity.Id, out var handle))
        {
            if (_zombies.Ecs.TryGet<Shambler>(handle, out var shambler))
            {
                speed *= shambler.SpeedMultiplier;
            }

            if (_zombies.Ecs.TryGet<Runner>(handle, out var runner))
            {
                speed *= runner.SpeedMultiplier;
            }
        }

        var senses = new Senses((float)type.SightMeters, (float)type.HearingMeters, _fieldOfViewCos, _options.NoticeMeters);
        return new Mind(entity.Id, (float)speed, type.DamageAt(entity.Zombie.Level), senses, Planner.CreatePath());
    }

    private void AssignDetail()
    {
        var count = _active.Count;
        if (_positions.Length < count)
        {
            _positions = new Vector3[Math.Max(count, _positions.Length * 2)];
            _levels = new DetailLevel[_positions.Length];
        }

        for (var i = 0; i < count; i++)
        {
            _positions[i] = _active[i].Position;
        }

        _detail.Assign(_players.AsSpan(0, _playerCount), _positions.AsSpan(0, count), _levels);
        for (var i = 0; i < count; i++)
        {
            _active[i].Detail = _levels[i];
        }

        _lastAssignedCount = count;
        _lastAssignedPlayers = _playerCount;
    }

    private void Perceive(Mind mind)
    {
        var remembered = mind.State == ZombieMindState.Chasing && _tick - mind.LastSeenTick <= _options.MemoryTicks;
        var sight = SightInterval(mind.Detail);
        if (sight > 0 && (_tick + mind.Id) % sight == 0
            && Perception.TrySee(mind.Position + new Vector3(0, _options.EyeHeight, 0), mind.Yaw, mind.Senses, out var seen))
        {
            if (mind.State != ZombieMindState.Chasing || mind.TargetPlayer != seen.Entity)
            {
                mind.StateSinceTick = _tick;
            }

            mind.State = ZombieMindState.Chasing;
            mind.TargetPlayer = seen.Entity;
            mind.LastSeenTick = _tick;
            remembered = true;
        }
        else if (!remembered && Perception.TryHear(mind.Position, mind.Senses, includeCommotion: mind.State == ZombieMindState.Idle, out var sound))
        {
            Investigate(mind, sound.Position);
        }

        switch (mind.State)
        {
            case ZombieMindState.Chasing when remembered && _world.TryGet(mind.TargetPlayer, out var player) && !player.Player.Dead:
                mind.Goal = player.Position;
                break;
            case ZombieMindState.Chasing when _world.TryGet(mind.TargetPlayer, out var lost) && !lost.Player.Dead:
                // Out of sight for too long: go and look where the zombie last knew the player to be.
                Investigate(mind, mind.Goal);
                break;
            case ZombieMindState.Chasing:
                Forget(mind);
                break;
            case ZombieMindState.Investigating when Arrived(mind) || _tick - mind.StateSinceTick > _options.InvestigateTicks:
                Forget(mind);
                break;
        }
    }

    private void Investigate(Mind mind, Vector3 position)
    {
        if (mind.State != ZombieMindState.Investigating || Vector3.DistanceSquared(mind.Goal, position) > 1f)
        {
            mind.StateSinceTick = _tick;
        }

        mind.State = ZombieMindState.Investigating;
        mind.TargetPlayer = 0;
        mind.Goal = position;
    }

    private static void Forget(Mind mind)
    {
        mind.State = ZombieMindState.Idle;
        mind.TargetPlayer = 0;
        mind.Field = null;
        mind.Door = null;
        mind.Path.Clear();
    }

    private bool Arrived(Mind mind)
    {
        var offset = mind.Goal - mind.Position;
        return new Vector2(offset.X, offset.Z).Length() <= _options.ArriveMeters && MathF.Abs(offset.Y) < 2f;
    }

    private static long TargetKey(Mind mind, NavCell goal) => mind.State == ZombieMindState.Chasing ? PlayerKeyFlag | mind.TargetPlayer : goal.Key;

    /// <summary>Zombies sharing a target follow one flow field toward it; a field nobody follows any more goes back to the pool.</summary>
    private void FormHordes()
    {
        _groups.Clear();
        foreach (var mind in _active)
        {
            if (mind.State != ZombieMindState.Idle)
            {
                mind.GoalCell = GoalCell(mind.Goal);
                ref var group = ref CollectionsMarshal.GetValueRefOrAddDefault(_groups, TargetKey(mind, mind.GoalCell), out _);
                group.Count++;
                group.Farthest = MathF.Max(group.Farthest, Vector3.Distance(mind.Position, mind.Goal));
            }
        }

        foreach (var mind in _active)
        {
            if (mind.State == ZombieMindState.Idle)
            {
                mind.Field = null;
                continue;
            }

            // The field reaches the farthest member of the horde with room to spare for walking round things, and no farther.
            var key = TargetKey(mind, mind.GoalCell);
            var group = _groups[key];
            var reach = (int)(((group.Farthest * 1.5f) + 12f) * Navigation.StraightCost);
            mind.Field = group.Count >= _options.HordeSize ? AcquireField(key, mind.GoalCell, reach) : null;
        }

        foreach (var (key, field) in _fields)
        {
            if (field.LastUsedTick != _tick)
            {
                _staleFields.Add(key);
            }
        }

        foreach (var key in _staleFields)
        {
            if (_fields.Remove(key, out var field))
            {
                Planner.Cancel(field.Field);
                _freeFields.Push(field);
            }
        }

        _staleFields.Clear();
    }

    private NavCell GoalCell(Vector3 goal) => Navigation.TrySnap(goal, out var cell) ? cell : NavCell.Containing(goal);

    private HordeField? AcquireField(long key, NavCell target, int reach)
    {
        if (!_fields.TryGetValue(key, out var field))
        {
            if (_freeFields.Count > 0)
            {
                field = _freeFields.Pop();
            }
            else if (_fields.Count < _options.MaxFlowFields)
            {
                field = new HordeField(Planner.CreateFlowField());
            }
            else
            {
                return null;
            }

            field.Reset();
            _fields[key] = field;
            Planner.Request(field.Field, target, reach);
            field.RequestedTick = _tick;
        }
        else if (field.LastUsedTick != _tick
            && (field.Field.RequestedTarget != target || reach > field.Field.RequestedReach)
            && _tick - field.RequestedTick >= _options.RetargetTicks)
        {
            Planner.Request(field.Field, target, reach);
            field.RequestedTick = _tick;
        }

        field.LastUsedTick = _tick;
        return field;
    }

    private void Act(Mind mind, float seconds)
    {
        if (mind.State == ZombieMindState.Idle)
        {
            return;
        }

        if (!mind.HasCell)
        {
            mind.HasCell = Navigation.TrySnap(mind.Position, out mind.Cell);
            if (!mind.HasCell)
            {
                return;
            }
        }

        if (mind.Door is { } door)
        {
            HitDoor(mind, door);
            return;
        }

        var speed = mind.Speed * (mind.Crawling ? _options.CrawlSpeedFactor : 1f);
        var remaining = speed * seconds;
        var position = mind.Position;
        var heading = Vector3.Zero;
        for (var steps = 0; steps < 8 && remaining > 1e-4f; steps++)
        {
            if (!TryNextCell(mind, out var next, out var onPath))
            {
                break;
            }

            if (Navigation.IsBlockedByDoor(next))
            {
                if (FindDoor(next, out var blocking))
                {
                    mind.Door = blocking;
                    mind.NextHitTick = _tick;
                    heading = next.Floor - position;
                }

                break;
            }

            if (!Navigation.TryMoveCost(mind.Cell, next, out _))
            {
                // The route no longer fits the world, as when the zombie was pushed off it. Plan again.
                mind.Path.Clear();
                mind.HasCell = Navigation.TrySnap(position, out mind.Cell);
                break;
            }

            var target = next.Floor;
            var offset = new Vector3(target.X - position.X, 0, target.Z - position.Z);
            var distance = offset.Length();
            heading = offset;
            if (distance <= remaining)
            {
                position = target;
                mind.Cell = next;
                remaining -= distance;
                if (onPath)
                {
                    mind.Path.Advance();
                }
            }
            else
            {
                position += offset * (remaining / distance);
                position.Y = distance < 0.5f ? next.Y : mind.Cell.Y;
                remaining = 0;
            }
        }

        var yaw = heading.LengthSquared() > 1e-6f ? MathF.Atan2(heading.X, -heading.Z) : mind.Yaw;
        if (position != mind.Position || yaw != mind.Yaw)
        {
            mind.Position = position;
            mind.Yaw = yaw;
            _world.Move(mind.Id, position, yaw);
        }
    }

    private bool FindDoor(NavCell cell, out Door door)
    {
        for (var dy = 0; dy < Navigation.Options.Height; dy++)
        {
            if (Doors.TryGetAt(cell.X, cell.Y + dy, cell.Z, out door) && door.IsClosed)
            {
                return true;
            }
        }

        door = null!;
        return false;
    }

    private void HitDoor(Mind mind, Door door)
    {
        if (!door.IsClosed)
        {
            mind.Door = null;
            return;
        }

        if (_tick < mind.NextHitTick)
        {
            return;
        }

        mind.NextHitTick = _tick + _options.AttackIntervalTicks;
        Perception.Emit(door.Cell.Floor, _options.DoorHitSoundMeters, mind.Id, SoundKind.Commotion);
        if (Doors.Damage(door, mind.Damage))
        {
            mind.Door = null;
        }
    }

    /// <summary>The cell to walk into next: from the horde's flow field when it covers the zombie, otherwise from the zombie's own route.</summary>
    private bool TryNextCell(Mind mind, out NavCell next, out bool onPath)
    {
        onPath = false;
        if (mind.Field is { } horde && horde.Field.Covers(mind.Cell))
        {
            if (horde.Field.TryGetNext(mind.Cell, out next))
            {
                return true;
            }

            if (mind.Cell == mind.GoalCell)
            {
                return false;
            }

            // At the field's target while the goal has moved on and the field has yet to follow: close the gap alone.
        }

        var path = mind.Path;
        var goal = mind.GoalCell;
        var sinceRequest = _tick - mind.PathRequestedTick;
        var goalMoved = path.Status != PathStatus.None && Moved(path.Goal, goal);
        if (path.HasNext && !(goalMoved && sinceRequest >= _options.RetargetTicks))
        {
            next = path.PeekNext();
            onPath = true;
            return true;
        }

        next = default;
        if (mind.Cell == goal || path.Status == PathStatus.Pending)
        {
            return false;
        }

        // A new goal is planned for at once; a goal that could not be reached is tried again after a while.
        var wait = path.Status == PathStatus.None || path.Goal != goal ? (goalMoved ? _options.RetargetTicks : 0) : 2 * Simulation.TickRateHz;
        if (sinceRequest >= wait)
        {
            Planner.Request(path, mind.Cell, goal);
            mind.PathRequestedTick = _tick;
        }

        return false;

        static bool Moved(NavCell a, NavCell b) => Math.Abs(a.X - b.X) + Math.Abs(a.Z - b.Z) + Math.Abs(a.Y - b.Y) > 2;
    }

    private sealed class Mind(uint id, float speed, double damage, Senses senses, NavPath path)
    {
        public uint Id { get; } = id;

        public float Speed { get; } = speed;

        public double Damage { get; } = damage;

        public Senses Senses { get; } = senses;

        public NavPath Path { get; } = path;

        public ZombieMindState State { get; set; }

        public DetailLevel Detail { get; set; }

        public uint TargetPlayer { get; set; }

        public Vector3 Goal { get; set; }

        public NavCell GoalCell { get; set; }

        public long LastSeenTick { get; set; }

        public long StateSinceTick { get; set; }

        public long PathRequestedTick { get; set; }

        public HordeField? Field { get; set; }

        public Door? Door { get; set; }

        public long NextHitTick { get; set; }

        public Vector3 Position { get; set; }

        public float Yaw { get; set; }

        public bool Crawling { get; set; }

        public long SeenTick { get; set; }

        public bool HasCell;

        public NavCell Cell;
    }

    private struct Group
    {
        public int Count;
        public float Farthest;
    }

    private sealed class HordeField(FlowField field)
    {
        public FlowField Field { get; } = field;

        public long LastUsedTick { get; set; }

        public long RequestedTick { get; set; }

        public void Reset()
        {
            Field.Invalidate();
            LastUsedTick = 0;
            RequestedTick = 0;
        }
    }
}
