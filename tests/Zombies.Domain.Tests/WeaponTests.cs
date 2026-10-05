using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;

namespace Zombies.Domain.Tests;

public sealed class WeaponTests
{
    private static readonly ItemId Pistol = new("base:item/pistol");
    private static readonly ItemId Bat = new("base:item/bat");
    private static readonly ItemId Rounds = new("base:item/pistol_rounds");
    private static readonly ItemId Suppressor = new("base:item/suppressor");
    private static readonly ItemId Compensator = new("base:item/compensator");
    private static readonly ItemId Scope = new("base:item/scope");
    private static readonly ItemId Grip = new("base:item/grip");

    private readonly WeaponService _service = new(new WeaponCatalog(
        [
            new WeaponCategory("base:weapon_category/pistol", handling: 10, mounts: ["muzzle", "optic"]),
            new WeaponCategory("base:weapon_category/blunt_melee", handling: 5, mounts: []),
        ],
        [
            new WeaponDefinition(Pistol, "base:weapon_category/pistol", damage: 30, DamageType.Pierce, rateOfFire: 2, reach: 25, noise: 90, ammoItem: Rounds, wearPerUse: 10),
            new WeaponDefinition(Bat, "base:weapon_category/blunt_melee", damage: 20, DamageType.Blunt, rateOfFire: 1, reach: 1.2, noise: 20, handsNeeded: 2, handling: 3, wearPerUse: 25, extraMounts: ["grip"]),
        ],
        [
            new AttachmentDefinition(Suppressor, "muzzle", [new(new StatName("noise"), ModifierOperation.Multiply, 0.2), new(new StatName("damage"), ModifierOperation.Add, -2)]),
            new AttachmentDefinition(Compensator, "muzzle", [new(new StatName("handling"), ModifierOperation.Add, 4)]),
            new AttachmentDefinition(Scope, "optic", [new(new StatName("reach"), ModifierOperation.Multiply, 1.5)]),
            new AttachmentDefinition(Grip, "grip", [new(new StatName("handling"), ModifierOperation.Add, 2)]),
        ]));

    private static ItemState Loaded(int rounds, int? condition = null) =>
        condition is { } c
            ? ItemState.Create([new(WeaponService.RoundsValue, rounds), new(WeaponService.ConditionValue, c)])
            : ItemState.Create([new(WeaponService.RoundsValue, rounds)]);

    private WeaponStats Stats(ItemId weapon, ItemState? state)
    {
        Assert.True(_service.TryGetEffectiveStats(weapon, state, out var stats));
        return stats;
    }

    [Fact]
    public void Attach_FitsAttachmentOnOfferedMountAndRecordsItInState()
    {
        var result = _service.Attach(Pistol, Loaded(7), Suppressor);

        Assert.True(result.IsSuccess);
        Assert.Equal(new AttachmentFitted(Pistol, Suppressor, "muzzle"), Assert.Single(result.Events));
        Assert.True(result.State!.Attached.ContainsKey(Suppressor));
        Assert.Equal(7, WeaponService.RoundsOf(result.State));
    }

    [Fact]
    public void Attach_AllowsOneAttachmentPerMountAndOtherMountsStayFree()
    {
        var state = _service.Attach(Pistol, null, Suppressor).State;

        Assert.Equal(WeaponError.MountOccupied, _service.Attach(Pistol, state, Compensator).Error);
        Assert.Equal(WeaponError.MountOccupied, _service.Attach(Pistol, state, Suppressor).Error);
        Assert.True(_service.Attach(Pistol, state, Scope).IsSuccess);
    }

    [Fact]
    public void Attach_RejectsMountsTheWeaponDoesNotOfferAndUnknownItems()
    {
        Assert.Equal(WeaponError.MountNotOffered, _service.Attach(Bat, null, Suppressor).Error);
        Assert.Equal(WeaponError.UnknownAttachment, _service.Attach(Pistol, null, Rounds).Error);
        Assert.Equal(WeaponError.UnknownWeapon, _service.Attach(Rounds, null, Suppressor).Error);
        Assert.True(_service.Attach(Bat, null, Grip).IsSuccess);
    }

