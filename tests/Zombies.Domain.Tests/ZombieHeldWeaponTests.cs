using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Zombies;

namespace Zombies.Domain.Tests;

public sealed class ZombieHeldWeaponTests
{
    private const string Category = "t:weapon_category/melee";

    private static WeaponCatalog Weapons() => new(
        [new WeaponCategory(Category, 1, [])],
        [
            new WeaponDefinition(new ItemId("t:item/knife"), Category, 12, DamageType.Cut, 2, 0.4, 10, handsNeeded: 1),
            new WeaponDefinition(new ItemId("t:item/bat"), Category, 25, DamageType.Blunt, 1, 1.1, 30, handsNeeded: 2),
            new WeaponDefinition(new ItemId("t:item/pistol"), Category, 35, DamageType.Pierce, 2, 30, 90, ammoItem: new ItemId("t:item/rounds")),
        ],
        []);

    private static string Json(string heldWeapons, string missingParts = "") => $$"""
        {
          "id": "t:zombie/walker",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4 },
          "senses": { "sight": 20, "hearing": 30 },
          "appearance": { "skinTones": ["#8fa07a"] },
          "missingParts": [ {{missingParts}} ],
          "heldWeapons": [ {{heldWeapons}} ]
        }
        """;

    private const string Mixed = """{ "weight": 2 }, { "item": "t:item/knife", "weight": 3 }, { "item": "t:item/bat", "weight": 3 }""";
    private const string BothArms = """{ "part": "leftArm", "chance": 1 }, { "part": "rightArm", "chance": 1 }""";
    private const string LeftArm = """{ "part": "leftArm", "chance": 1 }""";
    private const string RightArm = """{ "part": "rightArm", "chance": 1 }""";
    private const string EitherArm = """{ "part": "leftArm", "chance": 0.5 }, { "part": "rightArm", "chance": 0.5 }""";

    private static ZombieAppearance Look(ZombieTypeDefinition type, ulong seed) =>
        ZombieGenerator.Generate(type, new ZombieSpec(seed, type.Id, 1), Weapons());

    private static ZombieTypeDefinition Type(string heldWeapons, string missingParts = "") => ZombieTypeJson.Parse(Json(heldWeapons, missingParts));

    [Fact]
    public void AZombieType_ListsWeightedHeldWeapons_IncludingNone()
    {
        var type = Type(Mixed);

        Assert.Equal(3, type.HeldWeapons.Count);
        Assert.Null(type.HeldWeapons[0].Item);
        Assert.Equal(2, type.HeldWeapons[0].Weight);
        Assert.Equal("t:item/knife", type.HeldWeapons[1].Item?.Value);
        Assert.Equal(3, type.HeldWeapons[1].Weight);
        Assert.Empty(ZombieTypeJson.Parse(Json("")).HeldWeapons);
    }

    [Fact]
    public void ATypeWithAWeightedTable_SpawnsSomeZombiesWithEachChoice_AndNoOthers()
    {
        var type = Type(Mixed);

        var held = Enumerable.Range(0, 400).Select(seed => Look(type, (ulong)seed).HeldWeapon?.Item.Value).ToList();

        Assert.Contains(null, held);
        Assert.Contains("t:item/knife", held);
        Assert.Contains("t:item/bat", held);
        Assert.All(held, h => Assert.Contains(h, new string?[] { null, "t:item/knife", "t:item/bat" }));
        Assert.True(held.Count(h => h == "t:item/knife") > held.Count(h => h is null) / 2, "A weight of 3 should beat a weight of 2.");
    }

    [Fact]
    public void ATypeThatListsOnlyNone_NeverHoldsAWeapon_AndOneWithOnlyAWeaponAlwaysDoes()
    {
        var none = Type("""{ "weight": 1 }""");
        var knife = Type("""{ "item": "t:item/knife" }""");

        Assert.All(Enumerable.Range(0, 100), seed => Assert.Null(Look(none, (ulong)seed).HeldWeapon));
        Assert.All(Enumerable.Range(0, 100), seed => Assert.Equal("t:item/knife", Look(knife, (ulong)seed).HeldWeapon?.Item.Value));
    }

    [Fact]
    public void TheSameSpec_YieldsTheSameHeldWeapon_OnTwoIndependentConstructions()
    {
        // Two clients: each parses the definition, builds its own catalogs, and derives the zombie from the spec alone.
        var server = Type(Mixed, EitherArm);
        var client = Type(Mixed, EitherArm);
        var serverWeapons = Weapons();
        var clientWeapons = Weapons();

        for (ulong seed = 0; seed < 300; seed++)
        {
            var spec = new ZombieSpec(seed, "t:zombie/walker", 1);
            var left = ZombieGenerator.Generate(server, spec, serverWeapons);
            var right = ZombieGenerator.Generate(client, spec, clientWeapons);

            Assert.Equal(left.HeldWeapon, right.HeldWeapon);
            Assert.Equal(left, right);
        }
    }

