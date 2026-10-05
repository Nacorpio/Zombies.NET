using UnitsNet;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class InventoryViewTests
{
    private static readonly ItemId Beans = new("base:item/canned_beans");
    private static readonly ItemId Bandage = new("base:item/bandage");
    private static readonly ItemId Pebble = new("base:item/pebble");
    private static readonly ItemId Pistol = new("base:item/pistol_9mm");
    private static readonly ItemId Spoon = new("base:item/rusty_spoon");

    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "item.base.item.canned_beans": "Canned beans",
              "item.base.item.bandage": "Bandage",
              "inv.backpack": "Backpack",
              "inv.ground": "Ground",
              "error.exceeds_mass_limit": "Too heavy",
              "error.item_mismatch": "Cannot merge",
              "error.insufficient_items": "Not enough" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private sealed class Harness
    {
        public Harness(Mass? backpackLimit = null)
        {
            Catalog = new ItemCatalog([
                new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), 4),
                new ItemDefinition(Bandage, Mass.FromGrams(20), Volume.FromLiters(0.05), 10),
                new ItemDefinition(Pebble, Mass.FromGrams(50), Volume.FromLiters(0.02), 1),
                new ItemDefinition(Pistol, Mass.FromKilograms(1), Volume.FromLiters(0.5), 1),
                new ItemDefinition(Spoon, Mass.FromGrams(30), Volume.FromLiters(0.02), 1),
            ]);
            Containers = new InMemoryContainerRepository();
            Inventory = new InventoryService(Catalog, Containers);
            Localizer = English();
            View = new InventoryView(Inventory, Containers, Catalog, Localizer);

            Backpack = new ContainerId(1);
            Ground = new ContainerId(2);
            Containers.TryAdd(new Container(Backpack, backpackLimit ?? Mass.FromKilograms(20), Volume.FromLiters(30), Catalog));
            Containers.TryAdd(new Container(Ground, Mass.FromKilograms(1000), Volume.FromLiters(1000), Catalog));
            View.AddTarget(Backpack, "inv.backpack");
            View.AddTarget(Ground, "inv.ground");
        }

        public ItemCatalog Catalog { get; }

        public InMemoryContainerRepository Containers { get; }

        public InventoryService Inventory { get; }

        public Localizer Localizer { get; }

        public InventoryView View { get; }

        public ContainerId Backpack { get; }

        public ContainerId Ground { get; }

        public Container Container(ContainerId id)
        {
            Assert.True(Containers.TryGet(id, out var container));
            return container;
        }

        public StackId Add(ContainerId container, ItemId item, int count, ItemState? state = null)
        {
            Assert.True(Inventory.AddItems(container, item, count, state).IsSuccess);
            return Container(container).Stacks.Last(s => s.Item == item && Equals(s.State, state)).Id;
        }
    }

    [Fact]
    public void Slots_ShowEveryStackWithItsLocalizedNameAndCount()
    {
        var harness = new Harness();
        harness.Add(harness.Backpack, Beans, 3);
        harness.Add(harness.Backpack, Bandage, 2);

        var slots = harness.View.Slots(harness.Backpack);

        Assert.Equal(["Canned beans", "Bandage"], slots.Select(s => s.Label));
        Assert.Equal([3, 2], slots.Select(s => s.Count));
        Assert.All(slots, s => Assert.Equal(harness.Backpack, s.Container));
    }

    [Fact]
    public void Slots_AnItemWithNoTranslation_ShowsAReadableNameInsteadOfTheKey()
    {
        var harness = new Harness();
        harness.Add(harness.Backpack, Spoon, 1);

        Assert.Equal("Rusty spoon", Assert.Single(harness.View.Slots(harness.Backpack)).Label);    }

    [Fact]
    public void Slots_ShowHowFullTheContainerIs()
    {
        var harness = new Harness();
        harness.Add(harness.Backpack, Beans, 4);

        var panel = harness.View.Panel(harness.Backpack);

        Assert.Equal("Backpack", panel.Title);
        Assert.Equal(1.6 / 20, panel.MassFraction, 3);
        Assert.Equal(1.4 / 30, panel.VolumeFraction, 3);
    }

    [Fact]
    public void ListAndGrid_ShowTheSameModelInTheSameOrder()
    {
        var harness = new Harness();
        harness.Add(harness.Backpack, Beans, 3);
        harness.Add(harness.Backpack, Bandage, 2);

        harness.View.Mode = InventoryViewMode.List;
        var list = harness.View.Slots(harness.Backpack);
        harness.View.Mode = InventoryViewMode.Grid;
        var grid = harness.View.Slots(harness.Backpack);

        Assert.Equal(list, grid);
    }

    [Fact]
    public void List_StacksSlotsInOneColumn_AndGridUsesSeveral()
    {
        var harness = new Harness();
        for (var i = 0; i < 4; i++)
        {
            harness.Add(harness.Backpack, Pebble, 1);
        }

        var panel = new UiRect(0, 0, 200, 200);
        harness.View.Mode = InventoryViewMode.List;
        var list = harness.View.ArrangeSlots(harness.Backpack, panel, textScale: 1);
        harness.View.Mode = InventoryViewMode.Grid;
        harness.View.Columns = 2;
        var grid = harness.View.ArrangeSlots(harness.Backpack, panel, textScale: 1);

        Assert.Equal(4, list.Count);
        Assert.Equal(4, grid.Count);
        Assert.Single(list.Select(r => r.X).Distinct());
        Assert.Equal(2, grid.Select(r => r.X).Distinct().Count());
        Assert.All(list, r => Assert.True(r.Width <= panel.Width));
        Assert.All(grid, r => Assert.True(r.Right <= panel.Right));
    }

    [Fact]
    public void ArrangeSlots_KeepsEverySlotInsideThePanel_EvenWhenThereAreMoreThanFit()
    {
        var harness = new Harness();
        for (var i = 0; i < 30; i++)
        {
            harness.Add(harness.Backpack, Pebble, 1);
        }

        var panel = new UiRect(10, 20, 120, 60);
        harness.View.Mode = InventoryViewMode.Grid;
        harness.View.Columns = 3;

        foreach (var rect in harness.View.ArrangeSlots(harness.Backpack, panel, textScale: 1))
        {
            Assert.True(rect.X >= panel.X && rect.Right <= panel.Right, rect.ToString());
            Assert.True(rect.Y >= panel.Y && rect.Bottom <= panel.Bottom, rect.ToString());
        }
    }

    [Fact]
    public void Drag_MovesAWholeStackToAnotherContainer()
    {
        var harness = new Harness();
        var stack = harness.Add(harness.Backpack, Beans, 3);

        Assert.True(harness.View.BeginDrag(harness.Backpack, stack));
        Assert.Equal(3, harness.View.Dragging!.Count);
        Assert.True(harness.View.DropOn(harness.Ground));

        Assert.Empty(harness.View.Slots(harness.Backpack));
        Assert.Equal(3, Assert.Single(harness.View.Slots(harness.Ground)).Count);
        Assert.Null(harness.View.Dragging);
    }

    [Fact]
    public void Drag_OntoAStackOfTheSameItem_MergesThem()
    {
        var harness = new Harness();
        var from = harness.Add(harness.Backpack, Beans, 2);
        var onto = harness.Add(harness.Ground, Beans, 1);

        harness.View.BeginDrag(harness.Backpack, from);
        Assert.True(harness.View.DropOn(harness.Ground, onto));

        Assert.Equal(3, Assert.Single(harness.View.Slots(harness.Ground)).Count);
    }

    [Fact]
    public void Drag_OntoAStackOfADifferentItem_IsRefusedAndTheStackStaysPut()
    {
        var harness = new Harness();
        var from = harness.Add(harness.Backpack, Beans, 2);
        var onto = harness.Add(harness.Ground, Bandage, 1);

        harness.View.BeginDrag(harness.Backpack, from);
        Assert.False(harness.View.DropOn(harness.Ground, onto));

        Assert.Equal(2, Assert.Single(harness.View.Slots(harness.Backpack)).Count);
        Assert.Equal("Cannot merge", harness.View.Message);
    }

    [Fact]
    public void Drag_ThatWouldNotFit_IsRefusedWithAReasonAndNothingMoves()
    {
        var harness = new Harness(backpackLimit: Mass.FromKilograms(1));
        var stack = harness.Add(harness.Ground, Beans, 4);

        harness.View.BeginDrag(harness.Ground, stack);
        Assert.False(harness.View.DropOn(harness.Backpack));

        Assert.Equal(4, Assert.Single(harness.View.Slots(harness.Ground)).Count);
        Assert.Empty(harness.View.Slots(harness.Backpack));
        Assert.Equal("Too heavy", harness.View.Message);
    }

    [Fact]
    public void Drag_OntoItsOwnContainer_IsANoOpThatSucceeds()
    {
        var harness = new Harness();
        var stack = harness.Add(harness.Backpack, Beans, 2);

        harness.View.BeginDrag(harness.Backpack, stack);

        Assert.True(harness.View.DropOn(harness.Backpack));
        Assert.Equal(2, Assert.Single(harness.View.Slots(harness.Backpack)).Count);
        Assert.Null(harness.View.Message);
    }

    [Fact]
    public void CancelDrag_PutsTheStackBack()
    {
        var harness = new Harness();
        var stack = harness.Add(harness.Backpack, Beans, 2);

        harness.View.BeginDrag(harness.Backpack, stack);
        harness.View.CancelDrag();

        Assert.Null(harness.View.Dragging);
        Assert.Equal(2, Assert.Single(harness.View.Slots(harness.Backpack)).Count);
    }

    [Fact]
    public void BeginDrag_OfAStackThatIsGone_IsRefused()
    {
        var harness = new Harness();

        Assert.False(harness.View.BeginDrag(harness.Backpack, new StackId(99)));
        Assert.Null(harness.View.Dragging);
    }

    [Fact]
    public void Drop_WithNothingBeingDragged_IsRefused()
    {
        var harness = new Harness();

        Assert.False(harness.View.DropOn(harness.Ground));
    }

    [Fact]
    public void Targets_AreListedInTheOrderTheyWereAdded_WithLocalizedTitles()
    {
        var harness = new Harness();

        Assert.Equal(["Backpack", "Ground"], harness.View.Targets.Select(t => t.Title));
    }

    [Fact]
    public void AWeaponSlot_ShowsItsConditionAndRounds()
    {
        var harness = new Harness();
        var pistol = Pistol;
        harness.Add(harness.Backpack, pistol, 1, ItemState.Create([new("condition", 40), new("rounds", 7)]));

        var slot = Assert.Single(harness.View.Slots(harness.Backpack));

        Assert.Equal(40, slot.Condition);
        Assert.Equal(7, slot.Rounds);
    }

    [Fact]
    public void AnOrdinaryItemSlot_HasNoConditionOrRounds()
    {
        var harness = new Harness();
        harness.Add(harness.Backpack, Beans, 1);

        var slot = Assert.Single(harness.View.Slots(harness.Backpack));

        Assert.Null(slot.Condition);
        Assert.Null(slot.Rounds);
    }
}
