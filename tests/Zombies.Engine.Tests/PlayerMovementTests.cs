using System.Numerics;
using Zombies.Domain.World;
using Zombies.Engine.Net;
using Zombies.Engine.Physics;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>Checks the shared movement model: the same inputs always give the same position, and the stance and lean behave.</summary>
public sealed class PlayerMovementTests
{
    private static PlayerInput Input(float forward = 0f, float strafe = 0f, float yaw = 0f, bool sprint = false, bool crouch = false, bool jump = false) =>
        new(forward, strafe, yaw, 0f, sprint, crouch, false, false, jump);

    private static PlayerMoveState Run(PlayerMoveState state, PlayerInput input, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            state = PlayerMovement.Step(state, input, PlayerMovement.StepSeconds, FlatFloorCollision.Instance);
        }

        return state;
    }

    [Fact]
    public void WalkingForward_ReachesWalkSpeed_AndStaysOnTheFloor()
    {
        var state = Run(PlayerMoveState.At(Vector3.Zero), Input(forward: 1f), 60);

        Assert.True(state.OnGround);
        Assert.Equal(0f, state.Position.Y, 3);
        Assert.Equal(PlayerMovement.WalkSpeed, new Vector2(state.Velocity.X, state.Velocity.Z).Length(), 2);
        Assert.True(state.Position.Z < -1f, "yaw 0 looks north, which is -Z.");
    }

    [Fact]
    public void Sprinting_IsFasterThanWalking_AndCrouchingIsSlower()
    {
        var walk = Run(PlayerMoveState.At(Vector3.Zero), Input(forward: 1f), 60);
        var sprint = Run(PlayerMoveState.At(Vector3.Zero), Input(forward: 1f, sprint: true), 60);
        var crouch = Run(PlayerMoveState.At(Vector3.Zero), Input(forward: 1f, crouch: true), 60);

        Assert.Equal(PlayerMovement.SprintSpeed, new Vector2(sprint.Velocity.X, sprint.Velocity.Z).Length(), 2);
        Assert.Equal(PlayerMovement.CrouchSpeed, new Vector2(crouch.Velocity.X, crouch.Velocity.Z).Length(), 2);
        Assert.True(sprint.Position.Z < walk.Position.Z);
        Assert.True(crouch.Position.Z > walk.Position.Z);
        Assert.True(crouch.Crouched);
    }

    [Fact]
    public void Jumping_LeavesTheGround_AndComesBackDown()
    {
        var state = PlayerMoveState.At(Vector3.Zero) with { OnGround = true };
        state = PlayerMovement.Step(state, Input(jump: true), PlayerMovement.StepSeconds, FlatFloorCollision.Instance);

        Assert.False(state.OnGround);
        Assert.True(state.Velocity.Y > 0f);

        state = Run(state, Input(), 120);
        Assert.True(state.OnGround);
        Assert.Equal(0f, state.Position.Y, 3);
    }

    [Fact]
    public void Crouching_IsSlowerAndLower_ButDoesNotJump()
    {
        var state = PlayerMoveState.At(Vector3.Zero) with { OnGround = true };
        state = PlayerMovement.Step(state, Input(crouch: true, jump: true), PlayerMovement.StepSeconds, FlatFloorCollision.Instance);

        Assert.True(state.OnGround);
        Assert.Equal(0f, state.Velocity.Y, 3);
        Assert.Equal(PlayerMovement.CrouchEyeHeight, PlayerMovement.EyeHeightFor(state.Crouched), 3);
    }

    [Fact]
    public void Leaning_MovesTheCameraSideways_AndReturnsToCentre()
    {
        var lean = 0f;
        for (var i = 0; i < 30; i++)
        {
            lean = PlayerMovement.StepLean(lean, new PlayerInput(0, 0, 0, 0, false, false, false, true, false), PlayerMovement.StepSeconds);
        }

        Assert.Equal(PlayerMovement.LeanOffset, lean, 3);

        for (var i = 0; i < 30; i++)
        {
            lean = PlayerMovement.StepLean(lean, Input(), PlayerMovement.StepSeconds);
        }

        Assert.Equal(0f, lean, 3);
    }

    [Fact]
    public void TheSameInputs_AlwaysGiveTheSamePosition()
    {
        var first = Run(PlayerMoveState.At(new Vector3(3, 0, 7)), Input(forward: 1f, strafe: 0.5f, yaw: 0.7f, sprint: true), 90);
        var second = Run(PlayerMoveState.At(new Vector3(3, 0, 7)), Input(forward: 1f, strafe: 0.5f, yaw: 0.7f, sprint: true), 90);

        Assert.Equal(first.Position, second.Position);
        Assert.Equal(first.Velocity, second.Velocity);
    }

    [Fact]
    public void InputCommand_RoundTripsThroughTheWire()
    {
        var input = new PlayerInput(1f, -0.333f, 1.25f, -0.5f, true, false, true, false, true);
        var writer = new NetWriter();
        new PlayerInputCommand(input).Write(writer);
        var reader = new NetReader(writer.Written);
        var read = PlayerInputCommand.Read(ref reader);
        reader.EnsureEnd();

        Assert.Equal(1f, read.Input.Forward, 3);
        Assert.Equal(-0.333f, read.Input.Strafe, 3);
        Assert.Equal(1.25f, read.Input.Yaw, 3);
        Assert.Equal(-0.5f, read.Input.Pitch, 3);
        Assert.True(read.Input.Sprint);
        Assert.False(read.Input.Crouch);
        Assert.True(read.Input.LeanLeft);
        Assert.False(read.Input.LeanRight);
        Assert.True(read.Input.Jump);
    }
}