    [Fact]
    public void AddingHeldWeapons_DoesNotChangeTheRestOfTheZombie()
    {
        var without = Type("", EitherArm);
        var with = Type(Mixed, EitherArm);

        for (ulong seed = 0; seed < 100; seed++)
        {
            var a = Look(without, seed);
            var b = Look(with, seed);
            Assert.Equal((a.HeightPermille, a.BuildPermille, a.SkinColor), (b.HeightPermille, b.BuildPermille, b.SkinColor));
            Assert.Equal(a.MissingParts, b.MissingParts);
        }
    }

    [Fact]
    public void AZombieWithNoArms_NeverHoldsAWeapon()
    {
        var type = Type("""{ "item": "t:item/knife", "weight": 5 }, { "item": "t:item/bat", "weight": 5 }""", BothArms);

        Assert.All(Enumerable.Range(0, 200), seed =>
        {
            var look = Look(type, (ulong)seed);
            Assert.Contains(BodyPart.LeftArm, look.MissingParts);
            Assert.Contains(BodyPart.RightArm, look.MissingParts);
            Assert.Null(look.HeldWeapon);
        });
    }

    [Theory]
    [InlineData(LeftArm, BodyPart.RightArm)]
    [InlineData(RightArm, BodyPart.LeftArm)]
    public void AZombieWithOneArm_HoldsOnlyAOneHandedWeapon_InTheHandItHas(string missing, BodyPart expectedArm)
    {
        var type = Type("""{ "item": "t:item/knife" }, { "item": "t:item/bat", "weight": 50 }""", missing);

        Assert.All(Enumerable.Range(0, 200), seed =>
        {
            var held = Look(type, (ulong)seed).HeldWeapon;
            Assert.NotNull(held);
            Assert.Equal("t:item/knife", held.Item.Value);
            Assert.Equal(1, held.HandsNeeded);
            Assert.Equal(expectedArm, held.Arm);
        });
    }

    [Fact]
    public void WhateverArmsAZombieSpawnsWith_ItHoldsOnlyWhatThoseArmsCanHold()
    {
        var type = Type(Mixed, EitherArm);
        var weapons = Weapons();

        var spawned = Enumerable.Range(0, 600).Select(seed => Look(type, (ulong)seed)).ToList();

        foreach (var look in spawned)
        {
            var arms = 2 - look.MissingParts.Count(p => p is BodyPart.LeftArm or BodyPart.RightArm);
            if (look.HeldWeapon is { } held)
            {
                Assert.True(weapons.TryGetWeapon(held.Item, out var weapon));
                Assert.True(weapon.HandsNeeded <= arms, $"{held.Item} needs {weapon.HandsNeeded} hands but the zombie has {arms}.");
                Assert.DoesNotContain(held.Arm, look.MissingParts);
            }
        }

        Assert.Contains(spawned, l => l.HeldWeapon is { HandsNeeded: 2 });
        Assert.Contains(spawned, l => l.HeldWeapon is { HandsNeeded: 1 } && l.MissingParts.Count == 1);
    }

    [Fact]
    public void ALostArm_DropsAOneHandedWeaponOnlyIfItIsTheHoldingArm()
    {
        var held = new ZombieHeldWeapon(new ItemId("t:item/knife"), 1, BodyPart.RightArm);

        Assert.True(held.IsHeldWith([]));
        Assert.True(held.IsHeldWith([BodyPart.LeftArm, BodyPart.LeftLeg]));
        Assert.False(held.IsHeldWith([BodyPart.RightArm]));
    }

    [Fact]
    public void ALostArm_DropsATwoHandedWeapon_WhicheverArmIt_Is()
    {
        var held = new ZombieHeldWeapon(new ItemId("t:item/bat"), 2, BodyPart.RightArm);

        Assert.True(held.IsHeldWith([BodyPart.Head]));
        Assert.False(held.IsHeldWith([BodyPart.LeftArm]));
        Assert.False(held.IsHeldWith([BodyPart.RightArm]));
    }

    [Fact]
    public void AHeldWeaponWithNoWeaponsToLookItUpIn_IsRefused()
    {
        var type = Type("""{ "item": "t:item/knife" }""");

        Assert.Throws<ArgumentException>(() => ZombieGenerator.Generate(type, new ZombieSpec(1, "t:zombie/walker", 1)));
    }

    [Theory]
    [InlineData("""{ "item": "t:item/nothing" }""", "t:item/nothing")]
    [InlineData("""{ "item": "t:item/pistol" }""", "not a melee weapon")]
    public void ACatalog_RefusesAHeldWeaponThatIsNotAMeleeWeapon(string held, string expected)
    {
        var error = Assert.Throws<ArgumentException>(() => new ZombieCatalog([Type(held)], weapons: Weapons()));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "item": "t:item/knife", "weight": 0 }""")]
    [InlineData("""{ "item": "t:item/knife" }, { "item": "t:item/knife" }""")]
    [InlineData("""{ "weight": 1 }, { "weight": 2 }""")]
    [InlineData("""{ "item": "not an id" }""")]
    public void ABadHeldWeaponTable_IsRefused(string held)
    {
        Assert.Throws<ZombieTypeDefinitionException>(() => Type(held));
    }
}
