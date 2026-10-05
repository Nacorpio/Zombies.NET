using System.Numerics;
using Zombies.Engine.Animation;

namespace Zombies.Engine.Tests.Rigs;

public sealed class WeaponRigTests
{
    private const string Pistol = """
        {
          "id": "test:weapon/pistol",
          "grip": { "offset": [0, 0, 8] },
          "mounts": {
            "muzzle": { "offset": [0, 0, -8] },
            "optic": { "offset": [0, 8, 0], "rotation": [0, 90, 0] }
          }
        }
        """;

    private static WeaponRig PistolRig()
    {
        Assert.True(WeaponRig.TryParse(Pistol, out var rig, out var error), error);
        return rig;
    }

    [Fact]
    public void AWeaponRig_LoadsFromJson_WithItsGripAndMountsInMeters()
    {
        var rig = PistolRig();

        Assert.Equal("test:weapon/pistol", rig.Id);
        RigTestData.Near(new Vector3(0f, 0f, 1f), rig.Grip.Position);
        Assert.Equal(["muzzle", "optic"], rig.Mounts.Keys.Order());
        RigTestData.Near(new Vector3(0f, 0f, -1f), rig.Mounts["muzzle"].Position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "grip": {} }""")]
    [InlineData("""{ "id": "x", "grip": 3 }""")]
    [InlineData("""{ "id": "x", "grip": { "offset": [1] } }""")]
    [InlineData("""{ "id": "x", "mounts": [] }""")]
    [InlineData("""{ "id": "x", "mounts": { "muzzle": 1 } }""")]
    [InlineData("""{ "id": "x", "mounts": { "muzzle": { "rotation": "up" } } }""")]
    public void ABadWeaponRig_IsRefusedWithAReason(string json)
    {
        Assert.False(WeaponRig.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AHeldWeapon_IsPlacedSoItsGripSitsOnTheHoldPoint_AndMountsFollowIt()
    {
        var animator = new Animator(RigTestData.Mini(), ClipSet.Empty);
        animator.Update(0f, RigTestData.Still());

        Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, PistolRig(), ["muzzle"], out var placement));

        // The right hand rests at (1, 1, 0). The grip is a meter behind the weapon's origin, so the origin is a meter in front.
        RigTestData.Near(new Vector3(1f, 1f, -1f), placement.Weapon.Position);
        var muzzle = Assert.Single(placement.Attachments);
        Assert.Equal("muzzle", muzzle.Mount);
        RigTestData.Near(new Vector3(1f, 1f, -2f), muzzle.World.Position);
        Assert.Empty(placement.UnknownMounts);
    }

    [Fact]
    public void TheGripStaysOnTheHand_NoMatterHowTheCharacterMoves()
    {
        var animator = new Animator(RigTestData.Mini(), ClipSet.Empty);
        var rig = PistolRig();

        for (var i = 0; i < 20; i++)
        {
            animator.Update(1f / 30f, new AnimationInput(4f, 0.4f, 0.3f, MissingPartSet.None));
            Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, rig, [], out var placement));
            Assert.True(animator.Pose.TryGetAttachPoint(HeldWeapon.RightHand, out var hold));

            RigTestData.Near(hold.Position, placement.Weapon.TransformPoint(rig.Grip.Position));
        }
    }

    [Fact]
    public void AnAttachment_IsDrawnAtItsMount_RotatedWithTheMount()
    {
        var animator = new Animator(RigTestData.Mini(), ClipSet.Empty);
        animator.Update(0f, RigTestData.Still());

        Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.LeftHand, PistolRig(), ["optic"], out var placement));

        var optic = Assert.Single(placement.Attachments);
        RigTestData.Near(new Vector3(-1f, 2f, -1f), optic.World.Position);
        // The mount is turned a quarter about Y, which takes forward toward -X.
        RigTestData.Near(-Vector3.UnitX, optic.World.TransformDirection(-Vector3.UnitZ));
    }

    [Fact]
    public void AMountTheWeaponDoesNotHave_IsReportedAndNotDrawn()
    {
        var animator = new Animator(RigTestData.Mini(), ClipSet.Empty);

        Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, PistolRig(), ["muzzle", "laser"], out var placement));

        Assert.Equal(["muzzle"], placement.Attachments.Select(a => a.Mount));
        Assert.Equal(["laser"], placement.UnknownMounts);
    }

    [Fact]
    public void AHandThatIsAMissingPart_HoldsNothing()
    {
        var animator = new Animator(RigTestData.Mini(), ClipSet.Empty);
        animator.Update(0f, RigTestData.Still(MissingPartSet.RightArm));

        Assert.False(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, PistolRig(), [], out _));
        Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.LeftHand, PistolRig(), [], out _));
    }

    [Fact]
    public void ASkeletonWithoutTheHoldPoint_HoldsNothing()
    {
        var skeleton = RigTestData.Parse("""{ "id": "x", "bones": [ { "name": "root" } ] }""");
        var animator = new Animator(skeleton, ClipSet.Empty);

        Assert.False(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, PistolRig(), [], out _));
    }

    [Fact]
    public void TheSameWeaponRig_FitsAnySkeleton()
    {
        var rig = PistolRig();
        foreach (var skeleton in new[] { RigTestData.Mini(), RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseSkeleton("viewmodel") })
        {
            var pose = new Animator(skeleton, ClipSet.Empty).Pose;

            Assert.True(HeldWeapon.TryPlace(pose, HeldWeapon.RightHand, rig, ["muzzle", "optic"], out var placement), skeleton.Id);
            Assert.Equal(2, placement.Attachments.Count);
        }
    }
}
