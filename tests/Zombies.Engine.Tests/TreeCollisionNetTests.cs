using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core;
using Zombies.Engine.Net;
using Zombies.Engine.Physics;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>Plays a real client against a real Server on one shared Jolt world, the way solo play does, and walks it into trees.</summary>
[Collection("Jolt")]
public sealed class TreeCollisionNetTests
{
    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void APlayer_RunningIntoTrees_NeverFallsThroughTheWorld(int seed)
    {
        var biomes = new BiomeCatalog([new Biome("base:biome/temperate_forest", ["forest"], (0, 100), (0, 100), 52, 14, 200)]);
        var generator = new WorldGenerator(777, biomes);
        using var physics = new PhysicsWorld();
        var chunks = new Dictionary<ChunkCoord, Chunk>();
        for (var cz = -3; cz <= 3; cz++)
        {
            for (var cx = -3; cx <= 3; cx++)
            {
                var chunk = generator.Generate(new ChunkCoord(cx, cz));
                chunks[chunk.Coord] = chunk;
                physics.AddTerrain(chunk);
            }
        }

        var random = new Random(seed);
        var wx = random.Next(-20, 20);
        var wz = random.Next(-20, 20);
        var ground = GroundTop(chunks[new ChunkCoord(wx >> 4, wz >> 4)], wx & 15, wz & 15);
        var spawn = new Vector3(wx + 0.5f, ground + 0.5f, wz + 0.5f);

        var network = new InMemoryNetwork();
        var transport = network.CreateServer();
        var server = new GameServer(transport, new ServerOptions(Identity, 777) { SpawnPoint = spawn });
        server.Collision = physics;
        var client = new GameClient(network.Connect(), Identity, "tester");
        long tick = 0;
        for (var i = 0; i < 8 && client.State != ClientState.Joined; i++)
        {
            server.Tick(tick++);
            client.Poll();
        }

        Assert.Equal(ClientState.Joined, client.State);
        client.Collision = physics.Create(client.Local.State.Position, PlayerMovement.StandingHeight);

        var clock = new FixedStepClock();
        var yaw = 0f;
        var lowest = float.MaxValue;
        var worst = string.Empty;
        for (var frame = 0; frame < 144 * 20; frame++)
        {
            if (frame % 90 == 0)
            {
                yaw = (float)(random.NextDouble() * Math.PI * 2);
            }

            var input = new PlayerInput(1f, random.Next(3) - 1f, yaw, 0f, random.Next(4) != 0, frame % 70 < 15 && seed % 2 == 0, false, false, frame % 130 == 0);
            for (var owed = clock.Advance(TimeSpan.FromSeconds(1d / (seed % 3 == 0 ? 60d : 144d))); owed > 0; owed--)
            {
                client.SendInput(input);
                server.Tick(tick++);
                client.Poll();
            }

            physics.Step(PlayerMovement.StepSeconds);

            var position = client.Local.State.Position;
            var px = (int)MathF.Floor(position.X);
            var pz = (int)MathF.Floor(position.Z);
            if (chunks.TryGetValue(new ChunkCoord(px >> 4, pz >> 4), out var under))
            {
                var floor = GroundTop(under, px & 15, pz & 15);
                if (position.Y - floor < lowest)
                {
                    lowest = position.Y - floor;
                    worst = $"frame {frame} at {position}, ground {floor}";
                }
            }
        }

        Assert.True(lowest > -2f, $"the player fell through the world: {worst}.");
    }

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
}
