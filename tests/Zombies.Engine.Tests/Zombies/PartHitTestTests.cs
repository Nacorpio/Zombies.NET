using System.Numerics;
using Zombies.Domain.Items;
using Zombies.Engine.Animation;
using Zombies.Engine.Tests.Rigs;

namespace Zombies.Engine.Tests;

/// <summary>
/// The humanoid's boxes in meters, from its voxel data at 8 per meter: the head is 0.5 wide and spans 1.375 to 1.875 high, the torso
/// 0.75 wide from 0.625 to 1.375, the left arm sits at x -0.5 and the right at 0.5, and each leg is 0.25 wide and 0.625 high.
/// The skeleton faces -Z, so a ray from the front starts at negative Z and travels +Z.
/// </summary>
public sealed class PartHitTestTests
{
    private static readonly Skeleton Humanoid = RigTestData.BaseSkeleton("humanoid");

    private static bool Cast(Vector3 origin, Vector3 direction, out PartHit hit, MissingPartSet missing = MissingPartSet.None, float yaw = 0f, Vector3? scale = null, Vector3? position = null, float max = 50f)
    {
        var animator = new Animator(Humanoid, ClipSet.Empty);
        animator.Update(0f, new AnimationInput(0f, 0f, 0f, missing));
        return PartHitTest.TryRaycast(animator.Pose, position ?? Vector3.Zero, yaw, scale ?? Vector3.One, origin, direction, max, out hit);
    }

    [Theory]
    [InlineData(1.6f, BodyPart.Head)]
    [InlineData(1.0f, BodyPart.Torso)]
    public void ARayFromTheFront_HitsTheHeadAndTorsoAtTheirHeights(float height, BodyPart expected)
    {
        Assert.True(Cast(new Vector3(0, height, -5), Vector3.UnitZ, out var hit));

        Assert.Equal(expected, hit.Part);
        Assert.InRange(hit.Distance, 4.7f, 4.9f);
        Assert.InRange(hit.Point.Z, -0.3f, -0.1f);
    }

    [Theory]
    [InlineData(-0.5f, 1.0f, BodyPart.LeftArm)]
    [InlineData(0.5f, 1.0f, BodyPart.RightArm)]
    [InlineData(-0.25f, 0.3f, BodyPart.LeftLeg)]
    [InlineData(0.25f, 0.3f, BodyPart.RightLeg)]
    public void ARayFromTheFront_HitsEachLimb(float x, float y, BodyPart expected)
    {
        Assert.True(Cast(new Vector3(x, y, -5), Vector3.UnitZ, out var hit));

        Assert.Equal(expected, hit.Part);
    }

    [Fact]
    public void ARayThatMissesEveryBox_HitsNothing()
    {
        Assert.False(Cast(new Vector3(2, 1, -5), Vector3.UnitZ, out _));
        Assert.False(Cast(new Vector3(0, 3, -5), Vector3.UnitZ, out _));
        Assert.False(Cast(new Vector3(0, 1, -5), -Vector3.UnitZ, out _));
    }

    [Fact]
    public void TheNearestPartWins_WhenARayCrossesSeveral()
    {
        // From the side at arm height the right arm is hit before the torso, and from the other side the left arm is.
        Assert.True(Cast(new Vector3(5, 1.0f, 0), -Vector3.UnitX, out var fromRight));
        Assert.True(Cast(new Vector3(-5, 1.0f, 0), Vector3.UnitX, out var fromLeft));

        Assert.Equal(BodyPart.RightArm, fromRight.Part);
        Assert.Equal(BodyPart.LeftArm, fromLeft.Part);
    }

    [Fact]
    public void TheRayStopsAtItsReach()
    {
        Assert.False(Cast(new Vector3(0, 1, -5), Vector3.UnitZ, out _, max: 4f));
        Assert.True(Cast(new Vector3(0, 1, -5), Vector3.UnitZ, out _, max: 5f));
    }