    [Fact]
    public void Detach_RemovesAttachmentFreesMountAndKeepsOtherState()
    {
        var fitted = _service.Attach(Pistol, Loaded(7, 80), Suppressor).State;

        var result = _service.Detach(Pistol, fitted, Suppressor);

        Assert.Equal(new AttachmentRemoved(Pistol, Suppressor, "muzzle"), Assert.Single(result.Events));
        Assert.Empty(result.State!.Attached);
        Assert.Equal(80, WeaponService.ConditionOf(result.State));
        Assert.True(_service.Attach(Pistol, result.State, Compensator).IsSuccess);
        Assert.Equal(WeaponError.NotFitted, _service.Detach(Pistol, result.State, Suppressor).Error);
        Assert.Equal(WeaponError.NotFitted, _service.Detach(Pistol, null, Suppressor).Error);
    }

    [Fact]
    public void EffectiveStats_CombineBaseStatsAndAttachmentModifiers()
    {
        var state = _service.Attach(Pistol, null, Suppressor).State;
        state = _service.Attach(Pistol, state, Scope).State;

        var stats = Stats(Pistol, state);

        Assert.Equal(28, stats.Damage, 6);
        Assert.Equal(18, stats.Noise, 6);
        Assert.Equal(37.5, stats.Reach, 6);
        Assert.Equal(10, stats.Handling, 6);
        Assert.Equal(2, stats.RateOfFire, 6);

        var bare = Stats(Pistol, null);
        Assert.Equal((30, 90, 25), (bare.Damage, bare.Noise, bare.Reach));
    }

    [Fact]
    public void EffectiveStats_UseCategoryHandlingUnlessTheWeaponSetsItsOwn()
    {
        Assert.Equal(10, Stats(Pistol, null).Handling);
        Assert.Equal(3, Stats(Bat, null).Handling);
        Assert.Equal(2, Stats(Bat, null).HandsNeeded);
    }

    [Fact]
    public void EffectiveStats_AreIndependentOfTheOrderAttachmentsWereFitted()
    {
        var a = _service.Attach(Pistol, _service.Attach(Pistol, null, Suppressor).State, Scope).State;
        var b = _service.Attach(Pistol, _service.Attach(Pistol, null, Scope).State, Suppressor).State;

        Assert.Equal(Stats(Pistol, a), Stats(Pistol, b));
    }

    [Fact]
    public void EffectiveStats_WornWeaponDealsLessDamage()
    {
        var fresh = Stats(Pistol, null).Damage;
        var worn = Stats(Pistol, Loaded(7, 50)).Damage;
        var nearlyBroken = Stats(Pistol, Loaded(7, 1)).Damage;

        Assert.True(worn < fresh);
        Assert.True(nearlyBroken < worn);
        Assert.Equal(fresh, Stats(Pistol, Loaded(7, 100)).Damage);
    }

    [Fact]
    public void Use_WearsTheWeaponSpendsARoundAndReturnsDamageForCombat()
    {
        var result = _service.Use(Pistol, Loaded(7));

        Assert.True(result.IsSuccess);
        Assert.Equal(new WeaponUsed(Pistol, 30, DamageType.Pierce, 90, 90, 6), Assert.Single(result.Events));
        Assert.Equal(90, WeaponService.ConditionOf(result.State));
        Assert.Equal(6, WeaponService.RoundsOf(result.State));
    }

    [Fact]
    public void Use_DamageReflectsAttachmentsAndCondition()
    {
        var state = _service.Attach(Pistol, Loaded(7, 50), Suppressor).State;

        var used = Assert.IsType<WeaponUsed>(Assert.Single(_service.Use(Pistol, state).Events));

        Assert.Equal(28 * 0.75, used.Damage, 6);
        Assert.Equal(18, used.Noise, 6);
    }

    [Fact]
    public void Use_FailsWithoutAmmoAndLeavesStateAlone()
    {
        Assert.Equal(WeaponError.OutOfAmmo, _service.Use(Pistol, Loaded(0)).Error);
        Assert.Equal(WeaponError.OutOfAmmo, _service.Use(Pistol, null).Error);
    }

