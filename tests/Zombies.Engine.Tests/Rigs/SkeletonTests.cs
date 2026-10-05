using System.Numerics;
using Zombies.Domain.Items;
using Zombies.Engine.Animation;

namespace Zombies.Engine.Tests.Rigs;

public sealed class SkeletonTests
{
    [Fact]
    public void VoxelScale_IsEightPerMeter()
    {
        Assert.Equal(8, VoxelScale.PerMeter);
        Assert.Equal(1f, VoxelScale.ToMeters(8));
        Assert.Equal(0.125f, VoxelScale.VoxelMeters);
        Assert.Equal(4, VoxelScale.ToVoxels(0.5f));
        Assert.Equal(12, VoxelScale.ToVoxels(1.5f));
    }

    [Fact]
    public void VoxelBox_ConvertsItsVoxelsToMeters()
    {
        var box = new VoxelBox(-4, 0, -2, 8, 16, 4, 0, 0);

        RigTestData.Near(new Vector3(-0.5f, 0f, -0.25f), box.MinMeters);
        RigTestData.Near(new Vector3(1f, 2f, 0.5f), box.SizeMeters);
        Assert.Equal(512, box.VoxelCount);
    }

    [Fact]
    public void ASkeleton_LoadsFromJson_WithBonesBoxesAttachPointsAndSetup()
    {
        var skeleton = RigTestData.Mini();

        Assert.Equal("test:rig/mini", skeleton.Id);
        Assert.Equal(11, skeleton.Bones.Count);
        Assert.Equal(-1, skeleton.Bones[0].Parent);
        Assert.Equal(skeleton.IndexOf("torso"), skeleton.Bones[skeleton.IndexOf("head")].Parent);
        Assert.Equal(-1, skeleton.IndexOf("tail"));

        var head = skeleton.Bones[skeleton.IndexOf("head")];
        var box = Assert.Single(head.Boxes);
        Assert.Equal(64, box.VoxelCount);

        Assert.True(skeleton.TryGetAttachPoint("held_right", out var held));
        Assert.Equal(skeleton.IndexOf("hand_r"), held.Bone);
        Assert.False(skeleton.TryGetAttachPoint("held_back", out _));

        Assert.Equal(2f, skeleton.Walk.StrideMeters);
        Assert.Equal(4, skeleton.Walk.Swings.Count);
        Assert.Equal(2, skeleton.LookAt.Count);
        Assert.Equal(80f * MathF.PI / 180f, skeleton.LookAt[1].MaxYawRadians, 5);
    }

    [Fact]
    public void BoneOffsets_AreWrittenInVoxels_AndHeldInMeters()
    {
        var skeleton = RigTestData.Mini();

        RigTestData.Near(new Vector3(0f, 1f, 0f), skeleton.Bones[skeleton.IndexOf("torso")].Rest.Position);
        RigTestData.Near(new Vector3(1f, 1f, 0f), skeleton.Bones[skeleton.IndexOf("arm_r")].Rest.Position);
    }

    [Fact]
    public void ABoneWithoutAPart_TakesItFromTheNearestBoneAboveIt()
    {
        var skeleton = RigTestData.Mini();

        Assert.Null(skeleton.PartOf(skeleton.IndexOf("root")));
        Assert.Equal(BodyPart.Torso, skeleton.PartOf(skeleton.IndexOf("torso")));
        Assert.Equal(BodyPart.RightArm, skeleton.PartOf(skeleton.IndexOf("hand_r")));
        Assert.Equal(BodyPart.LeftLeg, skeleton.PartOf(skeleton.IndexOf("foot_l")));
    }

    [Fact]
    public void ARestPose_PutsEveryBoneWhereItsOffsetsSay()
    {
        var pose = new RigPose(RigTestData.Mini());

        RigTestData.Near(new Vector3(1f, 1f, 0f), pose.World("hand_r").Position);
        RigTestData.Near(new Vector3(0f, 2f, 0f), pose.World("head").Position);
        RigTestData.Near(new Vector3(-1f, 0f, 0f), pose.World("foot_l").Position);
    }

