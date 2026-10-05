using System.Numerics;
using Zombies.Engine.Animation;
using Zombies.Engine.Net;

namespace Zombies.Engine.Tests.Rigs;

public sealed class AnimatorTests
{
    private const string Locomotion = """
        {
          "clips": [
            { "name": "idle", "duration": 2, "loop": true,
              "tracks": [ { "bone": "torso", "keys": [ { "offset": [0, 8, 0] } ] } ] },
            { "name": "idle_crawl", "duration": 2, "loop": true,
              "tracks": [ { "bone": "torso", "keys": [ { "offset": [0, -4, 0] } ] } ] },
            { "name": "walk", "duration": 1, "loop": true,
              "tracks": [ { "bone": "torso", "keys": [ { "time": 0 }, { "time": 0.5, "offset": [8, 0, 0] } ] } ] },
            { "name": "walk_crawl", "duration": 1, "loop": true,
              "tracks": [ { "bone": "torso", "keys": [ { "offset": [0, -8, 0] } ] } ] },
            { "name": "fire", "duration": 0.2, "loop": false,
              "tracks": [ { "bone": "torso", "keys": [ { "time": 0 }, { "time": 0.1, "offset": [8, 0, 0] }, { "time": 0.2 } ] } ] }
          ]
        }
        """;

    private static Animator Mini(ClipSet? clips = null) => new(RigTestData.Mini(), clips ?? ClipSet.Empty);

    /// <summary>How far forward (-Z) a bone that hangs straight down at rest ends up pointing, from -1 to 1.</summary>
    private static float Swing(Animator animator, string bone) =>
        -Vector3.Transform(-Vector3.UnitY, animator.Pose.Local(animator.Skeleton.IndexOf(bone)).Rotation).Z;

    [Fact]
    public void TheWalkPhase_AdvancesWithDistanceWalked_NotWithTime()
    {
        var animator = Mini();

        animator.Update(0.125f, RigTestData.Moving(4f));
        Assert.Equal(0.25f, animator.WalkPhase, 4);

        animator.Update(0.25f, RigTestData.Moving(2f));
        Assert.Equal(0.5f, animator.WalkPhase, 4);

        animator.Update(1f, RigTestData.Moving(2f));
        Assert.Equal(0.5f, animator.WalkPhase, 4);
    }

    [Fact]
    public void StandingStill_LeavesThePhaseAlone_AndEveryBoneAtRest()
    {
        var skeleton = RigTestData.Mini();
        var animator = new Animator(skeleton, ClipSet.Empty);
        animator.Update(0.5f, RigTestData.Moving(4f));
        var phase = animator.WalkPhase;

        animator.Update(2f, RigTestData.Still());

        Assert.Equal(phase, animator.WalkPhase);
        foreach (var bone in skeleton.Bones)
        {
            RigTestData.IsRest(animator.Pose, bone.Name, skeleton);
        }
    }

    [Fact]
    public void TheProceduralWalk_SwingsOppositeLegsOppositeWays_AndArmsAgainstTheirLegs()
    {
        var animator = Mini();

        animator.Update(0.125f, RigTestData.Moving(4f));

        Assert.Equal(MathF.Sin(40f * MathF.PI / 180f), Swing(animator, "leg_l"), 3);
        Assert.Equal(-MathF.Sin(40f * MathF.PI / 180f), Swing(animator, "leg_r"), 3);
        Assert.True(Swing(animator, "arm_r") > 0.4f, "The right arm swings forward with the left leg.");
        Assert.True(Swing(animator, "arm_l") < -0.4f, "The left arm swings back against the left leg.");
    }

    [Fact]
    public void TheSwing_IsScaledBySpeed_SoAHalfSpeedWalkSwingsHalfTheAngle()
    {
        var full = Mini();
        var half = Mini();

        full.Update(0.125f, RigTestData.Moving(4f));
        half.Update(0.25f, RigTestData.Moving(2f));

        Assert.Equal(0.25f, half.WalkPhase, 4);
        Assert.Equal(MathF.Sin(20f * MathF.PI / 180f), Swing(half, "leg_l"), 3);
        Assert.True(Swing(full, "leg_l") > Swing(half, "leg_l"));
    }

