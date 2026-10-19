using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
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
    public void MovementScore_SlowsTheTargetSpeed_AndNoModifiersLeavesItAlone()
    {
        var movement = LimbScoreJson.Parse("""
            { "id": "test:limb_score/movement", "stat": "move_speed", "floor": 0.2,
              "parts": [ { "part": "leftLeg", "required": true }, { "part": "rightLeg", "required": true } ] }
            """);
        var limp = LimbScores.ToModifiers(LimbScores.Compute([movement], new Body(new BodyId(1), missingAtSpawn: [BodyPart.LeftLeg]), []), new ModifierSource("limb_scores"));
        var whole = LimbScores.ToModifiers(LimbScores.Compute([movement], new Body(new BodyId(1)), []), new ModifierSource("limb_scores"));

        var slowed = PlayerMovement.Step(PlayerMoveState.At(Vector3.Zero), Input(forward: 1f), 1f, FlatFloorCollision.Instance, limp);
        var full = PlayerMovement.Step(PlayerMoveState.At(Vector3.Zero), Input(forward: 1f), 1f, FlatFloorCollision.Instance, whole);

        Assert.Equal(PlayerMovement.WalkSpeed * 0.2f, new Vector2(slowed.Velocity.X, slowed.Velocity.Z).Length(), 3);
        Assert.Equal(PlayerMovement.WalkSpeed, new Vector2(full.Velocity.X, full.Velocity.Z).Length(), 3);
        Assert.Equal(PlayerMovement.SprintSpeed, PlayerMovement.TargetSpeed(Input(sprint: true)));
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
[Collection("Jolt")]
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

        // Drop onto the first column near the middle with no trunk standing on it; canopies are not solid, so leaves are fine.
        var origin = generator.Generate(new ChunkCoord(0, 0));
        var dropX = 8;
        var dropZ = 8;
        for (var column = 0; column < 64 && HasLog(origin, dropX, dropZ); column++)
        {
            dropX = 4 + (column % 8);
            dropZ = 4 + (column / 8);
        }

        var start = new Vector3(dropX + 0.5f, 80f, dropZ + 0.5f);
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

        // The ground, not a tree: the player stands on the terrain under a canopy, and a heightfield slopes between its column
        // corners, so the surface may be as high as the highest neighbouring column.
        var surface = GroundTop(chunk, x, z);
        var highest = Math.Max(Math.Max(surface, GroundTop(chunk, Math.Min(x + 1, 15), z)), Math.Max(GroundTop(chunk, x, Math.Min(z + 1, 15)), GroundTop(chunk, Math.Min(x + 1, 15), Math.Min(z + 1, 15))));

        Assert.True(state.OnGround, "the player should have landed on the generated terrain.");
        Assert.True(state.Position.Y > surface - 1f && state.Position.Y < highest + 1f, $"the player should rest on the ground at {surface} to {highest}, but is at {state.Position} (dropped at {dropX},{dropZ}).");

        // Standing still must not creep: the character keeps its contacts between steps.
        var landed = state.Position;
        for (var i = 0; i < 300; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.Equal(landed, state.Position);
    }

    [Theory]
    [InlineData(15.5f)]
    [InlineData(15.9f)]
    [InlineData(31.5f)]
    [InlineData(-0.5f)]
    public void APlayer_StandingOverAChunkSeam_DoesNotFallThrough(float x)
    {
        var biomes = new BiomeCatalog([new Biome("base:biome/temperate_forest", ["forest"], (0, 100), (0, 100), 52, 14, 50)]);
        var generator = new WorldGenerator(12345, biomes);
        using var world = new PhysicsWorld();
        for (var cz = -1; cz <= 1; cz++)
        {
            for (var cx = -2; cx <= 2; cx++)
            {
                world.AddTerrain(generator.Generate(new ChunkCoord(cx, cz)));
            }
        }

        var start = new Vector3(x, 90f, 8.5f);
        var collision = world.Create(start, PlayerMovement.StandingHeight);
        var state = PlayerMoveState.At(start);
        for (var i = 0; i < 600; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.True(state.OnGround, $"the player over x={x} should have landed.");
        Assert.True(state.Position.Y > 40f, $"the player over x={x} fell through the seam to {state.Position.Y}.");
    }

    /// <summary>A tree as the generator grows one: a log trunk with a canopy of leaves over its top.</summary>
    private static void GrowTree(Chunk chunk, int treeX, int treeZ, int groundY, int trunkHeight)
    {
        var top = groundY + trunkHeight;
        for (var y = top - 2; y <= top + 1; y++)
        {
            var radius = y >= top ? 1 : 2;
            for (var ox = -radius; ox <= radius; ox++)
            {
                for (var oz = -radius; oz <= radius; oz++)
                {
                    if (!(Math.Abs(ox) == 2 && Math.Abs(oz) == 2))
                    {
                        chunk.Set(treeX + ox, y, treeZ + oz, Blocks.Leaves);
                    }
                }
            }
        }

        for (var y = groundY + 1; y <= top; y++)
        {
            chunk.Set(treeX, y, treeZ, Blocks.Log);
        }
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0f, -1f)]
    public void APlayer_WalkingIntoATree_StaysOnTheGround(float directionX, float directionZ)
    {
        using var world = new PhysicsWorld();
        var chunk = FlatChunk(new ChunkCoord(0, 0), groundY: 64);
        GrowTree(chunk, 8, 8, groundY: 64, trunkHeight: 5);
        chunk.RecomputeHeights();
        world.AddTerrain(chunk);

        // Start on the ground a few blocks out, outside the canopy, and walk straight at the trunk.
        var start = new Vector3(8.5f - (directionX * 5f), 65f, 8.5f - (directionZ * 5f));
        var collision = world.Create(start, PlayerMovement.StandingHeight);
        var state = PlayerMoveState.At(start);
        var yaw = MathF.Atan2(directionX, -directionZ);
        var input = new PlayerInput(1f, 0f, yaw, 0f, false, false, false, false, false);
        var lowest = state.Position.Y;
        for (var i = 0; i < 300; i++)
        {
            state = PlayerMovement.Step(state, input, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
            lowest = MathF.Min(lowest, state.Position.Y);
        }

        Assert.True(lowest > 63f, $"the player fell to {lowest} walking into a tree.");
        Assert.True(state.Position.Y < 67f, $"the player ended at {state.Position} walking into a tree (lowest {lowest}).");
    }

    private static bool HasLog(Chunk chunk, int x, int z)
    {
        for (var y = 0; y < ChunkConstants.Height; y++)
        {
            if (chunk.Get(x, y, z) == Blocks.Log)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Y of the top block of a column that is neither a log nor leaves, so the ground under a tree.</summary>
    private static int GroundTop(Chunk chunk, int x, int z)
    {
        for (var y = ChunkConstants.Height - 1; y >= 0; y--)
        {
            var block = chunk.Get(x, y, z);
            if (block != Blocks.Air && block != Blocks.Log && block != Blocks.Leaves)
            {
                return y + 1;
            }
        }

        return 0;
    }

    [Fact]
    public void APlayer_RunningAndJumpingAmongGeneratedTrees_NeverFallsThrough()
    {
        var biomes = new BiomeCatalog([new Biome("base:biome/temperate_forest", ["forest"], (0, 100), (0, 100), 52, 14, 200)]);
        var generator = new WorldGenerator(777, biomes);
        using var world = new PhysicsWorld();
        var chunks = new Dictionary<ChunkCoord, Chunk>();
        for (var cz = -3; cz <= 3; cz++)
        {
            for (var cx = -3; cx <= 3; cx++)
            {
                var chunk = generator.Generate(new ChunkCoord(cx, cz));
                chunks[chunk.Coord] = chunk;
                world.AddTerrain(chunk);
            }
        }

        var failures = new List<string>();
        var random = new Random(5);
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var wx = random.Next(-30, 30);
            var wz = random.Next(-30, 30);
            var chunk = chunks[new ChunkCoord(wx >> 4, wz >> 4)];
            var groundY = chunk.TopOpaque(wx & 15, wz & 15) + 1;
            var start = new Vector3(wx + 0.5f, groundY + 3f, wz + 0.5f);
            var collision = world.Create(start, PlayerMovement.StandingHeight);
            var state = PlayerMoveState.At(start);
            var yaw = (float)(random.NextDouble() * Math.PI * 2);
            for (var i = 0; i < 1200; i++)
            {
                if (i % 40 == 0)
                {
                    yaw = (float)(random.NextDouble() * Math.PI * 2);
                }

                var input = new PlayerInput(1f, random.Next(3) - 1f, yaw, 0f, true, random.Next(6) == 0, false, false, i % 25 == 0);
                state = PlayerMovement.Step(state, input, PlayerMovement.StepSeconds, collision);
                world.Step(PlayerMovement.StepSeconds);
                var px = (int)MathF.Floor(state.Position.X);
                var pz = (int)MathF.Floor(state.Position.Z);
                if (chunks.TryGetValue(new ChunkCoord(px >> 4, pz >> 4), out var under) && state.Position.Y < GroundTop(under, px & 15, pz & 15) - 2f)
                {
                    failures.Add($"attempt {attempt} fell to {state.Position} at tick {i} (ground {GroundTop(under, px & 15, pz & 15)}, start {start}).");
                    break;
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData(1.5f, 0f)]
    [InlineData(0f, 1.5f)]
    [InlineData(-1.5f, 1f)]
    [InlineData(0.6f, 0.6f)]
    public void APlayer_UnderACanopy_IsNotLeftFallingOrStuckBelowIt(float offsetX, float offsetZ)
    {
        using var world = new PhysicsWorld();
        var chunk = FlatChunk(new ChunkCoord(0, 0), groundY: 64);
        GrowTree(chunk, 8, 8, groundY: 64, trunkHeight: 5);
        chunk.RecomputeHeights();
        world.AddTerrain(chunk);

        var start = new Vector3(8.5f + offsetX, 65f, 8.5f + offsetZ);
        var collision = world.Create(start, PlayerMovement.StandingHeight);
        var state = PlayerMoveState.At(start);
        var lowest = state.Position.Y;
        for (var i = 0; i < 300; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
            lowest = MathF.Min(lowest, state.Position.Y);
        }

        Assert.True(lowest > 63f, $"the player fell to {lowest} under a canopy.");
        Assert.True(state.OnGround, $"the player is not standing on anything at {state.Position}.");
    }

    [Fact]
    public void APlayer_PushedUnderTheTerrain_IsStoodBackOnTopOfIt()
    {
        using var world = new PhysicsWorld();
        world.AddTerrain(FlatChunk(new ChunkCoord(0, 0), groundY: 64));

        // Start well under the surface, as a reconcile teleport beside a steep step could leave a player.
        var start = new Vector3(8f, 40f, 8f);
        var collision = world.Create(start, PlayerMovement.StandingHeight);
        var state = PlayerMoveState.At(start);
        for (var i = 0; i < 120; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        Assert.True(state.OnGround);
        Assert.True(state.Position.Y > 63f, $"the player should be back on the surface, but is at {state.Position.Y}.");
    }
}
