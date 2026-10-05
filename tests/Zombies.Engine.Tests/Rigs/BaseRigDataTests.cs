using System.Numerics;
using System.Text.Json;
using Zombies.Domain.Items;
using Zombies.Engine.Animation;

namespace Zombies.Engine.Tests.Rigs;

/// <summary>The rigs the base mod ships load through the mod source and drive the one animation system.</summary>
public sealed class BaseRigDataTests
{
    [Fact]
    public void TheBaseMod_ShipsItsRigFilesThroughTheModSource()
    {
        foreach (var file in new[] { "humanoid.skeleton.json", "humanoid.clips.json", "viewmodel.skeleton.json", "viewmodel.clips.json", "pistol_9mm.weapon.json" })
        {
            Assert.False(string.IsNullOrWhiteSpace(RigTestData.BaseRig(file)), file);
        }
    }

    [Fact]
    public void TheHumanoid_HasABoneForEveryBodyPart()
    {
        var skeleton = RigTestData.BaseSkeleton("humanoid");

        var covered = Enumerable.Range(0, skeleton.Bones.Count).Select(skeleton.PartOf).OfType<BodyPart>().ToHashSet();

        Assert.Equal([.. Enum.GetValues<BodyPart>()], covered.Order());
    }

    [Fact]
    public void TheHumanoid_StandsAboutAsTallAsAPlayer_BuiltFromEighthMeterVoxels()
    {
        var skeleton = RigTestData.BaseSkeleton("humanoid");
        var pose = new RigPose(skeleton);

        var top = 0f;
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            foreach (var box in skeleton.Bones[i].Boxes)
            {
                top = MathF.Max(top, pose.World(i).TransformPoint(box.MinMeters + box.SizeMeters).Y);
            }
        }