    [Fact]
    public void TheWalk_SwingsTheFeetTooBecauseTheyAreChildrenOfTheLegs()
    {
        var animator = Mini();

        animator.Update(0.125f, RigTestData.Moving(4f));

        // The leg swings forward, so its foot, which hangs one meter below it, ends up in front of where it was.
        Assert.True(animator.Pose.World("foot_l").Position.Z < -0.5f);
        Assert.True(animator.Pose.World("foot_r").Position.Z > 0.5f);
    }

    [Fact]
    public void Walking_IsTheSameEveryTime_ForTheSameInputs()
    {
        var a = Mini(RigTestData.Clips(Locomotion));
        var b = Mini(RigTestData.Clips(Locomotion));

        for (var i = 0; i < 90; i++)
        {
            var input = new AnimationInput(1f + (i % 5), 0.1f * (i % 7), 0.05f * (i % 3), MissingPartSet.None);
            a.Update(1f / 30f, input);
            b.Update(1f / 30f, input);
        }

        for (var bone = 0; bone < a.Pose.BoneCount; bone++)
        {
            Assert.Equal(a.Pose.World(bone), b.Pose.World(bone));
        }
    }

    [Fact]
    public void ACrawlingCharacter_DoesNotSwingItsLegs_ButStillSwingsItsArms()
    {
        var animator = Mini();

        animator.Update(0.125f, RigTestData.Moving(4f, MissingPartSet.LeftLeg));

        Assert.True(animator.IsCrawling);
        Assert.Equal(0f, Swing(animator, "leg_r"), 4);
        Assert.True(Swing(animator, "arm_r") > 0.4f);
    }

    [Fact]
    public void ASwingOnAMissingPart_IsLeftOut()
    {
        var skeleton = RigTestData.Mini();
        var animator = new Animator(skeleton, ClipSet.Empty);

        animator.Update(0.125f, RigTestData.Moving(4f, MissingPartSet.RightArm));

        RigTestData.IsRest(animator.Pose, "arm_r", skeleton);
        Assert.False(animator.Pose.IsVisible(skeleton.IndexOf("arm_r")));
    }

    [Fact]
    public void TheLookAt_TurnsTheHeadAndTorsoBetweenThemToFaceTheLook()
    {
        var animator = Mini();

        animator.Update(0f, RigTestData.Looking(MathF.PI / 2f, 0f));

        // Positive yaw faces right, which is +X.
        RigTestData.Near(Vector3.UnitX, animator.Pose.World("head").TransformDirection(-Vector3.UnitZ));
        var torso = animator.Pose.World("torso").TransformDirection(-Vector3.UnitZ);
        Assert.True(torso.X > 0.3f && torso.X < 0.5f, "The torso takes a quarter of the turn.");
    }

    [Fact]
    public void ALookDirection_HasToBeWithinWhatEachBoneMayTurn()
    {
        var animator = Mini();

        // Half a turn: the torso takes 45 degrees, and the head would take 135 but may only turn 80.
        animator.Update(0f, RigTestData.Looking(MathF.PI, 0f));

        var expected = 125f * MathF.PI / 180f;
        RigTestData.Near(
            new Vector3(MathF.Sin(expected), 0f, -MathF.Cos(expected)),
            animator.Pose.World("head").TransformDirection(-Vector3.UnitZ));
    }

    [Fact]
    public void LookingUp_TiltsTheHeadUp()
    {
        var animator = Mini();

        animator.Update(0f, RigTestData.Looking(0f, 0.5f));

        RigTestData.Near(
            new Vector3(0f, MathF.Sin(0.5f), -MathF.Cos(0.5f)),
            animator.Pose.World("head").TransformDirection(-Vector3.UnitZ));
    }