/// <summary>Checks that Jolt drives the character against real terrain, including a one-block ledge.</summary>
public sealed class PhysicsTests
{
    private static Chunk FlatChunk(ChunkCoord coord, int groundY)
    {
        var chunk = new Chunk(coord);
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                chunk.Set(x, 0, z, Blocks.Bedrock);
                for (var y = 1; y <= groundY; y++)
                {
                    chunk.Set(x, y, z, y == groundY ? Blocks.Grass : Blocks.Dirt);
                }
            }
        }

        chunk.RecomputeHeights();
        return chunk;
    }

    private static Chunk LedgeChunk(ChunkCoord coord, int lowY, int highY, int stepAtX)
    {
        var chunk = new Chunk(coord);
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var groundY = x >= stepAtX ? highY : lowY;
                chunk.Set(x, 0, z, Blocks.Bedrock);
                for (var y = 1; y <= groundY; y++)
                {
                    chunk.Set(x, y, z, y == groundY ? Blocks.Grass : Blocks.Dirt);
                }
            }
        }

        chunk.RecomputeHeights();
        return chunk;
    }

    [Fact]
    public void ACharacter_FallsOntoTheTerrain_AndStandsOnIt()
    {
        using var world = new PhysicsWorld();
        world.AddTerrain(FlatChunk(new ChunkCoord(0, 0), groundY: 64));
        var collision = world.Create(new Vector3(8.5f, 70f, 8.5f), PlayerMovement.StandingHeight);

        var state = PlayerMoveState.At(new Vector3(8.5f, 70f, 8.5f));
        for (var i = 0; i < 120; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.True(state.OnGround);
        Assert.Equal(65f, state.Position.Y, 1);
    }

    [Fact]
    public void WalkingIntoAOneBlockLedge_StepsUp()
    {
        using var world = new PhysicsWorld();
        world.AddTerrain(LedgeChunk(new ChunkCoord(0, 0), lowY: 64, highY: 65, stepAtX: 8));
        var start = new Vector3(4.5f, 65f, 8.5f);
        var collision = world.Create(start, PlayerMovement.StandingHeight);

        var state = PlayerMoveState.At(start);
        var input = new PlayerInput(0f, 1f, 0f, 0f, false, false, false, false, false);
        for (var i = 0; i < 60; i++)
        {
            state = PlayerMovement.Step(state, input, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.True(state.Position.X > 9f, $"the player should have walked past the ledge, but is at x {state.Position.X}.");
        Assert.Equal(66f, state.Position.Y, 1);
    }

    [Fact]
    public void WalkingIntoAWall_Stops()
    {
        using var world = new PhysicsWorld();
        var chunk = FlatChunk(new ChunkCoord(0, 0), groundY: 64);
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var y = 65; y <= 68; y++)
            {
                chunk.Set(10, y, z, Blocks.Stone);
            }
        }

        chunk.RecomputeHeights();
        world.AddTerrain(chunk);
        var start = new Vector3(4.5f, 65f, 8.5f);
        var collision = world.Create(start, PlayerMovement.StandingHeight);

        var state = PlayerMoveState.At(start);
        var input = new PlayerInput(0f, 1f, 0f, 0f, false, false, false, false, false);
        for (var i = 0; i < 60; i++)
        {
            state = PlayerMovement.Step(state, input, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.True(state.Position.X < 10f, $"the player should have stopped at the wall, but is at x {state.Position.X}.");
        Assert.Equal(65f, state.Position.Y, 1);
    }

    [Fact]
    public void RemovingTerrain_LetsThePlayerFall()
    {
        using var world = new PhysicsWorld();
        world.AddTerrain(FlatChunk(new ChunkCoord(0, 0), groundY: 64));
        var start = new Vector3(8.5f, 65f, 8.5f);
        var collision = world.Create(start, PlayerMovement.StandingHeight);

        var state = PlayerMoveState.At(start);
        for (var i = 0; i < 30; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.True(state.OnGround);
        Assert.True(world.RemoveTerrain(new ChunkCoord(0, 0)));

        for (var i = 0; i < 120; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.False(state.OnGround);
        Assert.True(state.Position.Y < 60f);
    }

    [Fact]
    public void APlayer_DroppedIntoGeneratedTerrain_LandsOnTheSurface_AndStaysPut()
    {
        var biomes = new BiomeCatalog([new Biome("base:biome/temperate_forest", ["forest"], (0, 100), (0, 100), 52, 14, 50)]);
        var generator = new WorldGenerator(12345, biomes);
        using var world = new PhysicsWorld();

        // The chunks around the spawn, as the client streams them.
        for (var cz = -1; cz <= 1; cz++)
        {
            for (var cx = -1; cx <= 1; cx++)
            {
                world.AddTerrain(generator.Generate(new ChunkCoord(cx, cz)));
            }
        }

        var start = new Vector3(8f, 80f, 8f);
        var collision = world.Create(start, PlayerMovement.StandingHeight);
        var state = PlayerMoveState.At(start);
        for (var i = 0; i < 600; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        var chunk = generator.Generate(new ChunkCoord(0, 0));
        var x = (int)MathF.Floor(state.Position.X);
        var z = (int)MathF.Floor(state.Position.Z);
        var surface = chunk.TopOpaque(x, z) + 1;

        Assert.True(state.OnGround, "the player should have landed on the generated terrain.");
        Assert.True(Math.Abs(state.Position.Y - surface) < 1f, $"the player should rest on the surface at {surface}, but is at {state.Position.Y}.");

        // Standing still must not creep: the character keeps its contacts between steps.
        var landed = state.Position;
        for (var i = 0; i < 300; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.Equal(landed, state.Position);
    }
}
