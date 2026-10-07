using UnitsNet;
using Zombies.Domain.Actions;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

/// <summary>A right click on a Stack: the menu lists what the Domain offers, and choosing an entry runs the matching Domain command.</summary>
public sealed class InventoryActionMenuTests
{
    private static readonly ItemId Beans = new("test:item/beans");
    private static readonly ItemId Rock = new("test:item/rock");
    private static readonly ItemId Shirt = new("test:item/shirt");
    private static readonly ItemId Tee = new("test:item/tee");
    private static readonly ItemId Pistol = new("test:item/pistol");

    private static readonly ContainerId Backpack = new(1);
    private static readonly ContainerId Ground = new(2);
    private static readonly ContainerId Crate = new(3);

    private static readonly UiRect Screen = new(0, 0, 1280, 720);

    private static readonly string[] ActionJson =
    [
        """{ "id": "base:item_action/use", "label": "menu.use", "icon": "use", "group": "use", "order": 0, "appliesWhen": [ "consumable" ] }""",
        """{ "id": "base:item_action/equip", "label": "menu.equip", "icon": "equip", "group": "use", "order": 1, "appliesWhen": [ "wearable" ] }""",
        """{ "id": "base:item_action/inspect", "label": "menu.inspect", "icon": "inspect", "group": "inspect", "order": 0 }""",
        """{ "id": "base:item_action/drop", "label": "menu.drop", "icon": "drop", "group": "move", "order": 0, "enabledWhen": [ "own_container", "writable_container" ] }""",
        """{ "id": "base:item_action/split", "label": "menu.split", "icon": "split", "group": "move", "order": 1, "appliesWhen": [ "stack_count_above_one" ], "enabledWhen": [ "own_container", "writable_container" ] }""",
    ];

    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "item.test.item.beans": "Canned beans",
              "menu.use": "Use", "menu.equip": "Equip", "menu.drop": "Drop", "menu.split": "Split stack", "menu.inspect": "Inspect",
              "menu.reason.container_read_only": "This container is read only",
              "menu.reason.not_your_container": "That is not your container",
              "menu.reason.not_available": "Not available right now",
              "inspect.count": "Count {0}", "inspect.mass": "Mass {0} kg", "inspect.volume": "Volume {0} l",
              "error.unknown_stack": "That stack is gone",
              "error.nothing_to_do": "Nothing to do with that",
              "error.not_wearable": "That cannot be worn",
              "error.layer_occupied": "Something is already worn there" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private sealed class Harness
    {
        public Harness(params string[] extraActions)
        {
            var items = new ItemCatalog(
            [
                new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), 4, edible: true),
                new ItemDefinition(Rock, Mass.FromKilograms(2), Volume.FromLiters(1), 1),
                new ItemDefinition(Shirt, Mass.FromKilograms(0.3), Volume.FromLiters(1), 1),
                new ItemDefinition(Tee, Mass.FromKilograms(0.2), Volume.FromLiters(1), 1),
                new ItemDefinition(Pistol, Mass.FromKilograms(1), Volume.FromLiters(0.5), 1),
            ]);
            var wearables = new WearableCatalog(
            [
                new WearableDefinition(Shirt, ClothingLayer.Base, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.05)),
                new WearableDefinition(Tee, ClothingLayer.Base, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.02)),
            ]);
            var weapons = new WeaponCatalog(
                [new WeaponCategory("test:weapon_category/pistol", 10, ["muzzle"])],
                [new WeaponDefinition(Pistol, "test:weapon_category/pistol", 30, DamageType.Pierce, 2, 25, 90)],
                []);
            Containers = new InMemoryContainerRepository();
            Inventory = new InventoryService(items, Containers);
            Outfit = new Outfit(wearables);
            Actions = new ItemActionService(new ItemActionCatalog(ActionJson.Concat(extraActions).Select(ItemActionJson.Parse)), items, wearables, weapons);
            View = new InventoryView(Inventory, Containers, items, English(), new WeaponFittingService(Inventory, Containers, new WeaponService(weapons)), Actions, Outfit);

            foreach (var id in new[] { Backpack, Ground, Crate })
            {
                Containers.TryAdd(new Container(id, Mass.FromKilograms(50), Volume.FromLiters(50), items));
            }

            View.SetRole(Backpack, new ContainerRole(IsOwn: true, IsReadOnly: false, DropInto: Ground));
            View.SetRole(Ground, new ContainerRole(IsOwn: false, IsReadOnly: false));
            View.SetRole(Crate, new ContainerRole(IsOwn: true, IsReadOnly: true));
        }

        public InMemoryContainerRepository Containers { get; }

        public InventoryService Inventory { get; }

        public Outfit Outfit { get; }

        public ItemActionService Actions { get; }

        public InventoryView View { get; }

        public List<ItemUsed> Uses { get; } = [];

        public StackId Add(ContainerId container, ItemId item, int count, ItemState? state = null)
        {
            Assert.True(Inventory.AddItems(container, item, count, state).IsSuccess);
            return Stacks(container).Last(s => s.Item == item).Id;
        }

        public IReadOnlyList<ItemStack> Stacks(ContainerId container) => Containers.TryGet(container, out var c) ? c.Stacks : [];

        public ItemStack Stack(ContainerId container, StackId id) => Stacks(container).Single(s => s.Id == id);

        public bool Open(ContainerId container, StackId stack) => View.OpenMenu(container, stack, 100, 100, Screen, 1);

        public IReadOnlyList<string> MenuIds => View.Menu.Current?.Items.Select(i => i.Id.Split('/')[1]).ToList() ?? [];

        /// <summary>Clicks the middle of an entry, the way the mouse does.</summary>
        public bool Click(string action)
        {
            var item = View.Menu.Current!.Items.Single(i => i.Id.EndsWith("/" + action, StringComparison.Ordinal));
            return View.Menu.Click(item.Bounds.X + (item.Bounds.Width / 2), item.Bounds.Y + (item.Bounds.Height / 2));
        }
    }

    [Fact]
    public void RightClick_ListsExactlyTheActionsTheDomainOffers_InItsOrderAndGroups()
    {
        var h = new Harness();
        var stack = h.Add(Backpack, Beans, 3);
        var offered = h.Actions.Available(h.Stack(Backpack, stack), new ItemActionContext(IsOwnContainer: true, IsReadOnly: false));

        Assert.True(h.Open(Backpack, stack));

        var items = h.View.Menu.Current!.Items;
        Assert.Equal(offered.Select(a => a.Id), items.Select(i => i.Id));
        Assert.Equal(["inspect", "drop", "split", "use"], h.MenuIds);
        Assert.Equal(["Inspect", "Drop", "Split stack", "Use"], items.Select(i => i.Label));
        Assert.Equal([false, true, false, true], items.Select(i => i.StartsGroup));
        Assert.All(items, i => Assert.True(i.IsEnabled));
    }

    [Fact]
    public void ActionsThatMakeNoSenseAreOmitted_AndOnesThatCannotRunNowAreDisabledWithTheirReason()
    {
        var h = new Harness();
        var rock = h.Add(Backpack, Rock, 1);
        var beans = h.Add(Crate, Beans, 3);

        Assert.True(h.Open(Backpack, rock));
        Assert.Equal(["inspect", "drop"], h.MenuIds);

        Assert.True(h.Open(Crate, beans));
        var items = h.View.Menu.Current!.Items.ToDictionary(i => i.Id.Split('/')[1]);
        Assert.True(items["inspect"].IsEnabled);
        Assert.True(items["use"].IsEnabled);
        Assert.False(items["drop"].IsEnabled);
        Assert.Equal("This container is read only", items["drop"].DisabledReason);
        Assert.False(items["split"].IsEnabled);
        Assert.Equal("This container is read only", items["split"].DisabledReason);
    }

    [Fact]
    public void ChoosingADisabledEntry_RunsNothing()
    {
        var h = new Harness();
        var beans = h.Add(Crate, Beans, 3);
        Assert.True(h.Open(Crate, beans));

        Assert.True(h.Click("drop"));

        Assert.True(h.View.Menu.IsOpen);
        Assert.Equal(3, h.Stack(Crate, beans).Count);
        Assert.Empty(h.Stacks(Ground));
    }

    [Fact]
    public void Drop_MovesTheWholeStackToTheDropContainer_AndTheViewFollows()
    {
        var h = new Harness();
        var stack = h.Add(Backpack, Beans, 3);
        Assert.True(h.Open(Backpack, stack));

        Assert.True(h.Click("drop"));

        Assert.False(h.View.Menu.IsOpen);
        Assert.Empty(h.View.Slots(Backpack));
        Assert.Equal([(Beans, 3)], h.View.Slots(Ground).Select(s => (s.Item, s.Count)));
    }

    [Fact]
    public void Split_HalvesTheStack_AndTheViewFollows()
    {
        var h = new Harness();
        var stack = h.Add(Backpack, Beans, 4);
        Assert.True(h.Open(Backpack, stack));

        Assert.True(h.Click("split"));

        Assert.Equal([2, 2], h.View.Slots(Backpack).Select(s => s.Count));
        Assert.False(h.View.Menu.IsOpen);
    }

    [Fact]
    public void Use_TakesOneOut_AndTellsTheHostWhichItemWasUsed()
    {
        var h = new Harness();
        h.View.Used += h.Uses.Add;
        var stack = h.Add(Backpack, Beans, 2);

        Assert.True(h.Open(Backpack, stack));
        Assert.True(h.Click("use"));

        Assert.Equal(1, h.Stack(Backpack, stack).Count);
        Assert.Equal(new ItemUsed(Backpack, Beans, null), Assert.Single(h.Uses));

        Assert.True(h.Open(Backpack, stack));
        Assert.True(h.Click("use"));

        Assert.Empty(h.Stacks(Backpack));
        Assert.Equal(2, h.Uses.Count);
    }

    [Fact]
    public void Use_OnAStatefulStack_TakesFromThatStack()
    {
        var h = new Harness();
        h.View.Used += h.Uses.Add;
        var plain = h.Add(Backpack, Beans, 2);
        var marked = h.Add(Backpack, Beans, 2, ItemState.Create([new("fresh", 1)]));

        Assert.True(h.Open(Backpack, marked));
        Assert.True(h.Click("use"));

        Assert.Equal(2, h.Stack(Backpack, plain).Count);
        Assert.Equal(1, h.Stack(Backpack, marked).Count);
        Assert.Equal(ItemState.Create([new("fresh", 1)]), Assert.Single(h.Uses).State);
    }

    [Fact]
    public void Equip_MovesTheItemFromTheContainerToTheOutfit()
    {
        var h = new Harness();
        var shirt = h.Add(Backpack, Shirt, 1);
        Assert.True(h.Open(Backpack, shirt));

        Assert.True(h.Click("equip"));

        Assert.Equal([Shirt], h.Outfit.WornItems);
        Assert.Empty(h.Stacks(Backpack));
    }

    [Fact]
    public void Equip_WhenTheLayerIsTaken_ChangesNothingAndSaysWhy()
    {
        var h = new Harness();
        var shirt = h.Add(Backpack, Shirt, 1);
        var tee = h.Add(Backpack, Tee, 1);
        Assert.True(h.View.Perform(ItemActionIds.Equip, Backpack, shirt));

        Assert.False(h.View.Perform(ItemActionIds.Equip, Backpack, tee));

        Assert.Equal("Something is already worn there", h.View.Message);
        Assert.Equal([Shirt], h.Outfit.WornItems);
        Assert.Equal([Tee], h.Stacks(Backpack).Select(s => s.Item));
    }

    [Fact]
    public void Inspect_OnAWeapon_OpensItsMounts_AndOnAnythingElseShowsWhatItIs()
    {
        var h = new Harness();
        var pistol = h.Add(Backpack, Pistol, 1);
        var beans = h.Add(Backpack, Beans, 3);

        Assert.True(h.Open(Backpack, beans));
        Assert.True(h.Click("inspect"));
        Assert.Equal("Canned beans", h.View.Inspection!.Title);
        Assert.Contains("Count 3", string.Join(' ', h.View.Inspection.Lines));
        Assert.Null(h.View.Weapon);

        Assert.True(h.Open(Backpack, pistol));
        Assert.True(h.Click("inspect"));
        Assert.Equal(Pistol, h.View.Weapon!.Item);
        Assert.Null(h.View.Inspection);

        // Inspecting something else closes the weapon's Mounts again.
        Assert.True(h.Open(Backpack, beans));
        Assert.True(h.Click("inspect"));
        Assert.Null(h.View.Weapon);
        Assert.NotNull(h.View.Inspection);
    }

    [Fact]
    public void ALootedContainer_OffersTheSameActions_WithTheOnesYouCannotRunDisabledForItsOwner()
    {
        var h = new Harness();
        h.View.Used += h.Uses.Add;
        var beans = h.Add(Ground, Beans, 2);

        Assert.True(h.Open(Ground, beans));

        var items = h.View.Menu.Current!.Items.ToDictionary(i => i.Id.Split('/')[1]);
        Assert.Equal("That is not your container", items["drop"].DisabledReason);
        Assert.Equal("That is not your container", items["split"].DisabledReason);
        Assert.True(h.Click("use"));
        Assert.Equal(1, h.Stack(Ground, beans).Count);
        Assert.Equal(Ground, Assert.Single(h.Uses).Container);
    }

    [Fact]
    public void RightClickingEmptySpace_OpensNothing_AndClosesAMenuThatWasOpen()
    {
        var h = new Harness();
        var stack = h.Add(Backpack, Beans, 3);
        Assert.True(h.Open(Backpack, stack));

        Assert.False(h.Open(Backpack, new StackId(99)));

        Assert.False(h.View.Menu.IsOpen);
    }

    [Fact]
    public void RightClicking_WhileDragging_DoesNotOpenAMenuOrDropTheDrag()
    {
        var h = new Harness();
        var stack = h.Add(Backpack, Beans, 3);
        Assert.True(h.View.BeginDrag(Backpack, stack));

        Assert.False(h.Open(Backpack, stack));

        Assert.False(h.View.Menu.IsOpen);
        Assert.NotNull(h.View.Dragging);
    }

    [Fact]
    public void StartingADrag_ClosesTheMenu()
    {
        var h = new Harness();
        var stack = h.Add(Backpack, Beans, 3);
        Assert.True(h.Open(Backpack, stack));

        Assert.True(h.View.BeginDrag(Backpack, stack));

        Assert.False(h.View.Menu.IsOpen);
    }

    [Fact]
    public void AMenuThatWentStale_RunsNothingAndSaysSo()
    {
        var h = new Harness();
        h.View.Used += h.Uses.Add;
        var stack = h.Add(Backpack, Beans, 3);
        Assert.True(h.Open(Backpack, stack));
        Assert.True(h.Inventory.RemoveItems(Backpack, Beans, 3).IsSuccess);

        Assert.True(h.Click("use"));

        Assert.Empty(h.Uses);
        Assert.Equal("That stack is gone", h.View.Message);
    }

    [Fact]
    public void Perform_RefusesAnActionTheDomainDoesNotOffer()
    {
        var h = new Harness();
        var rock = h.Add(Backpack, Rock, 1);

        Assert.False(h.View.Perform(ItemActionIds.Split, Backpack, rock));

        Assert.Equal("Nothing to do with that", h.View.Message);
        Assert.Equal(1, h.Stack(Backpack, rock).Count);
    }

    [Fact]
    public void AModDefinedAction_AppearsInTheMenu_AndIsHandedToTheHostWhenChosen()
    {
        var h = new Harness("""{ "id": "mymod:item_action/sniff", "label": "menu.inspect", "icon": "inspect", "group": "inspect", "order": 5 }""");
        var chosen = new List<ItemActionChosen>();
        h.View.ActionChosen += chosen.Add;
        var stack = h.Add(Backpack, Beans, 1);

        Assert.True(h.Open(Backpack, stack));
        Assert.Contains("sniff", h.MenuIds);
        Assert.True(h.Click("sniff"));

        Assert.Equal(new ItemActionChosen("mymod:item_action/sniff", Backpack, stack), Assert.Single(chosen));
        Assert.Equal(1, h.Stack(Backpack, stack).Count);
    }
    [Fact]
    public void WithoutAnActionService_NoMenuOpens()
    {
        var items = new ItemCatalog([new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), 4)]);
        var containers = new InMemoryContainerRepository();
        var inventory = new InventoryService(items, containers);
        containers.TryAdd(new Container(Backpack, Mass.FromKilograms(50), Volume.FromLiters(50), items));
        inventory.AddItems(Backpack, Beans, 1);
        var view = new InventoryView(inventory, containers, items, English());

        Assert.False(view.OpenMenu(Backpack, containers.TryGet(Backpack, out var c) ? c.Stacks[0].Id : default, 0, 0, Screen, 1));
    }

    [Fact]
    public void EveryLabelAndReasonTheBaseContentUses_HasATranslationInEveryLanguage()
    {
        var loaded = RepositoryMods.For(ProcessRole.Server);
        var localizer = LocalizationLoader.Load(loaded.Packages, loaded.Mods).Localizer;
        var actions = loaded.Mods.Registry.OfKind("item_action").Select(d => ItemActionJson.Parse(d.Json)).ToList();
        Assert.NotEmpty(actions);
        var labelKeys = actions.Select(a => a.Label);
        var reasonKeys = actions.SelectMany(a => a.EnabledWhen.Select(c => $"menu.reason.{a.DisabledReason ?? ItemActionDefinition.ReasonFor(c)}"));
        var needed = labelKeys.Concat(reasonKeys).Distinct().ToList();
        Assert.Equal("en", localizer.Language);
        Assert.All(needed, key => Assert.NotEqual(key, localizer.Get(key)));

        // Get falls back to English, so a missing translation must be found by asking which keys a language lacks.
        foreach (var language in localizer.Languages)
        {
            var missing = localizer.MissingKeys(language);
            Assert.All(needed, key => Assert.DoesNotContain(key, missing));
        }
        Assert.Contains("sv", localizer.Languages);
    }
}
