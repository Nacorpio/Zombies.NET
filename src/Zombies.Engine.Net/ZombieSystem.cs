using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Items;
using Zombies.Domain.World;
using Zombies.Domain.Zombies;
using Zombies.Engine.Animation;
using Zombies.Engine.Core;
using Zombies.Engine.Ecs;

namespace Zombies.Engine.Net;

/// <summary>How the Server runs its zombies.</summary>
public sealed record ZombieOptions
{
    /// <summary>How long a dead zombie stays in the world, so clients can play its fall, before the Server removes it.</summary>
    public int CorpseTicks { get; init; } = 30 * Simulation.TickRateHz;
}

/// <summary>A zombie died. <see cref="Drops"/> is what it was wearing and the melee weapon it still held, which fall where it died.</summary>
public sealed record ZombieDied(uint Entity, ZombieSpec Spec, Vector3 Position, IReadOnlyList<ItemId> Drops);

/// <summary>A zombie lost the arm holding its melee weapon and let go of it. The weapon falls where the zombie stands.</summary>
public sealed record ZombieDroppedWeapon(uint Entity, ZombieSpec Spec, Vector3 Position, ItemId Weapon);

/// <summary>
/// What a zombie's attack does right now, as the Server works it out from the weapon it holds. The Server applies this to whatever
/// the attack reaches: a client never says how far or how hard a zombie hits.
/// </summary>
public sealed record ZombieAttack(double Reach, double Damage, DamageType DamageType, ItemId? Weapon)
{
    /// <summary>How far a zombie with nothing in its hands reaches, in meters.</summary>
    public const double UnarmedReach = 0.8;
}

/// <summary>What one hit on a zombie did.</summary>
public sealed record ZombieHit(uint Entity, BodyPart Part, float Distance, Vector3 Point, bool Killed, bool LostPart, IReadOnlyList<IDomainEvent> Events);

/// <summary>What a Cosmetic event on a zombie is about.</summary>
public enum CosmeticKind : byte
{
    /// <summary>A hit landed: blood spray and a decal at the point.</summary>
    Hit,

    /// <summary>A Body part was shot off: it flies away as its own body.</summary>
    PartLost,

    /// <summary>The zombie died: it collapses as a ragdoll.</summary>
    Death,
}

/// <summary>
/// A Cosmetic event: the Server telling clients what to draw, never what happened to the world. <see cref="Seed"/> is derived from the
/// zombie's seed and how many Cosmetic events it has had, so every client that plays it makes the same ragdoll, decals, and spray.
/// <see cref="Direction"/> is the unit direction the hit travelled in. Nothing here is replicated state; the Server never reads it back.
/// </summary>
public sealed record ZombieCosmetic(uint Entity, CosmeticKind Kind, BodyPart Part, Vector3 Point, Vector3 Direction, float Damage, ulong Seed);

public sealed record ZombieSpawnReport(IReadOnlyList<uint> Spawned, IReadOnlyList<string> Problems);

/// <summary>
/// The Server's zombies. A zombie is a replicated entity (<see cref="ServerWorld"/>), an ECS entity carrying the components of its
/// Traits, and a Combat <see cref="Body"/>. The Server alone decides what a hit does: it tests the ray against per-part boxes on
/// a kinematic pose of the skeleton and applies the damage to the Body, which owns Dismemberment and Missing parts (ADR 0007).
/// Only the spec, the Missing parts, and whether it is dead are replicated; every client derives the look from the spec.
/// </summary>
public sealed class ZombieSystem : ITickable
{
    private const int SaltYaw = 119;

    private sealed class ZombieData(uint id, Entity entity, ZombieSpec spec, ZombieTypeDefinition type, ZombieAppearance appearance, Body body, Animator animator, long spawnedTick)
    {
        public uint Id { get; } = id;

        public Entity Entity { get; } = entity;

        public ZombieSpec Spec { get; } = spec;

