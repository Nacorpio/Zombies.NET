using UnitsNet;
using Zombies.Domain.Actions;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class ItemActionTests
{
    private static readonly ItemId Beans = new("test:item/beans");
    private static readonly ItemId Water = new("test:item/water");
    private static readonly ItemId Shirt = new("test:item/shirt");
    private static readonly ItemId Bat = new("test:item/bat");
    private static readonly ItemId Rock = new("test:item/rock");

    private static readonly ItemActionCatalog Catalog = new(
        new[]
        {
            """{ "id": "test:item_action/use", "label": "action.use", "icon": "use", "group": "use", "order": 0, "appliesWhen": [ "consumable" ] }""",
            """{ "id": "test:item_action/equip", "label": "action.equip", "icon": "equip", "group": "use", "order": 1, "appliesWhen": [ "wearable" ] }""",
            """{ "id": "test:item_action/inspect", "label": "action.inspect", "icon": "inspect", "group": "inspect", "order": 0 }""",
            """{ "id": "test:item_action/drop", "label": "action.drop", "icon": "drop", "group": "move", "order": 0, "enabledWhen": [ "own_container", "writable_container" ] }""",
            """{ "id": "test:item_action/split", "label": "action.split", "icon": "split", "group": "move", "order": 1, "appliesWhen": [ "stack_count_above_one" ], "enabledWhen": [ "own_container", "writable_container" ] }""",
        }.Select(ItemActionJson.Parse));

    private static readonly ItemActionService Service = new(
        Catalog,
        new ItemCatalog(
        [
            new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), 8, edible: true),
            new ItemDefinition(Water, Mass.FromKilograms(1), Volume.FromLiters(1), 2, drinkable: true),
            new ItemDefinition(Shirt, Mass.FromKilograms(0.3), Volume.FromLiters(1), 1),
            new ItemDefinition(Bat, Mass.FromKilograms(1), Volume.FromLiters(1), 1),
            new ItemDefinition(Rock, Mass.FromKilograms(2), Volume.FromLiters(1), 1),
        ]),
        new WearableCatalog([new WearableDefinition(Shirt, ClothingLayer.Base, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1))]),
        new WeaponCatalog(
            [new WeaponCategory("test:weapon_category/blunt", 5, [])],
            [new WeaponDefinition(Bat, "test:weapon_category/blunt", 20, DamageType.Blunt, 1, 1, 20)],
            []));

    private static readonly ItemActionContext Own = new(IsOwnContainer: true, IsReadOnly: false);
    private static readonly ItemActionContext ReadOnly = new(IsOwnContainer: true, IsReadOnly: true);
    private static readonly ItemActionContext Foreign = new(IsOwnContainer: false, IsReadOnly: false);

    private static ItemStack Stack(ItemId item, int count = 1) => new(new StackId(1), item, count);

    private static IReadOnlyList<AvailableAction> Available(ItemId item, int count = 1, ItemActionContext? context = null) =>
        Service.Available(Stack(item, count), context ?? Own);

    [Fact]
    public void Available_ReturnsActionsGroupedAndOrdered()
    {
        var actions = Available(Beans, 4);

        Assert.Equal(["inspect", "drop", "split", "use"], actions.Select(a => a.Id.Split('/')[1]));
        Assert.Equal(["inspect", "move", "move", "use"], actions.Select(a => a.Group));
        Assert.All(actions, a => Assert.True(a.IsEnabled));
    }

    [Fact]
    public void Split_IsOnlyOfferedForStacksAboveOne()
    {
        Assert.DoesNotContain(Available(Beans, 1), a => a.Id.EndsWith("split", StringComparison.Ordinal));
        Assert.Contains(Available(Beans, 2), a => a.Id.EndsWith("split", StringComparison.Ordinal));
    }

    [Fact]
    public void Equip_IsOnlyOfferedForWearables()
    {
        Assert.Contains(Available(Shirt), a => a.Id.EndsWith("equip", StringComparison.Ordinal));
        Assert.DoesNotContain(Available(Beans), a => a.Id.EndsWith("equip", StringComparison.Ordinal));
    }

    [Fact]
    public void Use_IsOnlyOfferedForEdibleOrDrinkableItems()
    {
        Assert.Contains(Available(Beans), a => a.Id.EndsWith("use", StringComparison.Ordinal));
        Assert.Contains(Available(Water), a => a.Id.EndsWith("use", StringComparison.Ordinal));
        Assert.DoesNotContain(Available(Rock), a => a.Id.EndsWith("use", StringComparison.Ordinal));
    }

    [Fact]
    public void ActionsThatMakeNoSenseAreOmittedRatherThanDisabled()
    {
        var actions = Available(Rock);

        Assert.Equal(["inspect", "drop"], actions.Select(a => a.Id.Split('/')[1]));
        Assert.All(actions, a => Assert.True(a.IsEnabled));
    }

    [Fact]
    public void ActionsThatCannotRunNowAreShownDisabledWithAReason()
    {
        var actions = Available(Beans, 4, ReadOnly);

        var drop = actions.Single(a => a.Id.EndsWith("drop", StringComparison.Ordinal));
        var split = actions.Single(a => a.Id.EndsWith("split", StringComparison.Ordinal));
        Assert.False(drop.IsEnabled);
        Assert.Equal("container_read_only", drop.DisabledReason);
        Assert.False(split.IsEnabled);
        Assert.Equal("container_read_only", split.DisabledReason);
        Assert.True(actions.Single(a => a.Id.EndsWith("inspect", StringComparison.Ordinal)).IsEnabled);
    }

    [Fact]
    public void ForeignContainer_DisablesActionsThatChangeIt()
    {
        var actions = Available(Beans, 4, Foreign);

        Assert.Equal("not_your_container", actions.Single(a => a.Id.EndsWith("drop", StringComparison.Ordinal)).DisabledReason);
        Assert.True(actions.Single(a => a.Id.EndsWith("use", StringComparison.Ordinal)).IsEnabled);
    }

    [Fact]
    public void DisabledReason_CanBeOverriddenByTheDefinition()
    {
        var catalog = new ItemActionCatalog([ItemActionJson.Parse("""{ "id": "test:item_action/drop", "label": "action.drop", "icon": "drop", "group": "move", "enabledWhen": [ "writable_container" ], "disabledReason": "cannot_drop_here" }""")]);
        var service = new ItemActionService(catalog, new ItemCatalog([new ItemDefinition(Rock, Mass.FromKilograms(1), Volume.FromLiters(1), 1)]), new WearableCatalog([]), new WeaponCatalog([], [], []));

        var drop = service.Available(Stack(Rock), ReadOnly).Single();

        Assert.Equal("cannot_drop_here", drop.DisabledReason);
    }

    [Fact]
    public void Json_ParsesADefinition()
    {
        var definition = ItemActionJson.Parse("""{ "id": "m:item_action/x", "label": "action.x", "icon": "use", "group": "use", "order": 3, "appliesWhen": [ "weapon" ], "enabledWhen": [ "own_container" ] }""");

        Assert.Equal((3, "action.x", "use"), (definition.Order, definition.Label, definition.Icon));
        Assert.Equal([ActionCondition.Weapon], definition.AppliesWhen);
        Assert.Equal([ActionCondition.OwnContainer], definition.EnabledWhen);
    }

    [Theory]
    [InlineData("""{ "id": "m:item_action/x" }""")]
    [InlineData("""{ "id": "Bad", "label": "a", "icon": "use", "group": "use" }""")]
    [InlineData("""{ "id": "m:item_action/x", "label": "a", "icon": "Bad Icon", "group": "use" }""")]
    [InlineData("""{ "id": "m:item_action/x", "label": "a", "icon": "use", "group": "use", "appliesWhen": [ "own_container" ] }""")]
    [InlineData("""{ "id": "m:item_action/x", "label": "a", "icon": "use", "group": "use", "enabledWhen": [ "wearable" ] }""")]
    [InlineData("""{ "id": "m:item_action/x", "label": "a", "icon": "use", "group": "use", "appliesWhen": [ "nonsense" ] }""")]
    [InlineData("not json")]
    public void Json_RejectsInvalidDefinitions(string json)
    {
        Assert.Throws<ItemActionDefinitionException>(() => ItemActionJson.Parse(json));
    }

    [Fact]
    public void Catalog_RejectsDuplicates()
    {
        var definition = new ItemActionDefinition("m:item_action/x", "action.x", "use", "use", 0);
        Assert.Throws<ArgumentException>(() => new ItemActionCatalog([definition, definition]));
    }
}
