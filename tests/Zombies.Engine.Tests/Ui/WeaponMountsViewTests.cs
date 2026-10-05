using UnitsNet;
using Zombies.Domain.Actions;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class WeaponMountsViewTests
{
    private static readonly ItemId Pistol = new("base:item/pistol_9mm");
    private static readonly ItemId Suppressor = new("base:item/suppressor");
    private static readonly ItemId Scope = new("base:item/red_dot_sight");
    private static readonly ItemId Beans = new("base:item/canned_beans");

    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "item.base.item.pistol_9mm": "9mm pistol",
              "item.base.item.suppressor": "Suppressor",
              "item.base.item.red_dot_sight": "Red dot sight",
              "item.base.item.canned_beans": "Canned beans",
              "inv.backpack": "Backpack",
              "inv.ground": "Ground",
              "mount.muzzle": "Muzzle",
              "mount.optic": "Optic",
              "mount.free": "Nothing fitted",
              "mount.no_change": "No change",
              "mount.stat_change": "{0} {1}",
              "mount.refused.mount_occupied": "Something is already fitted here",
              "mount.refused.unknown_attachment": "That is not an attachment",
              "mount.refused.wrong_mount": "It belongs on: {0}",
              "stat.damage": "Damage",
              "stat.reach": "Reach",
              "stat.noise": "Noise",
              "dialog.mount_refused.title": "Cannot fit",
              "dialog.mount_refused.caption": "{0} on {1}",
              "dialog.mount_refused.ok": "OK",
              "error.exceeds_mass_limit": "Too heavy to carry" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var items = new ItemCatalog([
                new ItemDefinition(Pistol, Mass.FromKilograms(1), Volume.FromLiters(0.5), 1),
                new ItemDefinition(Suppressor, Mass.FromKilograms(0.2), Volume.FromLiters(0.2), 4),
                new ItemDefinition(Scope, Mass.FromKilograms(0.1), Volume.FromLiters(0.1), 4),
                new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), 4),
            ]);
            Containers = new InMemoryContainerRepository();
            Inventory = new InventoryService(items, Containers);
            var weapons = new WeaponService(new WeaponCatalog(
                [new WeaponCategory("base:weapon_category/pistol", 10, ["muzzle", "optic"])],
                [new WeaponDefinition(Pistol, "base:weapon_category/pistol", 35, DamageType.Pierce, 2.5, 30, 90)],
                [
                    new AttachmentDefinition(Suppressor, "muzzle", [new(WeaponService.Noise, ModifierOperation.Multiply, 0.2), new(WeaponService.Damage, ModifierOperation.Add, -2)]),
                    new AttachmentDefinition(Scope, "optic", [new(WeaponService.Reach, ModifierOperation.Multiply, 1.5)]),
                ]));
            View = new InventoryView(Inventory, Containers, items, English(), new WeaponFittingService(Inventory, Containers, weapons));

            Containers.TryAdd(new Container(Backpack, Mass.FromKilograms(20), Volume.FromLiters(30), items));
            Containers.TryAdd(new Container(Ground, Mass.FromKilograms(1000), Volume.FromLiters(1000), items));
            View.AddTarget(Backpack, "inv.backpack");
            View.AddTarget(Ground, "inv.ground");
            Weapon = Add(Backpack, Pistol, 1);
        }

        public InMemoryContainerRepository Containers { get; }

        public InventoryService Inventory { get; }

        public InventoryView View { get; }

        public ContainerId Backpack { get; } = new(1);

        public ContainerId Ground { get; } = new(2);

        public StackId Weapon { get; }

        public StackId Add(ContainerId container, ItemId item, int count)
        {
            Assert.True(Inventory.AddItems(container, item, count).IsSuccess);
            Assert.True(Containers.TryGet(container, out var found));
            return found.Stacks.Last(s => s.Item == item).Id;
        }

        public int CountOf(ContainerId container, ItemId item)
        {
            Assert.True(Containers.TryGet(container, out var found));
            return found.CountOf(item);
        }

        public Harness Opened()
        {
            Assert.True(View.OpenWeapon(Backpack, Weapon));
            return this;
        }

        public void Fit(ItemId attachment, string mount)
        {
            Assert.True(View.BeginDrag(Ground, Add(Ground, attachment, 1)));
            Assert.True(View.DropOnMount(mount));
        }
    }

    [Fact]
    public void OpeningAWeapon_ShowsEveryMountItOffers_AllFreeAtFirst()
    {
        var harness = new Harness().Opened();

        Assert.Equal("9mm pistol", harness.View.Weapon!.Label);
        Assert.Equal(["Muzzle", "Optic"], harness.View.Mounts.Select(m => m.Label));
        Assert.All(harness.View.Mounts, m => Assert.Null(m.Fitted));
    }

    [Fact]
    public void OpeningSomethingThatIsNotAWeapon_OpensNothing()
    {
        var harness = new Harness();
        var beans = harness.Add(harness.Backpack, Beans, 1);

        Assert.False(harness.View.OpenWeapon(harness.Backpack, beans));
        Assert.Null(harness.View.Weapon);
        Assert.Empty(harness.View.Mounts);
    }

    [Fact]
    public void DraggingAnAttachmentOntoACompatibleMount_FitsItAndTakesItFromItsContainer()
    {
        var harness = new Harness().Opened();

        harness.Fit(Suppressor, "muzzle");

        var muzzle = harness.View.Mounts.Single(m => m.Mount == "muzzle");
        Assert.Equal(Suppressor, muzzle.Fitted);
        Assert.Equal("Suppressor", muzzle.FittedLabel);
        Assert.Equal(0, harness.CountOf(harness.Ground, Suppressor));
        Assert.Null(harness.View.Dragging);
        Assert.Null(harness.View.Refusal);
    }

    [Fact]
    public void DraggingAFittedAttachmentIntoAContainer_TakesItOffTheWeapon()
    {
        var harness = new Harness().Opened();
        harness.Fit(Suppressor, "muzzle");

        Assert.True(harness.View.BeginDragFromMount("muzzle"));
        Assert.True(harness.View.DropOn(harness.Ground));

        Assert.Null(harness.View.Mounts.Single(m => m.Mount == "muzzle").Fitted);
        Assert.Equal(1, harness.CountOf(harness.Ground, Suppressor));
        Assert.Null(harness.View.Dragging);
    }

    [Fact]
    public void DraggingAFittedAttachmentIntoTheWeaponsOwnContainer_AlsoTakesItOff()
    {
        var harness = new Harness().Opened();
        harness.Fit(Suppressor, "muzzle");

        Assert.True(harness.View.BeginDragFromMount("muzzle"));
        Assert.True(harness.View.DropOn(harness.Backpack));

        Assert.Null(harness.View.Mounts.Single(m => m.Mount == "muzzle").Fitted);
        Assert.Equal(1, harness.CountOf(harness.Backpack, Suppressor));
    }

    [Fact]
    public void AFreeMount_HasNothingToPickUp()
    {
        var harness = new Harness().Opened();

        Assert.False(harness.View.BeginDragFromMount("optic"));
        Assert.Null(harness.View.Dragging);
    }

    [Fact]
    public void DroppingOnTheWrongMount_ChangesNothing_AndACaptionedDialogSaysWhereItBelongs()
    {
        var harness = new Harness().Opened();
        var suppressor = harness.Add(harness.Ground, Suppressor, 1);

        Assert.True(harness.View.BeginDrag(harness.Ground, suppressor));
        Assert.False(harness.View.DropOnMount("optic"));

        Assert.All(harness.View.Mounts, m => Assert.Null(m.Fitted));
        Assert.Equal(1, harness.CountOf(harness.Ground, Suppressor));
        var dialog = harness.View.Refusal!;
        Assert.Equal("Cannot fit", dialog.Title);
        Assert.Equal(["It belongs on: Muzzle"], dialog.MessageLines);
        Assert.Equal("Suppressor on Optic", dialog.Caption);
        Assert.Equal("OK", Assert.Single(dialog.Buttons).Label);
        Assert.Equal("It belongs on: Muzzle", harness.View.Message);

        harness.View.DismissRefusal();
        Assert.Null(harness.View.Refusal);
    }

    [Fact]
    public void DroppingOnAnOccupiedMount_IsRefusedWithTheReason()
    {
        var harness = new Harness().Opened();
        harness.Fit(Suppressor, "muzzle");

        Assert.True(harness.View.BeginDrag(harness.Ground, harness.Add(harness.Ground, Suppressor, 1)));
        Assert.False(harness.View.DropOnMount("muzzle"));

        Assert.Equal(["Something is already fitted here"], harness.View.Refusal!.MessageLines);
        Assert.Equal(1, harness.CountOf(harness.Ground, Suppressor));
    }

    [Fact]
    public void TheMountTooltip_ShowsTheStatChangeBeforeTheDrop_AndNothingChangesYet()
    {
        var harness = new Harness().Opened();
        Assert.True(harness.View.BeginDrag(harness.Ground, harness.Add(harness.Ground, Suppressor, 1)));

        var tooltip = harness.View.MountTooltip("muzzle");

        Assert.Equal("Muzzle", tooltip.Title);
        Assert.Equal(["Damage -2, Noise -72"], tooltip.Lines);
        Assert.Equal("Suppressor", tooltip.Caption);
        Assert.All(tooltip.Lines, l => Assert.True(l.Length <= Tooltip.MaxCharsPerLine));
        Assert.Null(harness.View.Mounts.Single(m => m.Mount == "muzzle").Fitted);
        Assert.Equal([-2, -72], harness.View.Changes("muzzle").Select(c => Math.Round(c.Delta, 6)));
    }

    [Fact]
    public void TheMountTooltip_ExplainsWhyAMountRefusesWhatIsDragged()
    {
        var harness = new Harness().Opened();
        Assert.True(harness.View.BeginDrag(harness.Backpack, harness.Add(harness.Backpack, Beans, 1)));

        Assert.Equal(["That is not an attachment"], harness.View.MountTooltip("muzzle").Lines);
        Assert.Empty(harness.View.Changes("muzzle"));
    }

    [Fact]
    public void TheMountTooltip_OfAFittedMount_ShowsWhatItsAttachmentAdds_AndAFreeOneSaysSo()
    {
        var harness = new Harness().Opened();
        harness.Fit(Scope, "optic");

        var optic = harness.View.MountTooltip("optic");
        Assert.Equal(["Reach +15"], optic.Lines);
        Assert.Equal("Red dot sight", optic.Caption);
        Assert.Equal(["Nothing fitted"], harness.View.MountTooltip("muzzle").Lines);
    }

    [Fact]
    public void ArrangeMounts_PutsOneSlotPerMountInARow_InsideThePanel()
    {
        var harness = new Harness().Opened();
        var panel = new UiRect(10, 100, 300, 30);

        var rects = harness.View.ArrangeMounts(panel, 1);

        Assert.Equal(2, rects.Count);
        Assert.True(rects[0].Right <= rects[1].X);
        Assert.All(rects, r => Assert.True(r.X >= panel.X && r.Right <= panel.Right && r.Y >= panel.Y && r.Bottom <= panel.Bottom));
        Assert.Empty(new Harness().View.ArrangeMounts(panel, 1));
    }

    [Fact]
    public void WithoutAWeaponFittingService_TheInventoryOpensNoWeapon()
    {
        var items = new ItemCatalog([new ItemDefinition(Pistol, Mass.FromKilograms(1), Volume.FromLiters(0.5), 1)]);
        var containers = new InMemoryContainerRepository();
        var inventory = new InventoryService(items, containers);
        containers.TryAdd(new Container(new ContainerId(1), Mass.FromKilograms(20), Volume.FromLiters(30), items));
        Assert.True(inventory.AddItems(new ContainerId(1), Pistol, 1).IsSuccess);
        Assert.True(containers.TryGet(new ContainerId(1), out var backpack));
        var view = new InventoryView(inventory, containers, items, English());

        Assert.False(view.OpenWeapon(new ContainerId(1), backpack.Stacks[0].Id));
    }
}