    [Fact]
    public void ALookThatIsNotANumber_IsIgnored_RatherThanBreakingThePose()
    {
        var animator = Mini();

        animator.Update(0f, RigTestData.Looking(float.NaN, float.PositiveInfinity));

        Assert.False(float.IsNaN(animator.Pose.World("head").Position.X));
        RigTestData.Near(-Vector3.UnitZ, animator.Pose.World("head").TransformDirection(-Vector3.UnitZ));
    }

    [Fact]
    public void ALookRelativeToTheBody_GoesTheShorterWayRound()
    {
        Assert.Equal(-0.2832f, AnimationInput.RelativeYaw(3f, -3f), 3);
        Assert.Equal(0.2832f, AnimationInput.RelativeYaw(-3f, 3f), 3);
        Assert.Equal(0.5f, AnimationInput.RelativeYaw(1.5f, 1f), 5);
    }

    [Fact]
    public void TheIdleClip_PlaysWhenStill_AndTheWalkClipWhenMoving()
    {
        var animator = Mini(RigTestData.Clips(Locomotion));

        animator.Update(0.1f, RigTestData.Still());
        Assert.Equal("idle", animator.LocomotionClip);
        RigTestData.Near(new Vector3(0f, 2f, 0f), animator.Pose.World("torso").Position);

        animator.Update(0.125f, RigTestData.Moving(4f));
        Assert.Equal("walk", animator.LocomotionClip);
    }

    [Fact]
    public void TheWalkClip_IsDrivenByThePhase_SoItAdvancesWithDistance()
    {
        var animator = Mini(RigTestData.Clips(Locomotion));

        // A quarter of a stride into a one-second clip with a key at half a second is halfway to that key.
        animator.Update(0.125f, RigTestData.Moving(4f));

        Assert.Equal(0.5f, animator.Pose.Local(animator.Skeleton.IndexOf("torso")).Position.X, 3);
    }

    [Fact]
    public void ACharacterMissingALeg_PlaysTheCrawlVariantOfWhateverItIsDoing()
    {
        var animator = Mini(RigTestData.Clips(Locomotion));

        animator.Update(0.1f, RigTestData.Still(MissingPartSet.RightLeg));
        Assert.Equal("idle_crawl", animator.LocomotionClip);
        Assert.Equal(1f - 0.5f, animator.Pose.Local(animator.Skeleton.IndexOf("torso")).Position.Y, 3);

        animator.Update(0.1f, RigTestData.Moving(2f, MissingPartSet.RightLeg));
        Assert.Equal("walk_crawl", animator.LocomotionClip);
        Assert.Equal(0f, animator.Pose.Local(animator.Skeleton.IndexOf("torso")).Position.Y, 3);
    }

    [Fact]
    public void ACharacterMissingAnArm_StillPlaysTheOrdinaryClips()
    {
        var animator = Mini(RigTestData.Clips(Locomotion));

        animator.Update(0.1f, RigTestData.Still(MissingPartSet.LeftArm | MissingPartSet.Head));

        Assert.Equal("idle", animator.LocomotionClip);
        Assert.False(animator.IsCrawling);
    }

    [Fact]
    public void TheClip_FollowsTheMissingPartFlagsEveryFrame()
    {
        var animator = Mini(RigTestData.Clips(Locomotion));

        animator.Update(0.1f, RigTestData.Still(MissingPartSet.LeftLeg));
        animator.Update(0.1f, RigTestData.Still());

        Assert.Equal("idle", animator.LocomotionClip);
        Assert.False(animator.IsCrawling);
    }

    [Fact]
    public void WithNoClipsAtAll_TheAnimatorStillWorks_AndNamesNoLocomotionClip()
    {
        var animator = Mini();

        animator.Update(0.1f, RigTestData.Moving(3f));

        Assert.Null(animator.LocomotionClip);
    }