        public ZombieTypeDefinition Type { get; } = type;

        public ZombieAppearance Appearance { get; } = appearance;

        public Body Body { get; } = body;

        public Animator Animator { get; } = animator;

        public long PoseTick { get; set; } = spawnedTick;

        /// <summary>How many Weakpoint effect chances this zombie has rolled, so each roll draws its own number.</summary>
        public int EffectRolls { get; set; }

        /// <summary>How many Cosmetic events this zombie has raised, so each draws its own seed.</summary>
        public int CosmeticCount { get; set; }

        /// <summary>Whether the zombie has let go of its held weapon, by losing the arm that held it or by dying.</summary>
        public bool WeaponDropped { get; set; }

        public Vector3 Scale { get; } = new(appearance.BuildScale, appearance.HeightScale, appearance.BuildScale);
    }

    private readonly ServerWorld _world;
    private readonly Skeleton _skeleton;
    private readonly ClipSet _clips;
    private readonly ZombieOptions _options;
    private readonly Dictionary<uint, ZombieData> _zombies = [];
    private readonly Queue<(uint Id, long RemoveAt)> _corpses = new();
    private long _tick;

    /// <exception cref="ArgumentException">A Zombie type names a Trait nothing has registered.</exception>
    public ZombieSystem(ServerWorld world, ZombieCatalog catalog, TraitRegistry traits, Skeleton skeleton, ClipSet clips, ZombieOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clips);
        var unknown = catalog.Types
            .SelectMany(t => t.Traits.Where(r => !traits.IsRegistered(r.Trait)).Select(r => $"'{r.Trait}' on '{t.Id}'"))
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"No Trait is registered for {string.Join(", ", unknown)}.", nameof(traits));
        }

        // The animator refuses clips for bones the skeleton lacks; build one now so a bad rig fails at startup.
        _ = new Animator(skeleton, clips);
        _world = world;
        Catalog = catalog;
        Traits = traits;
        _skeleton = skeleton;
        _clips = clips;
        _options = options ?? new ZombieOptions();
    }

    public ZombieCatalog Catalog { get; }

    public TraitRegistry Traits { get; }

    /// <summary>The ECS world holding every zombie's Trait components.</summary>
    public EcsWorld Ecs { get; } = new();

    /// <summary>Zombies in the world, dead ones included until they are removed.</summary>
    public int Count => _zombies.Count;

    /// <summary>Raised when a zombie dies from a hit.</summary>
    public event Action<ZombieDied>? Died;

    /// <summary>Raised for each hit, severed part, and death, for clients to turn into visuals. Raising it changes nothing in the world.</summary>
    public event Action<ZombieCosmetic>? Cosmetic;

    /// <summary>Raised when a zombie that is still alive loses the arm holding its weapon. A zombie that dies drops it in <see cref="Died"/> instead.</summary>
    public event Action<ZombieDroppedWeapon>? DroppedWeapon;

    public bool TryGetEntity(uint id, out Entity entity)
    {
        if (_zombies.TryGetValue(id, out var data))
        {
            entity = data.Entity;
            return true;
        }

        entity = default;
        return false;
    }

    public bool TryGetBody(uint id, out Body body)
    {
        if (_zombies.TryGetValue(id, out var data))
        {
            body = data.Body;
            return true;
        }

        body = null!;
        return false;
    }

    public bool TryGetAppearance(uint id, out ZombieAppearance appearance)
    {
        if (_zombies.TryGetValue(id, out var data))
        {
            appearance = data.Appearance;
            return true;
        }

        appearance = null!;
        return false;
    }

    /// <summary>
    /// What the zombie's attack does now. The weapon it holds sets the reach and the damage, the damage scaled by its Level like any
    /// other damage of the type; with nothing in its hands, it hits at <see cref="ZombieAttack.UnarmedReach"/> with the damage of its type.
    /// False for a zombie that is not here or is dead.
    /// </summary>
    public bool TryGetAttack(uint id, out ZombieAttack attack)
    {
        attack = null!;
        if (!_zombies.TryGetValue(id, out var data) || !data.Body.IsAlive)
        {
            return false;
        }

        if (HeldWeaponOf(data) is { } held && Catalog.Weapons is { } weapons && weapons.TryGetWeapon(held.Item, out var weapon))
        {
            attack = new ZombieAttack(weapon.Reach, weapon.Damage * data.Type.LevelFactor(data.Spec.Level), weapon.DamageType, held.Item);
        }
        else
        {
            attack = new ZombieAttack(ZombieAttack.UnarmedReach, data.Type.DamageAt(data.Spec.Level), DamageType.Blunt, null);
        }

        return true;
    }

    /// <summary>
    /// Where the zombie's held weapon is drawn: its grip on the attach point of the hand that holds it, on the zombie's current
    /// pose. False when it holds nothing now, or the skeleton lacks the attach point. A renderer calls this each frame with the
    /// rig of <paramref name="weapon"/>, whose Item the zombie's appearance names.
    /// </summary>
    public bool TryGetHeldWeaponPlacement(uint id, WeaponRig weapon, out HeldWeaponPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        placement = null!;
        if (!_zombies.TryGetValue(id, out var data) || HeldWeaponOf(data) is not { } held)
        {
            return false;
        }

        var elapsed = (_tick - data.PoseTick) / (float)Simulation.TickRateHz;
        data.PoseTick = _tick;
        data.Animator.Update(elapsed, new AnimationInput(0f, 0f, 0f, MissingParts.From(data.Body.MissingParts)));
        var hand = held.Arm == BodyPart.RightArm ? Animation.HeldWeapon.RightHand : Animation.HeldWeapon.LeftHand;
        return Animation.HeldWeapon.TryPlace(data.Animator.Pose, hand, weapon, [], out placement);
    }

    /// <summary>Spawns a zombie from its spec. False, with the reason, when the spec names an unknown type or a Level out of its range.</summary>
    public bool TrySpawn(ZombieSpec spec, Vector3 position, float yaw, out uint id, out string? problem)
    {
        id = 0;
        if (!Catalog.TryGet(spec.Type, out var type))
        {
            problem = $"There is no Zombie type '{spec.Type}'.";
            return false;
        }

        if (spec.Level < 1 || spec.Level > type.TopLevel)
        {
            problem = $"Level {spec.Level} is outside 1 to {type.TopLevel} for '{type.Id}'.";
            return false;
        }

        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(yaw))
        {
            problem = "A zombie needs a real position and yaw.";
            return false;
        }

        var appearance = ZombieGenerator.Generate(type, spec, Catalog.Weapons);
        var missing = MissingParts.From(appearance.MissingParts);
        id = _world.SpawnZombie(position, yaw, new ZombieState(spec.Seed, (ushort)Catalog.IndexOf(type.Id), (byte)spec.Level, (byte)missing, Dead: false));

        var entity = Ecs.Create();
        foreach (var trait in type.Traits)
        {
            Traits.TryApply(trait.Trait, Ecs, entity, trait.Values);
        }

        var body = new Body(new BodyId(id), new BodyConfig { PartHealth = type.PartHealthAt(spec.Level) }, appearance.MissingParts);
        _zombies[id] = new ZombieData(id, entity, spec, type, appearance, body, new Animator(_skeleton, _clips), _tick);
        problem = null;
        return true;
    }

    /// <summary>
    /// Spawns the zombies a Settlement's plan calls for, each with a spec the world seed and the plan decide. The Level is the
    /// Region's Danger, held to the type's top Level, and the type is the one its upgrades have made it by <paramref name="worldDay"/>.
    /// A spawn that cannot happen is reported, never skipped silently.
    /// </summary>
    public ZombieSpawnReport SpawnSettlement(SettlementPlan plan, Func<int, int, float> groundHeight, int worldDay = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(groundHeight);
        var spawned = new List<uint>();
        var problems = new List<string>();
        for (var i = 0; i < plan.ZombieSpawns.Count; i++)
        {
            var spawn = plan.ZombieSpawns[i];
            var known = Catalog.TryGet(spawn.ZombieType, out var type);
            var level = known ? Math.Clamp(plan.Danger, 1, type.TopLevel) : 1;
            var seed = WorldHash.Mix(plan.Site.Seed, i, spawn.X ^ spawn.Z, SaltYaw);
            var yaw = (seed % 360UL) * (MathF.PI / 180f);
            var position = new Vector3(spawn.X + 0.5f, groundHeight(spawn.X, spawn.Z), spawn.Z + 0.5f);
            var spec = new ZombieSpec(seed, spawn.ZombieType, level);
            if (known)
            {
                spec = spec.At(Catalog, worldDay);
            }

            if (TrySpawn(spec, position, yaw, out var id, out var problem))
            {
                spawned.Add(id);
            }
            else
            {
                problems.Add($"Zombie {i} of the Settlement at {plan.Site.X},{plan.Site.Z}: {problem}");
            }
        }

        return new ZombieSpawnReport(spawned, problems);
    }

    /// <summary>
    /// Fires a ray into the world and applies <paramref name="damage"/> to the nearest living zombie it hits, on the Body part it
    /// hits. Null when it hits nothing. A hit inside a Weakpoint of the type does more damage, and the result then carries a
    /// <see cref="WeakpointHit"/> event. The Server is the only caller: a client asks to attack and never says what it hit.
    /// </summary>
    public ZombieHit? Hit(Vector3 origin, Vector3 direction, float maxDistance, DamageType type, double damage, AttackKind attack = AttackKind.Ranged)
    {
        if (!float.IsFinite(maxDistance) || maxDistance <= 0)
        {
            return null;
        }

        ZombieData? nearestZombie = null;
        PartHit nearest = default;
        foreach (var data in _zombies.Values)
        {
            if (!data.Body.IsAlive || !_world.TryGet(data.Id, out var state))
            {
                continue;
            }

            // A zombie is under 3 m tall, so one farther than the reach plus that cannot be hit.
            if (Vector3.DistanceSquared(state.Position, origin) > (maxDistance + 3f) * (maxDistance + 3f))
            {
                continue;
            }

            var elapsed = (_tick - data.PoseTick) / (float)Simulation.TickRateHz;
            data.PoseTick = _tick;
            data.Animator.Update(elapsed, new AnimationInput(0f, 0f, 0f, MissingParts.From(data.Body.MissingParts)));
            if (PartHitTest.TryRaycast(data.Animator.Pose, state.Position, state.Yaw, data.Scale, origin, direction, maxDistance, out var hit)
                && (nearestZombie is null || hit.Distance < nearest.Distance))
            {
                nearestZombie = data;
                nearest = hit;
            }
        }

        return nearestZombie is null ? null : Apply(nearestZombie, nearest, direction, type, damage, attack);
    }

    public void Tick(long tick)
    {
        _tick = tick;
        while (_corpses.Count > 0 && _corpses.Peek().RemoveAt <= tick)
        {
            Remove(_corpses.Dequeue().Id);
        }
    }

    private ZombieHit? Apply(ZombieData data, PartHit hit, Vector3 direction, DamageType type, double damage, AttackKind attack)
    {
        var weakpoint = Catalog.WeakpointsOf(data.Type)?.Find(hit.Part, hit.BoxOrigin, hit.BoxDirection, attack);
        var dealt = weakpoint is null ? damage : damage * weakpoint.CriticalMultiplier;
        var result = data.Body.TakeHit(hit.Part, type, dealt);
        if (!result.IsSuccess)
        {
            return null;
        }

        var events = result.Events;
        if (weakpoint is not null)
        {
            events = [.. events, new WeakpointHit(data.Id, weakpoint.Name, hit.Part, dealt, RollEffect(data, weakpoint, dealt))];
        }

        var killed = result.Events.OfType<BodyDied>().Any();
        var lost = result.Events.OfType<BodyPartLost>().Any();
        if (killed || lost)
        {
            var missing = (byte)MissingParts.From(data.Body.MissingParts);
            _world.TryGet(data.Id, out var state);
            _world.UpdateZombie(data.Id, state.Zombie with { Missing = missing, Dead = !data.Body.IsAlive });
        }

        ItemId? dropped = null;
        if (!data.WeaponDropped && data.Appearance.HeldWeapon is { } held && (killed || !held.IsHeldWith(data.Body.MissingParts)))
        {
            data.WeaponDropped = true;
            dropped = held.Item;
        }

        if (killed)
        {
            _world.TryGet(data.Id, out var state);
            _corpses.Enqueue((data.Id, _tick + _options.CorpseTicks));
            IReadOnlyList<ItemId> drops = dropped is { } weapon ? [.. data.Appearance.WornItems, weapon] : data.Appearance.WornItems;
            Died?.Invoke(new ZombieDied(data.Id, data.Spec, state.Position, drops));
        }
        else if (dropped is { } fallen)
        {
            _world.TryGet(data.Id, out var state);
            DroppedWeapon?.Invoke(new ZombieDroppedWeapon(data.Id, data.Spec, state.Position, fallen));
        }

        RaiseCosmetic(data, CosmeticKind.Hit, hit.Part, hit.Point, direction, dealt);
        if (lost)
        {
            var part = result.Events.OfType<BodyPartLost>().First().Part;
            RaiseCosmetic(data, CosmeticKind.PartLost, part, hit.Point, direction, dealt);
        }

        if (killed)
        {
            RaiseCosmetic(data, CosmeticKind.Death, hit.Part, hit.Point, direction, dealt);
        }

        return new ZombieHit(data.Id, hit.Part, hit.Distance, hit.Point, killed, lost, events);
    }

    private void RaiseCosmetic(ZombieData data, CosmeticKind kind, BodyPart part, Vector3 point, Vector3 direction, double damage)
    {
        var seed = DeterministicRandom.Combine(data.Spec.Seed, (ulong)data.CosmeticCount++);
        var unit = direction.LengthSquared() > 0f ? Vector3.Normalize(direction) : Vector3.UnitZ;
        Cosmetic?.Invoke(new ZombieCosmetic(data.Id, kind, part, point, unit, (float)damage, seed));
    }

    /// <summary>
    /// The effect of a Weakpoint when enough damage landed and the chance came up. The roll comes from the zombie's seed and how many
    /// it has rolled, so the same fight plays out the same way.
    /// </summary>
    private static WeakpointEffect? RollEffect(ZombieData data, Weakpoint weakpoint, double dealt)
    {
        if (weakpoint.Effect is null || dealt < weakpoint.EffectThreshold)
        {
            return null;
        }

        var random = new DeterministicRandom(DeterministicRandom.Combine(data.Spec.Seed, (ulong)data.EffectRolls++));
        return (int)random.NextBelow(ZombieTypeDefinition.BasisPoints) < weakpoint.EffectChanceBasis ? weakpoint.Effect : null;
    }

    /// <summary>The weapon the zombie holds right now: the one it spawned with, unless it has let go of it.</summary>
    private static ZombieHeldWeapon? HeldWeaponOf(ZombieData data) =>
        !data.WeaponDropped && data.Appearance.HeldWeapon is { } held && held.IsHeldWith(data.Body.MissingParts) ? held : null;

    private void Remove(uint id)
    {
        if (_zombies.Remove(id, out var data))
        {
            Ecs.Destroy(data.Entity);
        }

        _world.Despawn(id);
    }
}
