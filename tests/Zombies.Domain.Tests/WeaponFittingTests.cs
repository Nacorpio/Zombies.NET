using UnitsNet;
using Zombies.Domain.Actions;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class WeaponFittingTests
{
    private static readonly ItemId Pistol = new("test:item/pistol");
    private static readonly ItemId Suppressor = new("test:item/suppressor");
    private static readonly ItemId Scope = new("test:item/scope");
    private static readonly ItemId Rock = new("test:item/rock");

    private readonly InMemoryContainerRepository _containers = new();
    private readonly InventoryService _inventory;
    private readonly WeaponFittingService _fitting;
    private readonly ContainerId _backpack = new(1);
    private readonly ContainerId _ground = new(2);

    public WeaponFittingTests()
    {
        var items = new ItemCatalog(
        [
            new ItemDefinition(Pistol, Mass.FromKilograms(1), Volume.FromLiters(1), 1),
            new ItemDefinition(Suppressor, Mass.FromKilograms(0.5), Volume.FromLiters(0.5), 4),
            new ItemDefinition(Scope, Mass.FromKilograms(0.3), Volume.FromLiters(0.3), 4),
            new ItemDefinition(Rock, Mass.FromKilograms(2), Volume.FromLiters(1), 4),
        ]);
        _inventory = new InventoryService(items, _containers);
        _containers.TryAdd(new Container(_backpack, Mass.FromKilograms(2), Volume.FromLiters(10), items));
        _containers.TryAdd(new Container(_ground, Mass.FromKilograms(1000), Volume.FromLiters(1000), items));
        var weapons = new WeaponService(new WeaponCatalog(
            [new WeaponCategory("test:weapon_category/pistol", 10, ["muzzle", "optic"])],
            [new WeaponDefinition(Pistol, "test:weapon_category/pistol", 30, DamageType.Pierce, 2, 25, 90)],
            [
                new AttachmentDefinition(Suppressor, "muzzle", [new(WeaponService.Noise, ModifierOperation.Multiply, 0.2)]),
                new AttachmentDefinition(Scope, "optic", [new(WeaponService.Reach, ModifierOperation.Multiply, 1.5)]),
            ]));
        _fitting = new WeaponFittingService(_inventory, _containers, weapons);
    }

    private Container Container(ContainerId id)
    {
        Assert.True(_containers.TryGet(id, out var container));
        return container;
    }

    private StackId Add(ContainerId container, ItemId item, int count = 1)
    {
        Assert.True(_inventory.AddItems(container, item, count).IsSuccess);
        return Container(container).Stacks.Last(s => s.Item == item).Id;
    }

    private ItemStack Stack(ContainerId container, StackId id) => Container(container).Stacks.Single(s => s.Id == id);

    [Fact]
    public void Fit_TakesOneAttachmentFromItsContainer_AndFitsItToTheWeaponsMount()
    {
        var pistol = Add(_backpack, Pistol);
        var suppressors = Add(_ground, Suppressor, 2);

        var result = _fitting.Fit(_backpack, pistol, "muzzle", _ground, suppressors);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, Stack(_ground, suppressors).Count);
        Assert.Equal(Suppressor, _fitting.Weapons.FittedTo(Stack(_backpack, pistol).State, "muzzle"));
        Assert.Contains(new AttachmentFitted(Pistol, Suppressor, "muzzle"), result.Events);
        Assert.Equal(1.5, Container(_backpack).TotalMass.Kilograms, 6);
    }

    [Fact]
    public void Fit_FromTheSameContainer_DoesNotCountTheAttachmentTwice()
    {
        var pistol = Add(_backpack, Pistol);
        var suppressor = Add(_backpack, Suppressor);
        Add(_backpack, Suppressor);
        Assert.Equal(2, Container(_backpack).TotalMass.Kilograms, 6);

        Assert.True(_fitting.Fit(_backpack, pistol, "muzzle", _backpack, suppressor).IsSuccess);

        Assert.Equal(2, Container(_backpack).TotalMass.Kilograms, 6);
        Assert.Equal(1, Container(_backpack).CountOf(Suppressor, null));
    }

    [Fact]
    public void Fit_IsRefusedOnTheWrongMount_AndNothingChanges()
    {
        var pistol = Add(_backpack, Pistol);
        var suppressor = Add(_ground, Suppressor);

        var result = _fitting.Fit(_backpack, pistol, "optic", _ground, suppressor);

        Assert.Equal(WeaponError.WrongMount, result.WeaponError);
        Assert.Null(Stack(_backpack, pistol).State);
        Assert.Equal(1, Stack(_ground, suppressor).Count);
    }

    [Fact]
    public void Fit_IsRefusedOnAnOccupiedMount_OrForAnItemThatIsNoAttachment()
    {
        var pistol = Add(_backpack, Pistol);
        var suppressors = Add(_ground, Suppressor, 2);
        var rock = Add(_ground, Rock);
        Assert.True(_fitting.Fit(_backpack, pistol, "muzzle", _ground, suppressors).IsSuccess);

        Assert.Equal(WeaponError.MountOccupied, _fitting.Fit(_backpack, pistol, "muzzle", _ground, suppressors).WeaponError);
        Assert.Equal(WeaponError.UnknownAttachment, _fitting.Fit(_backpack, pistol, "muzzle", _ground, rock).WeaponError);
        Assert.Equal(1, Stack(_ground, suppressors).Count);
    }

    [Fact]
    public void Fit_IsRefusedWhenTheWeaponsContainerHasNoRoomForTheAttachment_AndNothingChanges()
    {
        var pistol = Add(_backpack, Pistol);
        var scope = Add(_backpack, Scope);
        var suppressor = Add(_ground, Suppressor);
        Assert.True(_fitting.Fit(_backpack, pistol, "optic", _backpack, scope).IsSuccess);
        Add(_backpack, Scope);
        Add(_backpack, Scope);

        // 1 kg pistol + 0.3 kg scope fitted + 0.6 kg of loose scopes; 0.5 kg more would pass the 2 kg limit.
        var result = _fitting.Fit(_backpack, pistol, "muzzle", _ground, suppressor);

        Assert.Equal(InventoryError.ExceedsMassLimit, result.InventoryError);
        Assert.Null(_fitting.Weapons.FittedTo(Stack(_backpack, pistol).State, "muzzle"));
        Assert.Equal(1, Stack(_ground, suppressor).Count);
    }

    [Fact]
    public void Remove_PutsTheAttachmentIntoAContainer_AndFreesTheMount()
    {
        var pistol = Add(_backpack, Pistol);
        var suppressor = Add(_ground, Suppressor);
        Assert.True(_fitting.Fit(_backpack, pistol, "muzzle", _ground, suppressor).IsSuccess);

        var result = _fitting.Remove(_backpack, pistol, "muzzle", _ground);

        Assert.True(result.IsSuccess);
        Assert.Null(Stack(_backpack, pistol).State);
        Assert.Equal(1, Container(_ground).CountOf(Suppressor, null));
        Assert.Contains(new AttachmentRemoved(Pistol, Suppressor, "muzzle"), result.Events);
        Assert.Equal(1, Container(_backpack).TotalMass.Kilograms, 6);
    }

    [Fact]
    public void Remove_IntoTheWeaponsOwnContainer_KeepsItsMass()
    {
        var pistol = Add(_backpack, Pistol);
        var suppressor = Add(_backpack, Suppressor);
        Assert.True(_fitting.Fit(_backpack, pistol, "muzzle", _backpack, suppressor).IsSuccess);

        Assert.True(_fitting.Remove(_backpack, pistol, "muzzle", _backpack).IsSuccess);

        Assert.Equal(1, Container(_backpack).CountOf(Suppressor, null));
        Assert.Equal(1.5, Container(_backpack).TotalMass.Kilograms, 6);
    }

    [Fact]
    public void Remove_IsRefusedForAFreeMount_OrAContainerWithNoRoom_AndNothingChanges()
    {
        var pistol = Add(_ground, Pistol);
        var suppressor = Add(_ground, Suppressor);
        Assert.True(_fitting.Fit(_ground, pistol, "muzzle", _ground, suppressor).IsSuccess);
        Add(_backpack, Rock);

        Assert.Equal(WeaponError.NotFitted, _fitting.Remove(_ground, pistol, "optic", _backpack).WeaponError);
        Assert.Equal(InventoryError.ExceedsMassLimit, _fitting.Remove(_ground, pistol, "muzzle", _backpack).InventoryError);
        Assert.Equal(Suppressor, _fitting.Weapons.FittedTo(Stack(_ground, pistol).State, "muzzle"));
        Assert.Equal(0, Container(_backpack).CountOf(Suppressor, null));
    }

    [Fact]
    public void FittedMounts_NameTheMountsThatHaveAnAttachment_ForTheRigToDraw()
    {
        var pistol = Add(_ground, Pistol);
        Assert.Empty(_fitting.Weapons.FittedMounts(Stack(_ground, pistol).State));

        Assert.True(_fitting.Fit(_ground, pistol, "optic", _ground, Add(_ground, Scope)).IsSuccess);
        Assert.True(_fitting.Fit(_ground, pistol, "muzzle", _ground, Add(_ground, Suppressor)).IsSuccess);

        Assert.Equal(["muzzle", "optic"], _fitting.Weapons.FittedMounts(Stack(_ground, pistol).State).Order());
        Assert.Equal(["muzzle", "optic"], _fitting.Weapons.Mounts(Pistol));
        Assert.Empty(_fitting.Weapons.Mounts(Rock));
    }
}