    [Fact]
    public void ADirectionNeedNotBeAUnitVector()
    {
        Assert.True(Cast(new Vector3(0, 1, -5), new Vector3(0, 0, 40), out var hit));

        Assert.InRange(hit.Distance, 4.7f, 4.9f);
    }

    [Fact]
    public void AMissingPartCannotBeHit()
    {
        Assert.True(Cast(new Vector3(-0.5f, 1.0f, -5), Vector3.UnitZ, out _));

        Assert.False(Cast(new Vector3(-0.5f, 1.0f, -5), Vector3.UnitZ, out _, missing: MissingPartSet.LeftArm));
        Assert.False(Cast(new Vector3(0, 1.6f, -5), Vector3.UnitZ, out _, missing: MissingPartSet.Head));
    }

    [Fact]
    public void TheBodyIsTurnedByItsYaw()
    {
        // At a yaw of a quarter turn the body faces +X, so its head is hit from +X and its left arm is on the -Z side.
        var quarter = MathF.PI / 2f;

        Assert.True(Cast(new Vector3(5, 1.6f, 0), -Vector3.UnitX, out var head, yaw: quarter));
        Assert.True(Cast(new Vector3(5, 1.0f, -0.5f), -Vector3.UnitX, out var arm, yaw: quarter));

        Assert.Equal(BodyPart.Head, head.Part);
        Assert.InRange(head.Distance, 4.7f, 4.9f);
        Assert.Equal(BodyPart.LeftArm, arm.Part);
    }

    [Fact]
    public void TheBodyStandsWhereItIsPlaced()
    {
        Assert.True(Cast(new Vector3(10, 81.6f, 3), Vector3.UnitZ, out var hit, position: new Vector3(10, 80, 8)));

        Assert.Equal(BodyPart.Head, hit.Part);
        Assert.InRange(hit.Distance, 4.7f, 4.9f);
    }

    [Fact]
    public void TheScaleStretchesTheBoxes_WithoutChangingDistancesInTheWorld()
    {
        // 1.5 times taller puts the head between 2.06 and 2.81 m; the ray at 2.5 m misses an ordinary body.
        Assert.False(Cast(new Vector3(0, 2.5f, -5), Vector3.UnitZ, out _));
        Assert.True(Cast(new Vector3(0, 2.5f, -5), Vector3.UnitZ, out var tall, scale: new Vector3(1, 1.5f, 1)));
        Assert.Equal(BodyPart.Head, tall.Part);
        Assert.InRange(tall.Distance, 4.7f, 4.9f);

        // Twice as wide puts the torso's front at 0.5 m instead of 0.25 m.
        Assert.True(Cast(new Vector3(0, 1.0f, -5), Vector3.UnitZ, out var wide, scale: new Vector3(2, 1, 2)));
        Assert.InRange(wide.Distance, 4.4f, 4.6f);
    }

    [Fact]
    public void NonsenseInputIsAMissNotAnException()
    {
        Assert.False(Cast(new Vector3(float.NaN, 1, -5), Vector3.UnitZ, out _));
        Assert.False(Cast(new Vector3(0, 1, -5), Vector3.Zero, out _));
        Assert.False(Cast(new Vector3(0, 1, -5), Vector3.UnitZ, out _, yaw: float.PositiveInfinity));
        Assert.False(Cast(new Vector3(0, 1, -5), Vector3.UnitZ, out _, scale: new Vector3(0, 1, 1)));
        Assert.False(Cast(new Vector3(0, 1, -5), Vector3.UnitZ, out _, max: float.NaN));
    }

    [Fact]
    public void TheHitNamesTheBoneItLanded()
    {
        var pose = new RigPose(Humanoid);
        Assert.True(PartHitTest.TryRaycast(pose, Vector3.Zero, 0f, Vector3.One, new Vector3(0, 1.6f, -5), Vector3.UnitZ, 50f, out var rest));

        Assert.Equal(BodyPart.Head, rest.Part);
        Assert.Equal(Humanoid.IndexOf("head"), rest.Bone);
    }
}
