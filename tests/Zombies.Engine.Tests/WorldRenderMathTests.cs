using System.Numerics;
using Zombies.Engine.Render;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

public sealed class WorldRenderMathTests
{
    private static Vector3 ToNdc(Matrix4x4 viewProjection, Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), viewProjection);
        return new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
    }

    private static Camera TestCamera() => new() { Position = new Vector3(10, 70, 10), Yaw = 0.6f, Pitch = -0.3f, Aspect = 16f / 9f, Far = 300 };

    [Fact]
    public void Camera_LooksNorthAtYawZeroAndTurnsRightForPositiveYaw()
    {
        var camera = new Camera();

        AssertNear(new Vector3(0, 0, -1), camera.Forward);

        camera.Yaw = MathF.PI / 2;

        AssertNear(new Vector3(1, 0, 0), camera.Forward);
        AssertNear(new Vector3(0, 0, 1), camera.Right);
    }

    [Fact]
    public void Camera_MouseLookTurnsRightAndLooksUpWhenTheMouseMovesUp()
    {
        var camera = new Camera();

        camera.Look(100, -100);

        Assert.True(camera.Yaw > 0);
        Assert.True(camera.Pitch > 0);
    }

    [Fact]
    public void Camera_PitchStopsJustShortOfStraightUpOrDown()
    {
        var camera = new Camera { Pitch = 10 };
        Assert.InRange(camera.Pitch, 1.5f, MathF.PI / 2);

        camera.Pitch = -10;
        Assert.InRange(camera.Pitch, -MathF.PI / 2, -1.5f);
        Assert.False(float.IsNaN(camera.Right.X));
    }

    [Fact]
    public void Camera_FlyMovesAlongTheFlatLookDirectionEvenWhenLookingUp()
    {
        var camera = new Camera { Pitch = 1.0f };

        camera.Fly(right: 0, up: 0, forward: 5);

        Assert.Equal(0f, camera.Position.Y, 4);
        Assert.Equal(-5f, camera.Position.Z, 3);
    }

    [Fact]
    public void Camera_ProjectionFollowsVulkanConventions()
    {
        var camera = new Camera { Position = Vector3.Zero, Yaw = 0, Pitch = 0, Near = 1, Far = 100 };

        var ahead = ToNdc(camera.ViewProjection, new Vector3(0, 0, -10));
        var above = ToNdc(camera.ViewProjection, new Vector3(0, 2, -10));
        var atNear = ToNdc(camera.ViewProjection, new Vector3(0, 0, -1));
        var atFar = ToNdc(camera.ViewProjection, new Vector3(0, 0, -100));

        Assert.Equal(0f, ahead.X, 4);
        Assert.Equal(0f, ahead.Y, 4);
        Assert.True(above.Y < 0, "world up must be negative clip Y in Vulkan");
        Assert.Equal(0f, atNear.Z, 3);
        Assert.Equal(1f, atFar.Z, 3);
    }

    [Fact]
    public void Frustum_KeepsWhatIsInFrontAndRejectsWhatIsBehindOrFarOff()
    {
        var camera = new Camera { Position = Vector3.Zero, Yaw = 0, Pitch = 0, Far = 200 };
        var frustum = new Frustum(camera.ViewProjection);

        Assert.True(frustum.Intersects(new Vector3(-1, -1, -21), new Vector3(1, 1, -19)));
        Assert.False(frustum.Intersects(new Vector3(-1, -1, 19), new Vector3(1, 1, 21)));
        Assert.False(frustum.Intersects(new Vector3(500, -1, -21), new Vector3(502, 1, -19)));
        Assert.False(frustum.Intersects(new Vector3(-1, -1, -401), new Vector3(1, 1, -399)));
        Assert.True(frustum.Intersects(new Vector3(-50, -50, -50), new Vector3(50, 50, 50)));
    }

    [Fact]
    public void Frustum_WorksForOrthographicCascadeMatricesToo()
    {
        var camera = TestCamera();
        var cascade = CascadeShadows.Compute(camera, SunModel.At(0.4f).DirectionToSun, 1, 100, 1024)[0];
        var frustum = new Frustum(cascade.ViewProjection);

        Assert.True(frustum.Intersects(camera.Position + (camera.Forward * 20) - Vector3.One, camera.Position + (camera.Forward * 20) + Vector3.One));
        Assert.False(frustum.Intersects(camera.Position + new Vector3(5000, 0, 0), camera.Position + new Vector3(5002, 2, 2)));
    }

    [Fact]
    public void Sun_IsHighAtNoonAndBelowTheHorizonAtMidnight()
    {
        var noon = SunModel.At(0.5f);
        var midnight = SunModel.At(0f);

        Assert.True(noon.DirectionToSun.Y > 0.9f);
        Assert.True(noon.CastsShadows);
        Assert.InRange(noon.SunIntensity, 0.95f, 1f);
        Assert.True(midnight.DirectionToSun.Y < -0.9f);
        Assert.False(midnight.CastsShadows);
        Assert.Equal(0f, midnight.SunIntensity, 3);
    }

    [Fact]
    public void Sun_RisesInTheEastAndSetsInTheWest()
    {
        Assert.True(SunModel.At(0.30f).DirectionToSun.X > 0.5f);
        Assert.True(SunModel.At(0.70f).DirectionToSun.X < -0.5f);
    }

    [Fact]
    public void Sun_ColorsChangeWithTheTimeOfDay()
    {
        var noon = SunModel.At(0.5f);
        var sunset = SunModel.At(0.72f);
        var night = SunModel.At(0f);

        Assert.True(noon.SunColor.Y > sunset.SunColor.Y, "the low sun is redder");
        Assert.True(noon.AmbientColor.Length() > night.AmbientColor.Length() * 3);
        Assert.True(noon.SkyColor.Length() > night.SkyColor.Length() * 5);
    }

    [Fact]
    public void Sun_ChangesSmoothlyAllDayAndStaysInRange()
    {
        var previous = SunModel.At(0f);
        for (var step = 1; step <= 2000; step++)
        {
            var state = SunModel.At(step / 2000f);
            foreach (var (a, b) in new[] { (previous.SkyColor, state.SkyColor), (previous.FogColor, state.FogColor), (previous.AmbientColor, state.AmbientColor), (previous.SunColor, state.SunColor) })
            {
                Assert.True(Vector3.Distance(a, b) < 0.02f, $"colour jumped at step {step}");
                Assert.True(b.X is >= 0 and <= 1.0001f && b.Y is >= 0 and <= 1.0001f && b.Z is >= 0 and <= 1.0001f);
            }

            Assert.InRange(state.SunIntensity, 0f, 1f);
            previous = state;
        }

        AssertNear(SunModel.At(0.3f).DirectionToSun, SunModel.At(1.3f).DirectionToSun);
    }

    [Fact]
    public void DayClock_AdvancesWrapsAndCanPause()
    {
        var clock = new DayClock(startFraction: 0.9f, secondsPerDay: 100);

        clock.Advance(20);

        Assert.Equal(0.1f, clock.Fraction, 4);

        clock.Paused = true;
        clock.Advance(50);

        Assert.Equal(0.1f, clock.Fraction, 4);

        clock.Set(1.25f);

        Assert.Equal(0.25f, clock.Fraction, 4);
    }

    [Fact]
    public void Cascades_SplitsGrowAndEndAtTheShadowDistance()
    {
        var splits = CascadeShadows.Splits(0.1f, 150, 3);

        Assert.Equal(3, splits.Length);
        Assert.True(splits[0] > 0.1f && splits[0] < splits[1] && splits[1] < splits[2]);
        Assert.Equal(150f, splits[2]);
        Assert.Equal([150f], CascadeShadows.Splits(0.1f, 150, 1));
        Assert.True(splits[0] < 150f / 3, "the first slice should be smaller than an even split");
        Assert.Throws<ArgumentOutOfRangeException>(() => CascadeShadows.Splits(0.1f, 0.05f, 3));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Cascades_EachMatrixContainsItsWholeSliceOfTheView(int count)
    {
        var camera = TestCamera();
        var sun = SunModel.At(0.4f).DirectionToSun;

        var cascades = CascadeShadows.Compute(camera, sun, count, 150, 2048);

        Assert.Equal(count, cascades.Length);
        foreach (var cascade in cascades)
        {
            foreach (var corner in CascadeShadows.SliceCorners(camera, cascade.SplitNear, cascade.SplitFar))
            {
                var ndc = ToNdc(cascade.ViewProjection, corner);
                Assert.InRange(ndc.X, -1.001f, 1.001f);
                Assert.InRange(ndc.Y, -1.001f, 1.001f);
                Assert.InRange(ndc.Z, -0.001f, 1.001f);
            }
        }
    }

    [Fact]
    public void Cascades_CoverTerrainBetweenTheSliceAndTheSun()
    {
        var camera = TestCamera();
        var sun = SunModel.At(0.4f).DirectionToSun;
        var cascade = CascadeShadows.Compute(camera, sun, 1, 100, 2048)[0];

        var inFrontOfView = camera.Position + (camera.Forward * 50);
        var towardSun = inFrontOfView + (sun * 120);
        var ndc = ToNdc(cascade.ViewProjection, towardSun);

        Assert.InRange(ndc.Z, 0f, 1f);
    }

    [Fact]
    public void Cascades_AreStableAsTheCameraTurnsAndRejectTinyResolutions()
    {
        var sun = SunModel.At(0.4f).DirectionToSun;
        var a = TestCamera();
        var b = TestCamera();
        b.Yaw += 0.5f;

        var first = CascadeShadows.Compute(a, sun, 1, 100, 2048)[0].ViewProjection;
        var second = CascadeShadows.Compute(b, sun, 1, 100, 2048)[0].ViewProjection;

        // A turn changes which part of the world is covered, but the scale (the size of the bound) stays the same.
        Assert.Equal(first.M11, second.M11, 4);
        Assert.Equal(first.M22, second.M22, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => CascadeShadows.Compute(a, sun, 2, 100, 8));
    }

    [Fact]
    public void RangeAllocator_HandsOutRangesAndReusesFreedOnes()
    {
        var allocator = new RangeAllocator(100);

        var a = allocator.Allocate(30);
        var b = allocator.Allocate(30);

        Assert.Equal(0, a);
        Assert.Equal(30, b);
        Assert.Equal(60, allocator.UsedCount);
        Assert.Equal(2, allocator.AllocationCount);

        allocator.Free(a, 30);

        Assert.Equal(0, allocator.Allocate(20));
        Assert.Equal(50, allocator.UsedCount);
    }

    [Fact]
    public void RangeAllocator_MergesNeighboursSoSpaceIsNotLost()
    {
        var allocator = new RangeAllocator(90);
        var a = allocator.Allocate(30);
        var b = allocator.Allocate(30);
        var c = allocator.Allocate(30);

        allocator.Free(a, 30);
        allocator.Free(c, 30);
        Assert.Equal(30, allocator.LargestFree);
        Assert.Equal(-1, allocator.Allocate(60));

        allocator.Free(b, 30);

        Assert.Equal(90, allocator.LargestFree);
        Assert.Equal(0, allocator.Allocate(90));
    }

    [Fact]
    public void RangeAllocator_ReportsFullnessAndRejectsBadFrees()
    {
        var allocator = new RangeAllocator(10);
        var a = allocator.Allocate(10);

        Assert.Equal(-1, allocator.Allocate(1));

        allocator.Free(a, 10);

        Assert.Throws<InvalidOperationException>(() => allocator.Free(a, 10));
        Assert.Throws<InvalidOperationException>(() => allocator.Free(2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Free(8, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Allocate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RangeAllocator(0));
    }

    [Fact]
    public void RangeAllocator_SurvivesManyRandomAllocationsAndFrees()
    {
        var allocator = new RangeAllocator(10_000);
        var live = new List<(int Start, int Length)>();
        var random = new Random(5);

        for (var i = 0; i < 5000; i++)
        {
            if (live.Count > 0 && random.Next(3) == 0)
            {
                var index = random.Next(live.Count);
                allocator.Free(live[index].Start, live[index].Length);
                live.RemoveAt(index);
            }
            else
            {
                var length = random.Next(1, 200);
                var start = allocator.Allocate(length);
                if (start >= 0)
                {
                    live.Add((start, length));
                }
            }

            var ordered = live.OrderBy(r => r.Start).ToList();
            for (var n = 1; n < ordered.Count; n++)
            {
                Assert.True(ordered[n - 1].Start + ordered[n - 1].Length <= ordered[n].Start, "two live ranges overlap");
            }
        }

        foreach (var (start, length) in live)
        {
            allocator.Free(start, length);
        }

        Assert.Equal(10_000, allocator.LargestFree);
        Assert.Equal(0, allocator.AllocationCount);
    }

    [Fact]
    public void ChunkStreamer_LoadsACircleNearestFirstOnlyOnce()
    {
        var streamer = new ChunkStreamer(viewRadius: 3);

        var first = streamer.Update(new ChunkCoord(0, 0));
        var again = streamer.Update(new ChunkCoord(0, 0));

        Assert.Equal(first.Load.Count, streamer.RequestedCount);
        Assert.Equal(29, first.Load.Count);
        Assert.Equal(new ChunkCoord(0, 0), first.Load[0]);
        Assert.Empty(again.Load);
        Assert.Empty(again.Unload);
        Assert.DoesNotContain(new ChunkCoord(3, 3), first.Load);
        Assert.Contains(new ChunkCoord(3, 0), first.Load);

        var distances = first.Load.Select(c => (c.X * c.X) + (c.Z * c.Z)).ToList();
        Assert.Equal(distances.Order(), distances);
    }

    [Fact]
    public void ChunkStreamer_MovingLoadsTheNewEdgeAndUnloadsOnlyBeyondTheHysteresis()
    {
        var streamer = new ChunkStreamer(viewRadius: 3, hysteresis: 1);
        streamer.Update(new ChunkCoord(0, 0));

        var step = streamer.Update(new ChunkCoord(1, 0));

        Assert.All(step.Load, c => Assert.True(c.X >= 1 - 3 && c.X <= 1 + 3));
        Assert.Contains(new ChunkCoord(4, 0), step.Load);
        Assert.Empty(step.Unload);

        var far = streamer.Update(new ChunkCoord(10, 0));

        Assert.Contains(new ChunkCoord(0, 0), far.Unload);
        Assert.DoesNotContain(new ChunkCoord(0, 0), far.Load);
        Assert.False(streamer.IsRequested(new ChunkCoord(0, 0)));
        Assert.True(streamer.IsRequested(new ChunkCoord(10, 0)));

        var back = streamer.Update(new ChunkCoord(0, 0));
        Assert.Contains(new ChunkCoord(0, 0), back.Load);
    }

    [Fact]
    public void ChunkStreamer_FindsTheChunkForAnyWorldPositionIncludingNegatives()
    {
        Assert.Equal(new ChunkCoord(0, 0), ChunkStreamer.ChunkOf(0, 0));
        Assert.Equal(new ChunkCoord(0, 0), ChunkStreamer.ChunkOf(15.9f, 15.9f));
        Assert.Equal(new ChunkCoord(1, 1), ChunkStreamer.ChunkOf(16, 16));
        Assert.Equal(new ChunkCoord(-1, -1), ChunkStreamer.ChunkOf(-0.1f, -16));
        Assert.Equal(new ChunkCoord(-2, 0), ChunkStreamer.ChunkOf(-16.1f, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkStreamer(0));
    }

    [Fact]
    public void TerrainTextures_AreOpaqueDeterministicAndCoverEveryBlockFace()
    {
        var textures = TerrainTextures.Generate();

        Assert.Equal(TerrainTextures.TileCount * TerrainTextures.TileSize * TerrainTextures.TileSize * 4, textures.Length);
        Assert.Equal(textures, TerrainTextures.Generate());
        for (var i = 3; i < textures.Length; i += 4)
        {
            Assert.Equal(255, textures[i]);
        }

        for (ushort block = Blocks.Bedrock; block <= Blocks.Lamp; block++)
        {
            for (var face = 0; face < 6; face++)
            {
                var tile = Blocks.Tile(block, face);
                Assert.InRange(tile, 0, TerrainTextures.TileCount - 1);
                Assert.False(IsMagenta(textures, tile), $"block {block} face {face} falls back to the missing-texture colour");
            }
        }
    }

    [Fact]
    public void TerrainTextures_LookLikeWhatTheyAre()
    {
        var textures = TerrainTextures.Generate();

        var (grassR, grassG, grassB) = Average(textures, Blocks.Tile(Blocks.Grass, 2));
        var (dirtR, dirtG, dirtB) = Average(textures, Blocks.Tile(Blocks.Dirt, 0));
        var (stoneR, stoneG, stoneB) = Average(textures, Blocks.Tile(Blocks.Stone, 0));
        var (lampR, lampG, _) = Average(textures, Blocks.Tile(Blocks.Lamp, 0));

        Assert.True(grassG > grassR && grassG > grassB, "grass top is green");
        Assert.True(dirtR > dirtG && dirtG > dirtB, "dirt is brown");
        Assert.True(Math.Abs(stoneR - stoneG) < 12 && Math.Abs(stoneG - stoneB) < 12, "stone is grey");
        Assert.True(lampR > 200 && lampG > 180, "lamp is bright");
        Assert.NotEqual(Blocks.Tile(Blocks.Grass, 2), Blocks.Tile(Blocks.Grass, 0));
        Assert.Equal(Blocks.Tile(Blocks.Grass, 0), Blocks.Tile(Blocks.Grass, 1));
    }

    private static bool IsMagenta(byte[] textures, int tile)
    {
        var offset = tile * TerrainTextures.TileSize * TerrainTextures.TileSize * 4;
        return textures[offset] == 255 && textures[offset + 1] == 0 && textures[offset + 2] == 255;
    }

    private static (double R, double G, double B) Average(byte[] textures, int tile)
    {
        var offset = tile * TerrainTextures.TileSize * TerrainTextures.TileSize * 4;
        double r = 0, g = 0, b = 0;
        var pixels = TerrainTextures.TileSize * TerrainTextures.TileSize;
        for (var i = 0; i < pixels; i++)
        {
            r += textures[offset + (i * 4)];
            g += textures[offset + (i * 4) + 1];
            b += textures[offset + (i * 4) + 2];
        }

        return (r / pixels, g / pixels, b / pixels);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");
}
