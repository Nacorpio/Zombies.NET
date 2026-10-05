using System.Numerics;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Zombies;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Tests.Rigs;

namespace Zombies.Engine.Tests;

/// <summary>
/// Weakpoints on the humanoid: the head box is 0.5 wide and spans 1.375 to 1.875 high, so a ray from the front at x 0 and y 1.625
/// goes through the middle of it, and each 0.1 m sideways is 0.2 of the box.
/// </summary>
public sealed class ZombieWeakpointTests
{
    private static string Zombie(bool referencesSet = true, string id = "t:zombie/plain") => $$"""
        {
          "id": "{{id}}",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4, "maxLevel": 5, "perLevelBonus": 0.5 },
          "senses": { "sight": 20, "hearing": 30 },
          "appearance": { "skinTones": ["#8fa07a"] }{{(referencesSet ? ", \"weakpointSet\": \"t:weakpoint_set/plain\"" : string.Empty)}}
        }
        """;

    private static string SetOf(string eyes) => $$"""
        {
          "id": "t:weakpoint_set/plain",
          "weakpoints": [ {{eyes}} ]
        }
        """;

    private static string Eyes(string extra = "") => $$"""
        { "name": "eyes", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.2 }, "size": 0.4, "criticalMultiplier": 3{{extra}} }
        """;

    private static ZombieSystem System(string set, string? zombie = null)
    {
        var registry = new TraitRegistry();
        BaseTraits.Register(registry);
        var catalog = new ZombieCatalog([ZombieTypeJson.Parse(zombie ?? Zombie())], [WeakpointSetJson.Parse(set)]);
        return new ZombieSystem(new ServerWorld(), catalog, registry, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
    }

    private static uint Spawn(ZombieSystem system, ulong seed = 1)
    {
        Assert.True(system.TrySpawn(new ZombieSpec(seed, "t:zombie/plain", 1), Vector3.Zero, 0f, out var id, out var problem), problem);
        return id;
    }

    private static ZombieHit? ShootHead(ZombieSystem system, float x, double damage, AttackKind attack = AttackKind.Ranged) =>
        system.Hit(new Vector3(x, 1.625f, -5), Vector3.UnitZ, 20f, DamageType.Pierce, damage, attack);

    private static WeakpointHit? WeakpointOf(ZombieHit? hit) => hit?.Events.OfType<WeakpointHit>().SingleOrDefault();

    [Fact]
    public void AHitInsideAWeakpoint_MultipliesTheDamageOnTheBodyPart_AndTellsWhichWeakpointWasHit()
    {
        var system = System(SetOf(Eyes()));
        var id = Spawn(system);

        var hit = ShootHead(system, 0, damage: 10);

        Assert.NotNull(hit);
        Assert.Equal(BodyPart.Head, hit.Part);
        Assert.Equal(new WeakpointHit(id, "eyes", BodyPart.Head, 30, null), WeakpointOf(hit));
        Assert.True(system.TryGetBody(id, out var body));
        Assert.Equal(10, body.Health(BodyPart.Head));
    }

    [Fact]
    public void AHitInTheSamePartOutsideTheWeakpoint_DoesPlainDamage_AndNamesNoWeakpoint()
    {
        var system = System(SetOf(Eyes()));
        var id = Spawn(system);

        var hit = ShootHead(system, 0.2f, damage: 10);

        Assert.NotNull(hit);
        Assert.Equal(BodyPart.Head, hit.Part);
        Assert.Null(WeakpointOf(hit));
        Assert.True(system.TryGetBody(id, out var body));
        Assert.Equal(30, body.Health(BodyPart.Head));
    }

    [Fact]
    public void AWeakpointOnAnotherPart_DoesNotChangeAHit()
    {
        var system = System(SetOf(Eyes()));
        var id = Spawn(system);

        var hit = system.Hit(new Vector3(0, 1.0f, -5), Vector3.UnitZ, 20f, DamageType.Pierce, 10);

        Assert.NotNull(hit);
        Assert.Equal(BodyPart.Torso, hit.Part);
        Assert.Null(WeakpointOf(hit));
        Assert.True(system.TryGetBody(id, out var body));
        Assert.Equal(30, body.Health(BodyPart.Torso));
    }

    [Fact]
    public void ADifficulty_ShrinksTheWeakpointForThatKindOfAttackOnly()
    {
        // Half the size for a ranged hit, so the weakpoint spans 0.4 to 0.6 of the head: 0.08 m off center is 0.66, outside it.
        var system = System(SetOf(Eyes(", \"rangedDifficulty\": 0.5")));
        Spawn(system);

        Assert.Null(WeakpointOf(ShootHead(system, 0.08f, damage: 1, AttackKind.Ranged)));
        Assert.NotNull(WeakpointOf(ShootHead(system, 0.08f, damage: 1, AttackKind.Melee)));
    }

    [Fact]
    public void TheEffect_NeedsTheThresholdOfDamageAfterTheMultiplier()
    {
        var system = System(SetOf(Eyes(", \"effect\": \"stagger\", \"effectThreshold\": 20, \"effectChance\": 1")));
        Spawn(system);

        Assert.Null(WeakpointOf(ShootHead(system, 0, damage: 6))?.Effect);
        Assert.Equal(WeakpointEffect.Stagger, WeakpointOf(ShootHead(system, 0, damage: 7))?.Effect);
    }

    [Fact]
    public void AnEffectWithNoChance_NeverHappens()
    {
        var system = System(SetOf(Eyes(", \"effect\": \"stagger\", \"effectChance\": 0")));
        Spawn(system);

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(WeakpointOf(ShootHead(system, 0, damage: 1))?.Effect);
        }
    }