    [Fact]
    public void Use_WeaponWithoutAmmoItemNeedsNoRounds()
    {
        var result = _service.Use(Bat, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(75, WeaponService.ConditionOf(result.State));
    }

    [Fact]
    public void Use_BrokenWeaponCannotBeUsed()
    {
        var last = _service.Use(Pistol, Loaded(7, 10));

        Assert.True(last.IsSuccess);
        Assert.Equal(new WeaponBroke(Pistol), last.Events[1]);
        Assert.Equal(0, WeaponService.ConditionOf(last.State));
        Assert.Equal(WeaponError.Broken, _service.Use(Pistol, last.State).Error);
        Assert.Equal(WeaponError.Broken, _service.Use(Bat, ItemState.Create([new(WeaponService.ConditionValue, 0)])).Error);
    }

    [Fact]
    public void Use_RejectsUnknownWeapon()
    {
        Assert.Equal(WeaponError.UnknownWeapon, _service.Use(Rounds, null).Error);
    }

    [Fact]
    public void WeaponState_StacksOnlyMergeWhenEqual()
    {
        Assert.Equal(_service.Use(Pistol, Loaded(7)).State, _service.Use(Pistol, Loaded(7)).State);
        Assert.NotEqual(_service.Use(Pistol, Loaded(7)).State, _service.Use(Pistol, Loaded(6)).State);
    }

    [Fact]
    public void Catalog_RejectsUnknownCategoryAndDuplicates()
    {
        var weapon = new WeaponDefinition(Pistol, "base:weapon_category/missing", 1, DamageType.Cut, 1, 1, 1);
        Assert.Throws<ArgumentException>(() => new WeaponCatalog([], [weapon], []));
        var category = new WeaponCategory("base:weapon_category/pistol", 1, []);
        Assert.Throws<ArgumentException>(() => new WeaponCatalog([category, category], [], []));
    }

    [Fact]
    public void Definitions_RejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeaponDefinition(Pistol, "base:weapon_category/pistol", -1, DamageType.Cut, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeaponDefinition(Pistol, "base:weapon_category/pistol", 1, DamageType.Cut, 1, 1, 1, handsNeeded: 3));
        Assert.Throws<ArgumentException>(() => new AttachmentDefinition(Scope, "Not A Mount", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttachmentDefinition(Scope, "optic", [new(new StatName("reach"), ModifierOperation.Add, double.NaN)]));
    }

    [Fact]
    public void Json_ParsesCategoryWeaponAndAttachment()
    {
        var category = WeaponDefinitionJson.ParseCategory("""{ "id": "mymod:weapon_category/crossbow", "handling": 4, "mounts": ["optic"] }""");
        var weapon = WeaponDefinitionJson.ParseWeapon("""
            { "id": "mymod:weapon/crossbow", "item": "mymod:item/crossbow", "category": "mymod:weapon_category/crossbow",
              "damage": 40, "damageType": "pierce", "rateOfFire": 0.5, "reach": 40, "noise": 10, "handsNeeded": 2,
              "ammoItem": "mymod:item/bolt", "mounts": ["stock"] }
            """);
        var attachment = WeaponDefinitionJson.ParseAttachment("""
            { "id": "mymod:attachment/red_dot", "item": "mymod:item/red_dot", "mount": "optic",
              "effects": [ { "stat": "handling", "operation": "add", "value": 2 } ] }
            """);

        Assert.Equal(("mymod:weapon_category/crossbow", 4d, "optic"), (category.Id, category.Handling, Assert.Single(category.Mounts)));
        Assert.Equal((DamageType.Pierce, 2, new ItemId("mymod:item/bolt")), (weapon.DamageType, weapon.HandsNeeded, weapon.AmmoItem));
        Assert.Equal("stock", Assert.Single(weapon.ExtraMounts));
        Assert.Equal(new AttachmentEffect(new StatName("handling"), ModifierOperation.Add, 2), Assert.Single(attachment.Effects));
    }

    [Theory]
    [InlineData("""{ "id": "mymod:weapon_category/x" }""")]
    [InlineData("""{ "id": "Bad Id", "handling": 1 }""")]
    [InlineData("""{ "id": "mymod:weapon_category/x", "handling": -1 }""")]
    [InlineData("""{ "id": "mymod:weapon_category/x", "handling": 1, "mounts": ["Bad Mount"] }""")]
    [InlineData("not json")]
    public void Json_RejectsInvalidCategories(string json)
    {
        Assert.Throws<WeaponDefinitionException>(() => WeaponDefinitionJson.ParseCategory(json));
    }

    [Theory]
    [InlineData("""{ "id": "m:weapon/x", "item": "m:item/x", "category": "m:weapon_category/x", "damage": 1, "damageType": "laser", "rateOfFire": 1, "reach": 1, "noise": 1 }""")]
    [InlineData("""{ "id": "m:weapon/x", "item": "m:item/x", "category": "m:weapon_category/x", "damage": -1, "damageType": "cut", "rateOfFire": 1, "reach": 1, "noise": 1 }""")]
    [InlineData("""{ "id": "m:weapon/x", "item": "bad", "category": "m:weapon_category/x", "damage": 1, "damageType": "cut", "rateOfFire": 1, "reach": 1, "noise": 1 }""")]
    [InlineData("""{ "id": "m:weapon/x", "item": "m:item/x", "category": "m:weapon_category/x", "damage": 1, "damageType": "cut", "rateOfFire": 1, "reach": 1, "noise": 1, "handsNeeded": 3 }""")]
    [InlineData("""{ "id": "m:weapon/x", "item": "m:item/x", "category": "m:weapon_category/x", "damage": 1 }""")]
    public void Json_RejectsInvalidWeapons(string json)
    {
        Assert.Throws<WeaponDefinitionException>(() => WeaponDefinitionJson.ParseWeapon(json));
    }

    [Theory]
    [InlineData("""{ "id": "m:attachment/x", "item": "m:item/x", "mount": "Bad" }""")]
    [InlineData("""{ "id": "m:attachment/x", "item": "m:item/x", "mount": "optic", "effects": [ { "stat": "Bad", "operation": "add", "value": 1 } ] }""")]
    [InlineData("""{ "id": "m:attachment/x", "item": "m:item/x", "mount": "optic", "effects": [ { "stat": "noise", "operation": "set", "value": 1 } ] }""")]
    public void Json_RejectsInvalidAttachments(string json)
    {
        Assert.Throws<WeaponDefinitionException>(() => WeaponDefinitionJson.ParseAttachment(json));
    }

    [Fact]
    public void ModCanAddACategoryWeaponAndAttachmentWithJsonAlone()
    {
        var result = ModLoader.Load(
        [
            new ModPackage(
                "crossbows",
                """{ "id": "crossbows", "version": "1.0.0" }""",
                [
                    new ModFile("data/cat.json", """{ "id": "crossbows:weapon_category/crossbow", "handling": 4, "mounts": ["optic"] }"""),
                    new ModFile("data/weapon.json", """{ "id": "crossbows:weapon/hunter", "item": "crossbows:item/hunter", "category": "crossbows:weapon_category/crossbow", "damage": 40, "damageType": "pierce", "rateOfFire": 0.5, "reach": 40, "noise": 10 }"""),
                    new ModFile("data/att.json", """{ "id": "crossbows:attachment/sight", "item": "crossbows:item/sight", "mount": "optic", "effects": [ { "stat": "reach", "operation": "multiply", "value": 2 } ] }"""),
                ]),
        ]);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        string Json(string kind) => result.Registry.OfKind(kind).Single().Json;
        var catalog = new WeaponCatalog(
            [WeaponDefinitionJson.ParseCategory(Json("weapon_category"))],
            [WeaponDefinitionJson.ParseWeapon(Json("weapon"))],
            [WeaponDefinitionJson.ParseAttachment(Json("attachment"))]);
        var service = new WeaponService(catalog);

        var hunter = new ItemId("crossbows:item/hunter");
        var fitted = service.Attach(hunter, null, new ItemId("crossbows:item/sight"));

        Assert.True(fitted.IsSuccess);
        Assert.True(service.TryGetEffectiveStats(hunter, fitted.State, out var stats));
        Assert.Equal(80, stats.Reach, 6);
    }
}