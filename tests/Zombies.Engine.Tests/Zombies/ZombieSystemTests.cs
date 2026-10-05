using System.Numerics;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Zombies;
using Zombies.Engine.Animation;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Tests.Rigs;

namespace Zombies.Engine.Tests;

public sealed class ZombieSystemTests
{
    private readonly record struct Screamer(double Volume);

    private const string Plain = """
        {
          "id": "t:zombie/plain",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4, "maxLevel": 5, "perLevelBonus": 0.5 },
          "senses": { "sight": 20, "hearing": 30 },
          "traits": [ { "trait": "base:trait/runner", "values": { "speedMultiplier": 3 } } ],
          "appearance": { "skinTones": ["#8fa07a"] },
          "outfit": {
            "clothing": [ { "item": "t:item/shirt", "weight": 1 }, { "item": "t:item/jeans", "weight": 1 } ],
            "clothingCount": { "min": 2, "max": 2 },
            "headwear": [ { "item": "t:item/cap", "weight": 1 } ],
            "backpacks": [ { "item": "t:item/pack", "weight": 1 } ]
          },
          "missingParts": []
        }
        """;

    private static ZombieSystem System(out ServerWorld world, string[]? types = null, Action<TraitRegistry>? traits = null, ZombieOptions? options = null)
    {
        world = new ServerWorld();
        var registry = new TraitRegistry();
        BaseTraits.Register(registry);
        traits?.Invoke(registry);
        var catalog = new ZombieCatalog((types ?? [Plain]).Select(ZombieTypeJson.Parse));
        return new ZombieSystem(world, catalog, registry, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"), options);
    }

    private static uint Spawn(ZombieSystem system, ulong seed = 1, string type = "t:zombie/plain", int level = 1, Vector3? at = null, float yaw = 0f)
    {
        Assert.True(system.TrySpawn(new ZombieSpec(seed, type, level), at ?? Vector3.Zero, yaw, out var id, out var problem), problem);
        return id;
    }

    // From the front (the skeleton faces -Z), at the height of the part.
    private static ZombieHit? Shoot(ZombieSystem system, float x, float y, double damage = 100, DamageType type = DamageType.Pierce) =>
        system.Hit(new Vector3(x, y, -5), Vector3.UnitZ, 20f, type, damage);

    [Fact]
    public void ASpawnedZombie_IsAReplicatedEntityCarryingItsSpec()
    {
        var system = System(out var world);

        var id = Spawn(system, seed: 99, level: 3, at: new Vector3(4, 70, 5), yaw: 1.5f);

        Assert.True(world.TryGet(id, out var state));
        Assert.Equal(EntityKind.Zombie, state.Kind);
        Assert.Equal(new Vector3(4, 70, 5), state.Position);
        Assert.Equal(1.5f, state.Yaw);
        Assert.Equal(new ZombieState(99, 0, 3, 0, Dead: false), state.Zombie);
        Assert.Equal(1, system.Count);
    }

    [Fact]
    public void TheSameSpecGivesTheSameAppearance_WhereverItSpawns()
    {
        var first = System(out _);
        var second = System(out _);
        var a = Spawn(first, seed: 7, at: new Vector3(1, 2, 3));
        var b = Spawn(second, seed: 7, at: new Vector3(90, 5, -40));

        Assert.True(first.TryGetAppearance(a, out var left));
        Assert.True(second.TryGetAppearance(b, out var right));
        Assert.Equal(left, right);
        Assert.Equal(ZombieGenerator.Generate(first.Catalog.At(0), new ZombieSpec(7, "t:zombie/plain", 1)), left);
    }

    [Fact]
    public void ATypesTraitsBecomeEcsComponentsOnItsEntity()
    {
        var system = System(out _);
        var id = Spawn(system);

        Assert.True(system.TryGetEntity(id, out var entity));
        Assert.True(system.Ecs.TryGet<Runner>(entity, out var runner));
        Assert.Equal(3, runner.SpeedMultiplier);
        Assert.False(system.Ecs.Has<Bloater>(entity));
    }

    [Fact]
    public void AModCanAddATrait_AndZombieTypesCanListIt()
    {
        var screamer = Plain.Replace("base:trait/runner", "t:trait/screamer", StringComparison.Ordinal).Replace("\"speedMultiplier\": 3", "\"volume\": 7", StringComparison.Ordinal);
        var system = System(
            out _,
            [screamer],
            traits: registry => registry.Register("t:trait/screamer", (world, entity, values) => world.Set(entity, new Screamer(values["volume"]))));

        var id = Spawn(system);

        Assert.True(system.TryGetEntity(id, out var entity));
        Assert.True(system.Ecs.TryGet<Screamer>(entity, out var component));
        Assert.Equal(7, component.Volume);
    }

    [Fact]
    public void ATraitNoCodeRegistered_FailsAtStartupNamingIt()
    {
        var screamer = Plain.Replace("base:trait/runner", "t:trait/screamer", StringComparison.Ordinal);

        var error = Assert.Throws<ArgumentException>(() => System(out _, [screamer]));

        Assert.Contains("t:trait/screamer", error.Message, StringComparison.Ordinal);
        Assert.Contains("t:zombie/plain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATraitIdCannotBeRegisteredTwice()
    {
        var registry = new TraitRegistry();
        BaseTraits.Register(registry);

        Assert.Throws<InvalidOperationException>(() => BaseTraits.Register(registry));
        Assert.Equal([BaseTraits.BloaterId, BaseTraits.RunnerId, BaseTraits.ShamblerId], registry.Traits);
    }

    [Fact]
    public void ABadSpec_IsRefusedWithAReason()
    {
        var system = System(out var world);

        Assert.False(system.TrySpawn(new ZombieSpec(1, "t:zombie/none", 1), Vector3.Zero, 0f, out _, out var unknown));
        Assert.False(system.TrySpawn(new ZombieSpec(1, "t:zombie/plain", 6), Vector3.Zero, 0f, out _, out var level));
        Assert.False(system.TrySpawn(new ZombieSpec(1, "t:zombie/plain", 1), new Vector3(float.NaN, 0, 0), 0f, out _, out var position));

        Assert.Contains("t:zombie/none", unknown, StringComparison.Ordinal);
        Assert.Contains("Level 6", level, StringComparison.Ordinal);
        Assert.NotNull(position);
        Assert.Equal(0, world.Count);
        Assert.Equal(0, system.Count);
    }

    [Fact]
    public void AZombieSpawnsWithoutItsMissingParts_AndTheServerReplicatesThem()
    {
        var armless = Plain.Replace("\"missingParts\": []", "\"missingParts\": [ { \"part\": \"leftArm\", \"chance\": 1 }, { \"part\": \"rightLeg\", \"chance\": 1 } ]", StringComparison.Ordinal);
        var system = System(out var world, [armless]);

        var id = Spawn(system);

        Assert.True(world.TryGet(id, out var state));
        Assert.Equal((byte)(MissingPartSet.LeftArm | MissingPartSet.RightLeg), state.Zombie.Missing);
        Assert.True(system.TryGetBody(id, out var body));
        Assert.Equal([BodyPart.LeftArm, BodyPart.RightLeg], body.MissingParts);
    }

    [Fact]
    public void AHitOnALimb_TakesItOff_AndTheServerReplicatesIt()
    {
        var system = System(out var world);
        var id = Spawn(system);

        var hit = Shoot(system, -0.5f, 1.0f);

        Assert.NotNull(hit);
        Assert.Equal(id, hit.Entity);
        Assert.Equal(BodyPart.LeftArm, hit.Part);
        Assert.True(hit.LostPart);
        Assert.False(hit.Killed);
        Assert.True(world.TryGet(id, out var state));
        Assert.Equal((byte)MissingPartSet.LeftArm, state.Zombie.Missing);
        Assert.False(state.Zombie.Dead);
    }

    [Fact]
    public void ALostPartCannotBeHitAgain()
    {
        var system = System(out _);
        Spawn(system);
        Assert.NotNull(Shoot(system, -0.5f, 1.0f));

        Assert.Null(Shoot(system, -0.5f, 1.0f));
    }

    [Fact]
    public void AHitThatDoesNotDestroyAPart_WoundsIt()
    {
        var system = System(out var world);
        var id = Spawn(system);

        var hit = Shoot(system, 0, 1.0f, damage: 10);

        Assert.NotNull(hit);
        Assert.Equal(BodyPart.Torso, hit.Part);
        Assert.False(hit.LostPart);
        Assert.False(hit.Killed);
        Assert.Contains(hit.Events, e => e is Zombies.Domain.Combat.WoundCreated);
        Assert.True(system.TryGetBody(id, out var body));
        Assert.Equal(30, body.Health(BodyPart.Torso));
        Assert.True(world.TryGet(id, out var state));
        Assert.Equal(0, state.Zombie.Missing);
    }

    [Fact]
    public void ALevelMakesAZombieTougher()
    {
        var system = System(out _);
        var weak = Spawn(system, level: 1);
        var strong = Spawn(system, seed: 2, level: 5, at: new Vector3(10, 0, 0));

        Assert.True(system.TryGetBody(weak, out var weakBody));
        Assert.True(system.TryGetBody(strong, out var strongBody));
        Assert.Equal(40, weakBody.Health(BodyPart.Torso));
        Assert.Equal(120, strongBody.Health(BodyPart.Torso));
    }

    [Fact]
    public void LosingTheHead_KillsTheZombie_AndItDropsEverythingItWore()
    {
        var system = System(out var world);
        var id = Spawn(system, seed: 5);
        ZombieDied? died = null;
        system.Died += e => died = e;

        var hit = Shoot(system, 0, 1.6f);

        Assert.NotNull(hit);
        Assert.Equal(BodyPart.Head, hit.Part);
        Assert.True(hit.Killed);
        Assert.NotNull(died);
        Assert.Equal(id, died.Entity);
        Assert.Equal(new ZombieSpec(5, "t:zombie/plain", 1), died.Spec);
        Assert.Equal(["t:item/cap", "t:item/jeans", "t:item/pack", "t:item/shirt"], died.Drops.Select(i => i.Value).Order(StringComparer.Ordinal));
        Assert.True(world.TryGet(id, out var state));
        Assert.True(state.Zombie.Dead);
        Assert.True(system.TryGetAppearance(id, out var look));
        Assert.Equal(look.WornItems, died.Drops);
    }

    [Fact]
    public void ADeadZombieCannotBeHit_AndIsRemovedAfterTheCorpseTime()
    {
        var system = System(out var world, options: new ZombieOptions { CorpseTicks = 10 });
        var id = Spawn(system);
        system.Tick(100);
        Assert.NotNull(Shoot(system, 0, 1.0f));

        Assert.Null(Shoot(system, 0, 1.0f));
        system.Tick(109);
        Assert.True(world.TryGet(id, out _));
        system.Tick(110);

        Assert.False(world.TryGet(id, out _));
        Assert.Equal(0, system.Count);
        Assert.False(system.TryGetEntity(id, out _));
        Assert.Equal(0, system.Ecs.Count);
    }

    [Fact]
    public void TheNearestZombieAlongTheRayTakesTheHit()
    {
        var system = System(out _);
        var far = Spawn(system, seed: 1, at: new Vector3(0, 0, 4));
        var near = Spawn(system, seed: 2, at: new Vector3(0, 0, 0));

        var hit = Shoot(system, 0, 1.0f);

        Assert.NotNull(hit);
        Assert.Equal(near, hit.Entity);
        Assert.NotEqual(far, hit.Entity);
    }

    [Fact]
    public void AZombiesPositionAndYawDecideWhereItsBoxesAre()
    {
        var system = System(out _);
        var id = Spawn(system, at: new Vector3(20, 60, 20), yaw: MathF.PI / 2f);

        // Facing +X, so its head is hit from +X at 60 + 1.6 m.
        var miss = Shoot(system, 0, 1.6f);
        var hit = system.Hit(new Vector3(25, 61.6f, 20), -Vector3.UnitX, 20f, DamageType.Pierce, 5);

        Assert.Null(miss);
        Assert.NotNull(hit);
        Assert.Equal(id, hit.Entity);
        Assert.Equal(BodyPart.Head, hit.Part);
    }

    [Fact]
    public void ZeroOrNonsenseDamageOrReachHitsNothing()
    {
        var system = System(out _);
        Spawn(system);

        Assert.Null(Shoot(system, 0, 1.0f, damage: 0));
        Assert.Null(system.Hit(new Vector3(0, 1, -5), Vector3.UnitZ, 0f, DamageType.Cut, 5));
        Assert.Null(system.Hit(new Vector3(0, 1, -5), Vector3.UnitZ, float.NaN, DamageType.Cut, 5));
        Assert.Null(system.Hit(new Vector3(0, 1, -5), Vector3.Zero, 20f, DamageType.Cut, 5));
    }

    [Fact]
    public void ZombiesTooFarAwayAreNotEvenTested()
    {
        var system = System(out _);
        Spawn(system, at: new Vector3(0, 0, 100));

        Assert.Null(system.Hit(new Vector3(0, 1, -5), Vector3.UnitZ, 20f, DamageType.Cut, 5));
    }

    [Fact]
    public void AZombieMissingALeg_CrawlsOnTheServersPose_AndItsLegCannotBeHit()
    {
        var legless = Plain.Replace("\"missingParts\": []", "\"missingParts\": [ { \"part\": \"leftLeg\", \"chance\": 1 } ]", StringComparison.Ordinal);
        var system = System(out _, [legless]);
        Spawn(system);

        var parts = new List<BodyPart>();
        for (var height = 0.05f; height < 2.0f; height += 0.1f)
        {
            if (system.Hit(new Vector3(-0.25f, height, -5), Vector3.UnitZ, 20f, DamageType.Cut, 1) is { } hit)
            {
                parts.Add(hit.Part);
            }
        }

        Assert.NotEmpty(parts);
        Assert.DoesNotContain(BodyPart.LeftLeg, parts);
    }

    [Fact]
    public void TheBaseModsZombies_AllSpawnAndTakeAHit()
    {
        var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RigTestData.RepoRoot(), "mods")));
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));
        var catalog = ZombieContentLoader.Load(loaded.Registry);
        var traits = new TraitRegistry();
        BaseTraits.Register(traits);
        var world = new ServerWorld();
        var system = new ZombieSystem(world, catalog, traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));

        Assert.Equal(["base:zombie/bloater", "base:zombie/runner", "base:zombie/walker"], catalog.Types.Select(t => t.Id));
        for (var i = 0; i < catalog.Types.Count; i++)
        {
            var position = new Vector3(i * 10, 0, 0);
            var id = Spawn(system, seed: (ulong)(i + 1), type: catalog.Types[i].Id, level: 2, at: position);
            var hit = system.Hit(position + new Vector3(0, 1.0f, -5), Vector3.UnitZ, 20f, DamageType.Cut, 1);

            Assert.NotNull(hit);
            Assert.Equal(id, hit.Entity);
        }
    }


    [Fact]
    public void TheBaseContent_ResolvesEveryZombieReference()
    {
        var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RigTestData.RepoRoot(), "mods")));
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));
        var catalog = ZombieContentLoader.Load(loaded.Registry);
        var traits = new TraitRegistry();
        BaseTraits.Register(traits);
        var items = loaded.Registry.OfKind("item").Select(d => d.Id.Value).ToHashSet();
        var worn = catalog.Types.SelectMany(t => t.Outfit.Clothing.Concat(t.Outfit.Headwear).Concat(t.Outfit.Backpacks)).Select(w => w.Item).OfType<ItemId>();

        Assert.All(worn, item => Assert.Contains(item.Value, items));
        Assert.All(catalog.Types.SelectMany(t => t.Traits), trait => Assert.True(traits.IsRegistered(trait.Trait), trait.Trait));
        Assert.All(
            SettlementContentLoader.Load(loaded.Registry).Types.SelectMany(t => t.ZombieSpawns),
            rule => Assert.True(catalog.TryGet(rule.ZombieType, out _), rule.ZombieType));
    }

    private static (Zombies.Domain.World.SettlementPlan Plan, Zombies.Domain.Mods.DefinitionRegistry Registry) PlanWithZombies()
    {
        var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RigTestData.RepoRoot(), "mods")));
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));
        var settlements = SettlementContentLoader.Load(loaded.Registry);
        var grid = new Zombies.Domain.World.RegionGrid(4242, siteChancePercent: 100);
        var plan = Enumerable.Range(-8, 17)
            .SelectMany(x => Enumerable.Range(-8, 17).Select(z => new Zombies.Domain.World.RegionCoord(x, z)))
            .Select(region => Zombies.Domain.World.SettlementPlanner.Plan(settlements, grid, region))
            .First(p => p is { ZombieSpawns.Count: > 0 })!;
        return (plan, loaded.Registry);
    }

    [Fact]
    public void ASettlementsZombiesSpawnAtItsPlannedSpotsOnTheGround()
    {
        var (plan, registry) = PlanWithZombies();
        var catalog = ZombieContentLoader.Load(registry);
        var traits = new TraitRegistry();
        BaseTraits.Register(traits);
        var world = new ServerWorld();
        var system = new ZombieSystem(world, catalog, traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));

        var report = system.SpawnSettlement(plan, (x, z) => 64f);
        var again = new ZombieSystem(new ServerWorld(), catalog, traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid")).SpawnSettlement(plan, (x, z) => 64f);

        Assert.Empty(report.Problems);
        Assert.Equal(plan.ZombieSpawns.Count, report.Spawned.Count);
        Assert.Equal(report.Spawned.Count, again.Spawned.Count);
        for (var i = 0; i < report.Spawned.Count; i++)
        {
            Assert.True(world.TryGet(report.Spawned[i], out var state));
            Assert.Equal(64f, state.Position.Y);
            Assert.Equal(plan.ZombieSpawns[i].X + 0.5f, state.Position.X);
            Assert.Equal(catalog.IndexOf(plan.ZombieSpawns[i].ZombieType), state.Zombie.Type);
            Assert.Equal(Math.Clamp(plan.Danger, 1, catalog.At(state.Zombie.Type).TopLevel), state.Zombie.Level);
        }
    }

    [Fact]
    public void ASettlementSpawnThatCannotHappen_IsReportedNotSkippedSilently()
    {
        var (plan, _) = PlanWithZombies();
        var system = System(out var world);

        var report = system.SpawnSettlement(plan, (x, z) => 64f);

        Assert.Empty(report.Spawned);
        Assert.Equal(plan.ZombieSpawns.Count, report.Problems.Count);
        Assert.Contains("base:zombie/walker", report.Problems[0], StringComparison.Ordinal);
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void ASettlementsZombiesAreOfTheirEvolvedType_InAnOlderWorld_OnEveryServer()
    {
        var (plan, registry) = PlanWithZombies();
        var catalog = ZombieContentLoader.Load(registry);
        var traits = new TraitRegistry();
        BaseTraits.Register(traits);

        // The types, in plan order, of the zombies a fresh Server spawns when the world is this many days old.
        List<ZombieState> Spawned(int worldDay)
        {
            var world = new ServerWorld();
            var system = new ZombieSystem(world, catalog, traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
            var report = system.SpawnSettlement(plan, (x, z) => 64f, worldDay);
            Assert.Empty(report.Problems);
            return [.. report.Spawned.Select(id => world.TryGet(id, out var state) ? state.Zombie : throw new InvalidOperationException())];
        }

        var young = Spawned(29);
        var old = Spawned(30);

        Assert.Equal(plan.ZombieSpawns.Select(s => s.ZombieType), young.Select(z => catalog.At(z.Type).Id));
        Assert.Equal(old, Spawned(30));
        Assert.Equal(
            plan.ZombieSpawns.Select(s => s.ZombieType == "base:zombie/walker" ? "base:zombie/runner" : s.ZombieType),
            old.Select(z => catalog.At(z.Type).Id));
        Assert.Contains(plan.ZombieSpawns, s => s.ZombieType == "base:zombie/walker");
    }
}