    [Fact]
    public void AnAction_PlaysFromItsStart_RunsOverTheLocomotionClip_AndEndsByItself()
    {
        var animator = Mini(RigTestData.Clips(Locomotion));
        var torso = animator.Skeleton.IndexOf("torso");

        Assert.True(animator.PlayAction("fire"));
        animator.Update(0.1f, RigTestData.Still());
        Assert.Equal("fire", animator.ActionClip);
        Assert.Equal(0f, animator.Pose.Local(torso).Position.X, 3);

        animator.Update(0.1f, RigTestData.Still());
        Assert.Equal(1f, animator.Pose.Local(torso).Position.X, 3);
        Assert.Equal("fire", animator.ActionClip);

        animator.Update(0.1f, RigTestData.Still());
        Assert.Null(animator.ActionClip);
        Assert.Equal(0f, animator.Pose.Local(torso).Position.X, 3);
        Assert.Equal(2f, animator.Pose.Local(torso).Position.Y, 3);
    }

    [Fact]
    public void AnUnknownAction_IsRefused_AndAStoppedActionLeavesThePose()
    {
        var animator = Mini(RigTestData.Clips(Locomotion));

        Assert.False(animator.PlayAction("dance"));
        Assert.Null(animator.ActionClip);

        Assert.True(animator.PlayAction("fire"));
        animator.StopAction();
        Assert.Null(animator.ActionClip);
    }

    [Fact]
    public void TimeCannotRunBackwards()
    {
        var animator = Mini();

        Assert.Throws<ArgumentOutOfRangeException>(() => animator.Update(-0.1f, RigTestData.Still()));
        Assert.Throws<ArgumentOutOfRangeException>(() => animator.Update(float.NaN, RigTestData.Still()));
    }

    [Fact]
    public void ANonFiniteSpeed_CountsAsStandingStill()
    {
        var animator = Mini();

        animator.Update(0.1f, RigTestData.Moving(float.NaN));
        animator.Update(0.1f, RigTestData.Moving(-5f));

        Assert.Equal(0f, animator.WalkPhase);
    }

    [Fact]
    public void ThePose_IsOneObjectFilledInPlace_SoAnimatingAllocatesNothing()
    {
        var animator = Mini();
        var pose = animator.Pose;

        animator.Update(0.1f, RigTestData.Moving(3f));

        Assert.Same(pose, animator.Pose);
    }

    [Fact]
    public void APlayersMoveState_BecomesTheAnimationInput_IgnoringFalling()
    {
        var state = new PlayerMoveState(Vector3.Zero, new Vector3(3f, -9f, 4f), 1f, 0.2f, false, false);

        var input = AnimationInput.ForPlayer(state.Velocity, 0.5f, state.Yaw, state.Pitch, MissingPartSet.LeftArm);

        Assert.Equal(5f, input.Speed, 4);
        Assert.Equal(0.5f, input.LookYaw, 4);
        Assert.Equal(0.2f, input.LookPitch, 4);
        Assert.Equal(MissingPartSet.LeftArm, input.Missing);
    }

    [Fact]
    public void APlayerWalkingUnderTheMovementModel_SwingsItsLegs()
    {
        var state = PlayerMoveState.At(Vector3.Zero);
        var move = new PlayerInput(1f, 0f, 0f, 0f, false, false, false, false, false);
        var animator = Mini();

        for (var i = 0; i < 30; i++)
        {
            state = PlayerMovement.Step(state, move, PlayerMovement.StepSeconds, FlatFloorCollision.Instance);
            animator.Update(PlayerMovement.StepSeconds, AnimationInput.ForPlayer(state.Velocity, state.Yaw, state.Yaw, state.Pitch, MissingPartSet.None));
        }

        Assert.True(animator.WalkPhase > 0f);
        Assert.NotEqual(Swing(animator, "leg_l"), Swing(animator, "leg_r"));
    }
}