    [Fact]
    public void TheEffectChance_PlaysOutTheSameForTheSameZombieOnEveryServer()
    {
        static List<WeakpointEffect?> Effects(ulong seed)
        {
            var system = System(SetOf(Eyes(", \"effect\": \"stagger\", \"effectChance\": 0.5")));
            Spawn(system, seed);
            return [.. Enumerable.Range(0, 12).Select(_ => WeakpointOf(ShootHead(system, 0, damage: 1))!.Effect)];
        }

        var first = Effects(42);

        Assert.Equal(first, Effects(42));
        Assert.Contains(null, first);
        Assert.Contains(WeakpointEffect.Stagger, first);
        Assert.NotEqual(first, Effects(43));
    }

    [Fact]
    public void AZombieTypeWithoutAWeakpointSet_TakesPlainDamage()
    {
        var system = System(SetOf(Eyes()), Zombie(referencesSet: false));
        var id = Spawn(system);

        var hit = ShootHead(system, 0, damage: 10);

        Assert.Null(WeakpointOf(hit));
        Assert.True(system.TryGetBody(id, out var body));
        Assert.Equal(30, body.Health(BodyPart.Head));
    }

    [Fact]
    public void AWeakpointHit_CanKillThroughItsMultiplier()
    {
        var system = System(SetOf(Eyes()));
        Spawn(system);

        var hit = ShootHead(system, 0, damage: 14);

        Assert.NotNull(hit);
        Assert.True(hit.Killed);
        Assert.Equal(42, WeakpointOf(hit)?.Damage);
    }

    [Fact]
    public void ACatalogRefusesATypeNamingAWeakpointSetThatDoesNotExist()
    {
        var error = Assert.Throws<ArgumentException>(() => new ZombieCatalog([ZombieTypeJson.Parse(Zombie())]));

        Assert.Contains("t:weakpoint_set/plain", error.Message, StringComparison.Ordinal);
        Assert.Contains("t:zombie/plain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModCanAddAWeakpointSetToAZombieTypeWithAPatch()
    {
        var baseMod = new ModPackage("base", """{ "id": "base", "version": "1.0.0" }""", [new ModFile("data/z.json", Zombie(referencesSet: false, id: "base:zombie/plain"))]);
        var addon = new ModPackage(
            "t",
            """{ "id": "t", "version": "1.0.0", "dependencies": [ { "id": "base" } ] }""",
            [
                new ModFile("data/set.json", SetOf(Eyes())),
                new ModFile("data/patch.json", """{ "patch": "base:zombie/plain", "weakpointSet": "t:weakpoint_set/plain" }"""),
            ]);
        var loaded = ModLoader.Load([baseMod, addon]);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));

        var catalog = ZombieContentLoader.Load(loaded.Registry);

        Assert.True(catalog.TryGet("base:zombie/plain", out var type));
        Assert.Equal("t:weakpoint_set/plain", catalog.WeakpointsOf(type)?.Id);
    }

    [Fact]
    public void TheBaseModGivesItsWalkerWeakpoints()
    {
        var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RigTestData.RepoRoot(), "mods")));
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));

        var catalog = ZombieContentLoader.Load(loaded.Registry);

        Assert.True(catalog.TryGet("base:zombie/walker", out var walker));
        Assert.Contains(catalog.WeakpointsOf(walker)!.Weakpoints, w => w.Name == "eyes" && w.Part == BodyPart.Head);
    }
}
