using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Engine.Animation;
using Zombies.Engine.Core.Modding;

namespace Zombies.Engine.Tests.Rigs;

/// <summary>
/// The Attachments fitted to a weapon's Item state are the ones placed on its rig, on the first-person viewmodel and on the
/// humanoid skeleton that players and zombies share. These are the placements a renderer draws; nothing here draws.
/// </summary>
public sealed class VisibleAttachmentTests
{
    private static readonly ItemId Pistol = new("base:item/pistol_9mm");
    private static readonly ItemId Suppressor = new("base:item/suppressor");
    private static readonly ItemId Laser = new("base:item/laser_sight");

    private static WeaponService BaseWeapons()
    {
        var registry = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RigTestData.RepoRoot(), "mods"))).Registry;
        return new WeaponService(new WeaponCatalog(
            registry.OfKind("weapon_category").Select(d => WeaponDefinitionJson.ParseCategory(d.Json)),
            registry.OfKind("weapon").Select(d => WeaponDefinitionJson.ParseWeapon(d.Json)),
            registry.OfKind("attachment").Select(d => WeaponDefinitionJson.ParseAttachment(d.Json))));
    }

    private static WeaponRig PistolRig()
    {
        Assert.True(WeaponRig.TryParse(RigTestData.BaseRig("pistol_9mm.weapon.json"), out var rig, out var error), error);
        return rig;
    }

    private static ItemState Fitted(WeaponService weapons, params ItemId[] attachments)
    {
        ItemState? state = null;
        foreach (var attachment in attachments)
        {
            var result = weapons.Attach(Pistol, state, attachment);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            state = result.State;
        }

        return state!;
    }

    [Theory]
    [InlineData("viewmodel")]
    [InlineData("humanoid")]
    public void FittedAttachments_ArePlacedOnTheHeldWeapon_InFirstPersonAndInTheWorld(string skeleton)
    {
        var weapons = BaseWeapons();
        var state = Fitted(weapons, Suppressor, Laser);
        var animator = new Animator(RigTestData.BaseSkeleton(skeleton), RigTestData.BaseClips(skeleton));
        animator.Update(0.1f, RigTestData.Still());

        Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, PistolRig(), weapons.FittedMounts(state), out var placement));

        Assert.Equal(["laser", "muzzle"], placement.Attachments.Select(a => a.Mount).Order());
        Assert.Empty(placement.UnknownMounts);
    }

    [Fact]
    public void TakingAnAttachmentOff_StopsItBeingPlaced()
    {
        var weapons = BaseWeapons();
        var fitted = Fitted(weapons, Suppressor, Laser);
        var detached = weapons.Detach(Pistol, fitted, Suppressor).State;
        var animator = new Animator(RigTestData.BaseSkeleton("viewmodel"), RigTestData.BaseClips("viewmodel"));
        animator.Update(0.1f, RigTestData.Still());

        Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, PistolRig(), weapons.FittedMounts(detached), out var placement));

        Assert.Equal("laser", Assert.Single(placement.Attachments).Mount);
    }
}
