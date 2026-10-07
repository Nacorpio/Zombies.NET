using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Zombies;
using Zombies.Engine.Animation;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Tests.Rigs;

namespace Zombies.Engine.Tests;

public sealed class ZombieHeldWeaponTests
{
    private const string Category = "t:weapon_category/melee";

    private static string Type(string heldWeapons, string missingParts = "") => $$"""
        {
          "id": "t:zombie/armed",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4, "maxLevel": 5, "perLevelBonus": 0.5 },
          "senses": { "sight": 20, "hearing": 30 },
          "appearance": { "skinTones": ["#8fa07a"] },
          "outfit": { "clothing": [ { "item": "t:item/shirt" } ], "clothingCount": { "min": 1, "max": 1 } },
          "missingParts": [ {{missingParts}} ],
          "heldWeapons": [ {{heldWeapons}} ]
        }
        """;

    private const string Crowbar = """{ "item": "t:item/crowbar" }""";
    private const string Bat = """{ "item": "t:item/bat" }""";
    private const string LeftArmGone = """{ "part": "leftArm", "chance": 1 }""";
    private const string RightArmGone = """{ "part": "rightArm", "chance": 1 }""";

    private static WeaponCatalog Weapons() => new(
        [new WeaponCategory(Category, 1, [])],
        [
            new WeaponDefinition(new ItemId("t:item/crowbar"), Category, 22, DamageType.Blunt, 1.4, 1.0, 35, handsNeeded: 1),
            new WeaponDefinition(new ItemId("t:item/bat"), Category, 25, DamageType.Cut, 1.2, 1.1, 30, handsNeeded: 2),
        ],
        []);