        Assert.InRange(top, 1.7f, 2.0f);
        Assert.True(skeleton.VoxelCount > 0);
        Assert.Equal(VoxelScale.PerMeter * top, MathF.Round(VoxelScale.PerMeter * top), 3);
    }

    [Fact]
    public void TheBaseClips_OnlyNameBonesTheirSkeletonHas()
    {
        foreach (var name in new[] { "humanoid", "viewmodel" })
        {
            // The animator refuses a clip for a bone the skeleton lacks, so building it is the check.
            _ = new Animator(RigTestData.BaseSkeleton(name), RigTestData.BaseClips(name));
        }
    }

    [Fact]
    public void ALegglessHumanoid_PlaysTheBaseCrawlClips()
    {
        var animator = new Animator(RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));

        animator.Update(0.1f, RigTestData.Still(MissingPartSet.LeftLeg));
        Assert.Equal("idle_crawl", animator.LocomotionClip);

        animator.Update(0.1f, RigTestData.Moving(1f, MissingPartSet.LeftLeg | MissingPartSet.RightLeg));
        Assert.Equal("walk_crawl", animator.LocomotionClip);

        animator.Update(0.1f, RigTestData.Still());
        Assert.Equal("idle", animator.LocomotionClip);
    }

    [Fact]
    public void ACrawlingHumanoid_HasItsTorsoLowerThanAStandingOne()
    {
        var standing = new Animator(RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
        var crawling = new Animator(RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));

        standing.Update(0.1f, RigTestData.Still());
        crawling.Update(0.1f, RigTestData.Still(MissingPartSet.RightLeg));

        Assert.True(crawling.Pose.World("head").Position.Y < standing.Pose.World("head").Position.Y - 0.3f);
    }

    [Fact]
    public void ThePlayersHumanoid_WalksAndLooksAtWhatThePlayerLooksAt()
    {
        var animator = new Animator(RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
        var skeleton = animator.Skeleton;

        animator.Update(0.2f, new AnimationInput(4.3f, 0.6f, 0.2f, MissingPartSet.None));

        var leftLeg = animator.Pose.Local(skeleton.IndexOf("leg_l")).Rotation;
        var rightLeg = animator.Pose.Local(skeleton.IndexOf("leg_r")).Rotation;
        Assert.NotEqual(leftLeg, rightLeg);
        var facing = animator.Pose.World("head").TransformDirection(-Vector3.UnitZ);
        Assert.True(facing.X > 0.3f, "Looking right turns the head toward +X.");
        Assert.True(facing.Y > 0.05f, "Looking up tilts the head up.");
    }

    [Fact]
    public void TheViewmodel_IsAnOrdinarySkeletonAnimatedByTheSameAnimator()
    {
        var animator = new Animator(RigTestData.BaseSkeleton("viewmodel"), RigTestData.BaseClips("viewmodel"));
        var skeleton = animator.Skeleton;

        animator.Update(0.1f, RigTestData.Still());
        Assert.Equal("idle", animator.LocomotionClip);
        var standing = animator.Pose.World("hand_r").Position;

        // Walking bobs the arms with the same procedural walk the body uses.
        animator.Update(0.1f, RigTestData.Moving(4.3f));
        Assert.NotEqual(standing, animator.Pose.World("hand_r").Position);
        Assert.Empty(skeleton.LookAt);
        Assert.True(animator.Pose.TryGetAttachPoint(HeldWeapon.RightHand, out _));
    }

    [Fact]
    public void TheViewmodel_PlaysFireAndReloadOverItself_AndEachEndsByItself()
    {
        var animator = new Animator(RigTestData.BaseSkeleton("viewmodel"), RigTestData.BaseClips("viewmodel"));

        Assert.True(animator.PlayAction("fire"));
        animator.Update(0.05f, RigTestData.Still());
        animator.Update(0.05f, RigTestData.Still());
        Assert.Equal("fire", animator.ActionClip);
        var recoil = animator.Pose.World("hand_r").Position;

        animator.Update(0.5f, RigTestData.Still());
        animator.Update(0.1f, RigTestData.Still());
        Assert.Null(animator.ActionClip);
        Assert.NotEqual(recoil, animator.Pose.World("hand_r").Position);

        Assert.True(animator.PlayAction("reload"));
        for (var i = 0; i < 20; i++)
        {
            animator.Update(0.1f, RigTestData.Still());
        }

        Assert.Null(animator.ActionClip);
    }

    [Fact]
    public void AHeldWeapon_FollowsTheViewmodelsHand_AsItBobs()
    {
        var animator = new Animator(RigTestData.BaseSkeleton("viewmodel"), RigTestData.BaseClips("viewmodel"));
        Assert.True(WeaponRig.TryParse(RigTestData.BaseRig("pistol_9mm.weapon.json"), out var pistol, out var error), error);

        animator.Update(0.1f, RigTestData.Still());
        Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, pistol, ["muzzle"], out var still));
        animator.Update(0.1f, RigTestData.Moving(4.3f));
        Assert.True(HeldWeapon.TryPlace(animator.Pose, HeldWeapon.RightHand, pistol, ["muzzle"], out var walking));

        Assert.NotEqual(still.Weapon.Position, walking.Weapon.Position);
        Assert.NotEqual(still.Attachments[0].World.Position, walking.Attachments[0].World.Position);
    }

    [Fact]
    public void ThePistolRig_HasAMountForEveryMountOfThePistolCategory()
    {
        Assert.True(WeaponRig.TryParse(RigTestData.BaseRig("pistol_9mm.weapon.json"), out var pistol, out var error), error);
        var categoryPath = Path.Combine(RigTestData.RepoRoot(), "mods", "base", "data", "weapon_category", "pistol.json");
        using var category = JsonDocument.Parse(File.ReadAllText(categoryPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var mounts = category.RootElement.GetProperty("mounts").EnumerateArray().Select(m => m.GetString()!).Order().ToList();

        Assert.Equal(mounts, pistol.Mounts.Keys.Order());
    }
}