    [Fact]
    public void AMissingPart_HidesItsBones_AndTheOnesThatTakeTheirPartFromIt()
    {
        var skeleton = RigTestData.Mini();
        var animator = new Animator(skeleton, ClipSet.Empty);

        animator.Update(0f, RigTestData.Still(MissingPartSet.LeftLeg | MissingPartSet.RightArm));

        Assert.False(animator.Pose.IsVisible(skeleton.IndexOf("leg_l")));
        Assert.False(animator.Pose.IsVisible(skeleton.IndexOf("foot_l")));
        Assert.False(animator.Pose.IsVisible(skeleton.IndexOf("hand_r")));
        Assert.True(animator.Pose.IsVisible(skeleton.IndexOf("leg_r")));
        Assert.True(animator.Pose.IsVisible(skeleton.IndexOf("hand_l")));
        Assert.True(animator.Pose.IsVisible(skeleton.IndexOf("root")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("{ not json")]
    [InlineData("""{ "bones": [ { "name": "root" } ] }""")]
    [InlineData("""{ "id": "x", "bones": [] }""")]
    [InlineData("""{ "id": "x" }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a" }, { "name": "a" } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a", "parent": "b" }, { "name": "b" } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a", "parent": "nope" } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a", "part": "tail" } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a", "offset": [1, 2] } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a", "boxes": [ { "min": [0, 0, 0], "size": [0, 1, 1], "uv": [0, 0] } ] } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a", "boxes": [ { "min": [0, 0, 0], "size": [1, 1, 1], "uv": [-1, 0] } ] } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a", "boxes": [ { "min": [0, 0, 0], "size": [1, 1], "uv": [0, 0] } ] } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a" } ], "attachPoints": [ { "name": "p", "bone": "b" } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a" } ], "attachPoints": [ { "name": "p", "bone": "a" }, { "name": "p", "bone": "a" } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a" } ], "walk": { "swings": [ { "bone": "b" } ] } }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a" } ], "walk": { "strideLength": 0 } }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a" } ], "walk": { "swings": [ { "bone": "a", "phase": 2 } ] } }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a" } ], "lookAt": [ { "bone": "b" } ] }""")]
    [InlineData("""{ "id": "x", "bones": [ { "name": "a" } ], "lookAt": [ { "bone": "a", "yawWeight": 2 } ] }""")]
    public void ABadSkeleton_IsRefusedWithAReason_AndNeverThrows(string json)
    {
        Assert.False(Skeleton.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TheError_NamesTheBoneAndThePathToTheProblem()
    {
        Assert.False(Skeleton.TryParse("""{ "id": "x", "bones": [ { "name": "a" }, { "name": "b", "parent": "c" } ] }""", out _, out var error));

        Assert.Contains("bones[1]", error, StringComparison.Ordinal);
        Assert.Contains("'c'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPartSet_IsBuiltFromBodyParts_AndOnlyLegsMakeACharacterCrawl()
    {
        var flags = MissingParts.From([BodyPart.LeftArm, BodyPart.RightLeg]);

        Assert.Equal(MissingPartSet.LeftArm | MissingPartSet.RightLeg, flags);
        Assert.True(flags.Contains(BodyPart.RightLeg));
        Assert.False(flags.Contains(BodyPart.LeftLeg));
        Assert.True(flags.IsCrawling());
        Assert.False(MissingPartSet.None.IsCrawling());
        Assert.False((MissingPartSet.LeftArm | MissingPartSet.RightArm | MissingPartSet.Head).IsCrawling());
        Assert.True(MissingPartSet.LeftLeg.IsCrawling());
    }

    [Fact]
    public void EveryBodyPart_HasItsOwnFlag()
    {
        var seen = MissingPartSet.None;
        foreach (var part in Enum.GetValues<BodyPart>())
        {
            var flag = MissingParts.From(part);
            Assert.Equal(MissingPartSet.None, seen & flag);
            seen |= flag;
        }
    }
}