    private static ZombieSystem System(string type)
    {
        var traits = new TraitRegistry();
        BaseTraits.Register(traits);
        var catalog = new ZombieCatalog([ZombieTypeJson.Parse(type)], weapons: Weapons());
        return new ZombieSystem(new ServerWorld(), catalog, traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
    }

    private static uint Spawn(ZombieSystem system, int level = 1, ulong seed = 1)
    {
        Assert.True(system.TrySpawn(new ZombieSpec(seed, "t:zombie/armed", level), Vector3.Zero, 0f, out var id, out var problem), problem);
        return id;
    }

    // From the front (the skeleton faces -Z), at the height of the part: x = -0.5 is the left arm, 0.5 the right.
    private static ZombieHit? Shoot(ZombieSystem system, float x, float y, double damage = 100) =>
        system.Hit(new Vector3(x, y, -5), Vector3.UnitZ, 20f, DamageType.Pierce, damage);

    private static WeaponRig Rig()
    {
        Assert.True(WeaponRig.TryParse("""{ "id": "t:item/crowbar", "grip": { "offset": [0, 0, 0] } }""", out var rig, out var error), error);
        return rig;
    }

    [Fact]
    public void TheHeldWeapon_ComesFromTheSpecAlone_SoTwoServersAndClientsAgree()
    {
        var first = System(Type(Crowbar));
        var second = System(Type(Crowbar));
        var a = Spawn(first, seed: 42);
        var b = Spawn(second, seed: 42);

        Assert.True(first.TryGetAppearance(a, out var left));
        Assert.True(second.TryGetAppearance(b, out var right));
        Assert.Equal("t:item/crowbar", left.HeldWeapon?.Item.Value);
        Assert.Equal(left.HeldWeapon, right.HeldWeapon);
        Assert.Equal(left.HeldWeapon, ZombieGenerator.Generate(first.Catalog.At(0), new ZombieSpec(42, "t:zombie/armed", 1), Weapons()).HeldWeapon);
    }

    [Fact]
    public void TheHeldWeaponsReachAndDamage_DriveTheZombiesAttack_ScaledByItsLevel()
    {
        var system = System(Type(Crowbar));
        var level1 = Spawn(system);
        var level3 = Spawn(system, level: 3, seed: 2);

        Assert.True(system.TryGetAttack(level1, out var weak));
        Assert.True(system.TryGetAttack(level3, out var strong));

        Assert.Equal(new ItemId("t:item/crowbar"), weak.Weapon);
        Assert.Equal(1.0, weak.Reach);
        Assert.Equal(22, weak.Damage);
        Assert.Equal(DamageType.Blunt, weak.DamageType);
        Assert.Equal(1.0, strong.Reach);
        Assert.Equal(44, strong.Damage);
    }

    [Fact]
    public void AZombieHoldingNothing_AttacksWithTheDamageOfItsTypeAndTheUnarmedReach()
    {
        var system = System(Type("""{ "weight": 1 }"""));
        var id = Spawn(system, level: 3);

        Assert.True(system.TryGetAttack(id, out var attack));

        Assert.Null(attack.Weapon);
        Assert.Equal(ZombieAttack.UnarmedReach, attack.Reach);
        Assert.Equal(20, attack.Damage);
    }

    [Fact]
    public void ADeadZombie_HasNoAttack()
    {
        var system = System(Type(Crowbar));
        var id = Spawn(system);
        Assert.NotNull(Shoot(system, 0, 1.6f));

        Assert.False(system.TryGetAttack(id, out _));
    }

    [Fact]
    public void AZombieWithNoArms_NeverHoldsAWeapon_AndOneWithOneArmOnlyAOneHandedOne()
    {
        var armless = System(Type($"{Crowbar}, {Bat}", $"{LeftArmGone}, {RightArmGone}"));
        var oneArm = System(Type($"{Bat}, {Crowbar}", LeftArmGone));

        for (ulong seed = 0; seed < 50; seed++)
        {
            var none = Spawn(armless, seed: seed);
            var one = Spawn(oneArm, seed: seed);
            Assert.True(armless.TryGetAppearance(none, out var armlessLook));
            Assert.True(oneArm.TryGetAppearance(one, out var oneArmLook));
            Assert.Null(armlessLook.HeldWeapon);
            Assert.Equal("t:item/crowbar", oneArmLook.HeldWeapon?.Item.Value);
            Assert.True(armless.TryGetAttack(none, out var unarmed));
            Assert.Null(unarmed.Weapon);
        }
    }

    [Fact]
    public void TheWeapon_DropsAsLootWhenTheZombieDies_WithWhatItWore()
    {
        var system = System(Type(Crowbar));
        var id = Spawn(system);
        ZombieDied? died = null;
        var dropped = new List<ZombieDroppedWeapon>();
        system.Died += e => died = e;
        system.DroppedWeapon += dropped.Add;

        var hit = Shoot(system, 0, 1.6f);

        Assert.NotNull(hit);
        Assert.True(hit.Killed);
        Assert.NotNull(died);
        Assert.Equal(id, died.Entity);
        Assert.Equal(["t:item/crowbar", "t:item/shirt"], died.Drops.Select(i => i.Value).Order(StringComparer.Ordinal));
        Assert.Empty(dropped);
    }

    [Fact]
    public void TheWeapon_DropsWhenTheHoldingArmIsLost_OnceOnly_AndTheZombieIsThenUnarmed()
    {
        var system = System(Type(Crowbar));
        var id = Spawn(system);
        var dropped = new List<ZombieDroppedWeapon>();
        ZombieDied? died = null;
        system.DroppedWeapon += dropped.Add;
        system.Died += e => died = e;

        var hit = Shoot(system, 0.5f, 1.0f);

        Assert.NotNull(hit);
        Assert.Equal(BodyPart.RightArm, hit.Part);
        Assert.True(hit.LostPart);
        var fell = Assert.Single(dropped);
        Assert.Equal(id, fell.Entity);
        Assert.Equal(new ItemId("t:item/crowbar"), fell.Weapon);
        Assert.True(system.TryGetAttack(id, out var attack));
        Assert.Null(attack.Weapon);
        Assert.Equal(ZombieAttack.UnarmedReach, attack.Reach);

        Assert.NotNull(Shoot(system, 0, 1.6f));

        Assert.NotNull(died);
        Assert.Equal(["t:item/shirt"], died.Drops.Select(i => i.Value));
        Assert.Single(dropped);
    }

    [Fact]
    public void LosingTheOtherArm_LeavesAOneHandedWeaponInTheHand()
    {
        var system = System(Type(Crowbar));
        var id = Spawn(system);
        var dropped = new List<ZombieDroppedWeapon>();
        system.DroppedWeapon += dropped.Add;

        var hit = Shoot(system, -0.5f, 1.0f);

        Assert.NotNull(hit);
        Assert.Equal(BodyPart.LeftArm, hit.Part);
        Assert.True(hit.LostPart);
        Assert.Empty(dropped);
        Assert.True(system.TryGetAttack(id, out var attack));
        Assert.Equal(new ItemId("t:item/crowbar"), attack.Weapon);
    }

    [Fact]
    public void ATwoHandedWeapon_DropsWhenEitherArmIsLost()
    {
        var system = System(Type(Bat));
        var id = Spawn(system);
        var dropped = new List<ZombieDroppedWeapon>();
        system.DroppedWeapon += dropped.Add;
        Assert.True(system.TryGetAttack(id, out var armed));
        Assert.Equal(1.1, armed.Reach);

        Assert.NotNull(Shoot(system, -0.5f, 1.0f));

        Assert.Equal(new ItemId("t:item/bat"), Assert.Single(dropped).Weapon);
        Assert.True(system.TryGetAttack(id, out var unarmed));
        Assert.Null(unarmed.Weapon);
    }

    [Fact]
    public void TheWeapon_IsDrawnAtTheHandAttachPoint_AndGoneWhenTheHandIs()
    {
        var system = System(Type(Crowbar));
        var id = Spawn(system);
        var reference = new Animator(RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
        reference.Update(0f, new AnimationInput(0f, 0f, 0f, MissingPartSet.None));
        Assert.True(reference.Pose.TryGetAttachPoint(HeldWeapon.RightHand, out var hand));

        Assert.True(system.TryGetHeldWeaponPlacement(id, Rig(), out var placement));

        RigTestData.Near(hand.Position, placement.Weapon.Position);

        Assert.NotNull(Shoot(system, 0.5f, 1.0f));
        Assert.False(system.TryGetHeldWeaponPlacement(id, Rig(), out _));
    }

    [Fact]
    public void AZombieHoldingNothing_DrawsNothing()
    {
        var system = System(Type("""{ "weight": 1 }"""));
        var id = Spawn(system);

        Assert.False(system.TryGetHeldWeaponPlacement(id, Rig(), out _));
    }
}
