using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Domain.Zombies;
using Zombies.Engine.Ai;
using Zombies.Engine.Core;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Tests.Rigs;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>
/// The zombie AI run the way the Server runs it: a Server with clients joined over the in-memory transport, the zombies, and their AI,
/// ticked together on flat test terrain with walls and doors built where a test needs them. Tests assert what the world shows.
/// </summary>
public sealed class ZombieAiTests
{
    private const int Floor = 10;
    private const string Walker = "t:zombie/walker";

    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    private static readonly string WalkerJson = """
        {
          "id": "t:zombie/walker",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.5, "maxLevel": 5, "perLevelBonus": 0.5 },
          "senses": { "sight": 20, "hearing": 30 },
          "traits": [],
          "appearance": { "skinTones": ["#8fa07a"] },
          "outfit": { "clothing": [], "clothingCount": { "min": 0, "max": 0 }, "headwear": [], "backpacks": [] },
          "missingParts": []
        }
        """;

    /// <summary>Where players wait when a test does not need them near: far from every zombie, in a corner of the terrain.</summary>
    private static readonly Vector3 Away = new(-28.5f, Floor, -28.5f);

    private sealed class Rig
    {
        private readonly Dictionary<ChunkCoord, Chunk> _chunks = [];

        public Rig(int players = 1, ZombieAiOptions? options = null, int chunkRadius = 2)
        {
            // Flat stone from y 0 to 9, so everything stands at y 10, over chunks -2 to 5 on each axis unless the test needs more.
            for (var cz = -2; cz <= 3 + chunkRadius; cz++)
            {
                for (var cx = -2; cx <= 3 + chunkRadius; cx++)
                {
                    var chunk = new Chunk(new ChunkCoord(cx, cz));
                    for (var y = 0; y < Floor; y++)
                    {
                        for (var z = 0; z < ChunkConstants.Size; z++)
                        {
                            for (var x = 0; x < ChunkConstants.Size; x++)
                            {
                                chunk.Set(x, y, z, Blocks.Stone);
                            }
                        }
                    }

                    chunk.RecomputeHeights();
                    _chunks[chunk.Coord] = chunk;
                    Terrain.Add(chunk);
                }
            }

            Server = new GameServer(Network.CreateServer(), new ServerOptions(Identity, WorldSeed: 3) { SpawnPoint = Away });
            var traits = new TraitRegistry();
            BaseTraits.Register(traits);
            Zombies = new ZombieSystem(Server.World, new ZombieCatalog([ZombieTypeJson.Parse(WalkerJson)]), traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
            // A generous search budget, so how fast or busy the machine is never decides a test. The budget itself is tested on the planner.
            Ai = new ZombieAi(Server.World, Zombies, Terrain, options: (options ?? new ZombieAiOptions()) with { PathfindingBudget = TimeSpan.FromMilliseconds(50) });
            for (var i = 0; i < players; i++)
            {
                Clients.Add(new GameClient(Network.Connect(), Identity, $"player{i}"));
            }

            Run(5);
            foreach (var client in Clients)
            {
                Assert.Equal(ClientState.Joined, client.State);
            }
        }

        public ChunkTerrain Terrain { get; } = new();

        public InMemoryNetwork Network { get; } = new();

        public GameServer Server { get; }

        public ZombieSystem Zombies { get; }

        public ZombieAi Ai { get; }

        public List<GameClient> Clients { get; } = [];

        public long Tick { get; private set; }

        public void Step()
        {
            Server.Tick(Tick);
            Zombies.Tick(Tick);
            Ai.Tick(Tick);
            Tick++;
            foreach (var client in Clients)
            {
                client.Poll();
            }
        }

        public void Run(int ticks, Action? eachTick = null)
        {
            for (var i = 0; i < ticks; i++)
            {
                Step();
                eachTick?.Invoke();
            }
        }

        public PlayerSession Player(int index)
        {
            Assert.True(Server.TryGetPlayer(new ConnectionId(index + 1), out var session));
            return session;
        }

        public void Place(int player, Vector3 at) => Assert.True(Server.Teleport(Player(player), at, 0f));

        public uint Spawn(Vector3 at, ulong seed = 1, float yaw = 0f)
        {
            Assert.True(Zombies.TrySpawn(new ZombieSpec(seed, Walker, 1), at, yaw, out var id, out var problem), problem);
            return id;
        }

        public Vector3 PositionOf(uint id)
        {
            Assert.True(Server.World.TryGet(id, out var state));
            return state.Position;
        }

        public ZombieMindView Mind(uint id)
        {
            Assert.True(Ai.TryGetMind(id, out var view));
            return view;
        }

        public void SetBlock(int x, int y, int z, ushort block)
        {
            var chunk = _chunks[new ChunkCoord(x >> 4, z >> 4)];
            chunk.Set(x & 15, y, z & 15, block);
        }

        /// <summary>Builds a wall of stone from the floor up, along a line of blocks from one corner to the other.</summary>
        public void Wall(int x0, int z0, int x1, int z1, int height)
        {
            for (var x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            {
                for (var z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
                {
                    for (var y = Floor; y < Floor + height; y++)
                    {
                        SetBlock(x, y, z, Blocks.Stone);
                    }
                }
            }
        }

        /// <summary>A closed room of walls three blocks high from (x0, z0) to (x1, z1), its walls included.</summary>
        public void Room(int x0, int z0, int x1, int z1)
        {
            Wall(x0, z0, x1, z0, 3);
            Wall(x0, z1, x1, z1, 3);
            Wall(x0, z0, x0, z1, 3);
            Wall(x1, z0, x1, z1, 3);
        }

        public void Doorway(int x, int z)
        {
            SetBlock(x, Floor, z, Blocks.Air);
            SetBlock(x, Floor + 1, z, Blocks.Air);
        }
    }

    private static Vector3 At(float x, float z) => new(x, Floor, z);

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    [Fact]
    public void ASound_DrawsTheZombiesThatHearIt_AndNotThoseOutOfEarshot()
    {
        var rig = new Rig();
        var near = rig.Spawn(At(10.5f, 10.5f));
        var far = rig.Spawn(At(70.5f, 10.5f));
        var farStart = rig.PositionOf(far);
        rig.Run(30);
        Assert.Equal(ZombieMindState.Idle, rig.Mind(near).State);

        // A gunshot that carries 40 m: the near zombie is 20 m away; the far one is 40 m away, past its 30 m of hearing.
        var shot = At(30.5f, 10.5f);
        rig.Ai.Perception.Emit(shot, 40f);
        rig.Run(2);
        Assert.Equal(ZombieMindState.Investigating, rig.Mind(near).State);
        Assert.Equal(ZombieMindState.Idle, rig.Mind(far).State);

        rig.Run(20 * Simulation.TickRateHz);

        Assert.True(Flat(rig.PositionOf(near), shot) <= 2f, $"The zombie that heard the shot is at {rig.PositionOf(near)}.");
        Assert.Equal(farStart, rig.PositionOf(far));

        // Every client sees the zombie where the Server moved it.
        Assert.True(rig.Clients[0].World.TryGet(near, out var seen));
        Assert.Equal(rig.PositionOf(near), seen.Position);
    }

    [Fact]
    public void Footsteps_DrawZombies_ButAPlayerCreepingSlowlyIsNotHeard()
    {
        var rig = new Rig();

        // The zombie faces -Z; the player passes behind it, out of sight, 6 m away.
        var zombie = rig.Spawn(At(20.5f, 20.5f));
        var creeping = 0f;
        rig.Run(60, () => rig.Place(0, At(14.5f + (creeping += 0.05f), 26.5f)));
        Assert.Equal(ZombieMindState.Idle, rig.Mind(zombie).State);

        var running = 0f;
        rig.Run(30, () => rig.Place(0, At(14.5f + (running += 0.25f), 26.5f)));

        Assert.NotEqual(ZombieMindState.Idle, rig.Mind(zombie).State);
    }

    [Fact]
    public void AZombieThatSeesAPlayer_ChasesThem_ButAWallHidesThem()
    {
        var rig = new Rig(players: 2);
        rig.Wall(30, 0, 30, 40, 3);

        // Both zombies face -Z. Each player stands 10 m ahead of one of them, one in the open and one behind the wall.
        var watching = rig.Spawn(At(10.5f, 30.5f));
        var blind = rig.Spawn(At(35.5f, 30.5f), seed: 2);
        rig.Place(0, At(10.5f, 20.5f));
        rig.Place(1, At(25.5f, 22.5f));
        rig.Run(10);

        Assert.Equal(ZombieMindState.Chasing, rig.Mind(watching).State);
        Assert.Equal(rig.Player(0).EntityId, rig.Mind(watching).TargetPlayer);
        Assert.Equal(ZombieMindState.Idle, rig.Mind(blind).State);

        rig.Run(10 * Simulation.TickRateHz);
        Assert.True(Flat(rig.PositionOf(watching), At(10.5f, 20.5f)) <= 1.5f);
    }

    [Fact]
    public void AHorde_FollowsOneFlowFieldAroundAWall_ToItsTarget()
    {
        var rig = new Rig();

        // A wall with one gap at its south end stands between the horde and a noise.
        rig.Wall(40, 0, 40, 50, 3);
        rig.Wall(40, 52, 40, 70, 3);
        var zombies = new List<uint>();
        for (var i = 0; i < 12; i++)
        {
            zombies.Add(rig.Spawn(At(20.5f + (i % 4 * 2), 10.5f + (i / 4 * 2)), seed: (ulong)i + 1));
        }

        var noise = At(47.5f, 12.5f);
        rig.Ai.Perception.Emit(noise, 60f);
        rig.Run(Simulation.TickRateHz);
        Assert.Equal(1, rig.Ai.FlowFieldCount);
        Assert.All(zombies, z => Assert.True(rig.Mind(z).FollowsFlowField, $"Zombie {z} plans alone."));

        // Nobody walks through the wall: at every tick each zombie stands in a block it fits in.
        rig.Run(45 * Simulation.TickRateHz, () =>
        {
            foreach (var z in zombies)
            {
                var cell = NavCell.Containing(rig.PositionOf(z));
                Assert.False(rig.Terrain.IsSolid(cell.X, cell.Y, cell.Z), $"Zombie {z} is inside a wall at {cell}.");
            }
        });

        Assert.All(zombies, z => Assert.True(Flat(rig.PositionOf(z), noise) <= 4f, $"Zombie {z} stopped at {rig.PositionOf(z)}."));
    }

    [Fact]
    public void AHordeChasingAPlayer_SharesOneFlowFieldThatFollowsThem()
    {
        var rig = new Rig();
        var zombies = new List<uint>();
        for (var i = 0; i < 6; i++)
        {
            zombies.Add(rig.Spawn(At(20.5f + i, 30.5f), seed: (ulong)i + 1));
        }

        rig.Place(0, At(23.5f, 20.5f));
        rig.Run(10);
        Assert.Equal(1, rig.Ai.FlowFieldCount);
        Assert.All(zombies, z => Assert.Equal(ZombieMindState.Chasing, rig.Mind(z).State));

        // The player walks slowly (quietly) off east, then stops; the horde keeps after them.
        var x = 23.5f;
        rig.Run(15 * Simulation.TickRateHz, () => rig.Place(0, At(x = Math.Min(x + 0.04f, 35.5f), 20.5f)));

        Assert.Equal(1, rig.Ai.FlowFieldCount);
        Assert.All(zombies, z => Assert.True(Flat(rig.PositionOf(z), At(35.5f, 20.5f)) <= 3f, $"Zombie {z} is at {rig.PositionOf(z)}."));
    }

    [Fact]
    public void DetailCaps_HoldPerPlayer_HoweverManyZombiesCrowdThem()
    {
        var caps = new DetailCaps { FullPerPlayer = 12, ReducedPerPlayer = 40 };
        var rig = new Rig(players: 2, new ZombieAiOptions { Caps = caps });
        rig.Place(0, At(10.5f, 10.5f));
        rig.Place(1, At(60.5f, 60.5f));
        for (var i = 0; i < 200; i++)
        {
            // 150 around the first player and 50 around the second.
            var around = i < 150 ? At(10.5f, 10.5f) : At(60.5f, 60.5f);
            rig.Spawn(around + new Vector3((i % 13) - 6, 0, ((i / 13) % 13) - 6), seed: (ulong)i + 1);
        }

        rig.Run(15);

        Assert.Equal(200, rig.Ai.ActiveCount);
        Assert.Equal(24, rig.Ai.CountAt(DetailLevel.Full));
        Assert.Equal(40 + 38, rig.Ai.CountAt(DetailLevel.Reduced));
        Assert.Equal(150 - 12 - 40, rig.Ai.CountAt(DetailLevel.Dormant));
    }

    [Fact]
    public void DetailAssigner_GivesEachPlayerTheirNearestCreaturesFirst()
    {
        var assigner = new DetailAssigner(new DetailCaps { FullPerPlayer = 1, ReducedPerPlayer = 1, FullRadius = 10, ReducedRadius = 50 });
        Vector3[] players = [Vector3.Zero, new(100, 0, 0)];
        Vector3[] creatures = [new(3, 0, 0), new(1, 0, 0), new(2, 0, 0), new(99, 0, 0), new(30, 0, 0), new(60, 0, 0)];
        var levels = new DetailLevel[creatures.Length];

        assigner.Assign(players, creatures, levels);

        Assert.Equal(
            [DetailLevel.Dormant, DetailLevel.Full, DetailLevel.Reduced, DetailLevel.Full, DetailLevel.Dormant, DetailLevel.Reduced],
            levels);
    }

    [Fact]
    public void ZombiesBreakAClosedDoorInTheirWay_ThenWalkThroughIt()
    {
        var rig = new Rig();
        rig.Room(40, 40, 46, 46);
        rig.Doorway(43, 40);
        var door = rig.Ai.Doors.Add(43, Floor, 40, health: 30);
        var broken = new List<Door>();
        rig.Ai.Doors.Broken += broken.Add;
        var zombie = rig.Spawn(At(43.5f, 30.5f));

        var inside = At(43.5f, 43.5f);
        rig.Ai.Perception.Emit(inside, 40f);
        rig.Run(10 * Simulation.TickRateHz, () =>
        {
            if (door.IsClosed)
            {
                Assert.True(rig.PositionOf(zombie).Z < 40f, "The zombie passed a closed door.");
            }
        });

        Assert.Equal(DoorState.Broken, door.State);
        Assert.Equal([door], broken);
        rig.Run(5 * Simulation.TickRateHz);
        Assert.True(Flat(rig.PositionOf(zombie), inside) <= 2f, $"The zombie is at {rig.PositionOf(zombie)}.");
    }

    [Fact]
    public void ZombiesWalkThroughAnOpenDoor_WithoutBreakingIt()
    {
        var rig = new Rig();
        rig.Room(40, 40, 46, 46);
        rig.Doorway(43, 40);
        var door = rig.Ai.Doors.Add(43, Floor, 40, health: 30);
        Assert.True(door.Open());
        var zombie = rig.Spawn(At(43.5f, 30.5f));

        var inside = At(43.5f, 43.5f);
        rig.Ai.Perception.Emit(inside, 40f);
        rig.Run(15 * Simulation.TickRateHz);

        Assert.Equal(DoorState.Open, door.State);
        Assert.Equal(30, door.Health);
        Assert.True(Flat(rig.PositionOf(zombie), inside) <= 2f);
    }

    [Fact]
    public void ZombiesDoNotClimb_ATwoBlockWallKeepsThemOut()
    {
        var rig = new Rig();

        // A pen with walls two blocks high and no way in. The player stands on a block inside it, in plain sight over the wall.
        rig.Wall(40, 40, 46, 40, 2);
        rig.Wall(40, 46, 46, 46, 2);
        rig.Wall(40, 40, 40, 46, 2);
        rig.Wall(46, 40, 46, 46, 2);
        rig.SetBlock(43, Floor, 43, Blocks.Stone);
        rig.Place(0, new Vector3(43.5f, Floor + 1, 43.5f));
        var zombies = new[] { rig.Spawn(At(43.5f, 52.5f), yaw: 0f), rig.Spawn(At(43.5f, 34.5f), seed: 2, yaw: MathF.PI) };

        rig.Run(20 * Simulation.TickRateHz, () =>
        {
            foreach (var z in zombies)
            {
                var at = rig.PositionOf(z);
                Assert.False(at.X > 40 && at.X < 47 && at.Z > 40 && at.Z < 47, $"Zombie {z} got into the pen at {at}.");
                Assert.True(at.Y < Floor + 1, $"Zombie {z} climbed to {at}.");
            }
        });

        // They saw the player and are still after them, at the foot of the wall.
        Assert.All(zombies, z => Assert.NotEqual(ZombieMindState.Idle, rig.Mind(z).State));
        Assert.All(zombies, z => Assert.True(Flat(rig.PositionOf(z), At(43.5f, 43.5f)) <= 5f, $"Zombie {z} stayed at {rig.PositionOf(z)}."));
    }

    [Fact]
    public void ZombiesStepUpOneBlock_SoALowLedgeDoesNotStopThem()
    {
        var rig = new Rig();

        // The player stands on a platform one block high.
        for (var x = 40; x <= 46; x++)
        {
            for (var z = 40; z <= 46; z++)
            {
                rig.SetBlock(x, Floor, z, Blocks.Stone);
            }
        }

        var top = new Vector3(43.5f, Floor + 1, 43.5f);
        rig.Place(0, top);
        var zombie = rig.Spawn(At(43.5f, 52.5f));

        rig.Run(15 * Simulation.TickRateHz);

        Assert.True(Vector3.Distance(rig.PositionOf(zombie), top) <= 1.5f, $"The zombie is at {rig.PositionOf(zombie)}.");
    }

    [Fact]
    public void ASteadyStateTick_With4PlayersAnd200Zombies_AllocatesNothing()
    {
        var rig = new Rig(players: 4);
        Vector3[] homes = [At(10.5f, 10.5f), At(60.5f, 10.5f), At(10.5f, 60.5f), At(60.5f, 60.5f)];
        for (var i = 0; i < 200; i++)
        {
            var home = homes[i % 4];
            rig.Spawn(home + new Vector3((i * 7 % 25) - 12, 0, (i * 11 % 25) - 12), seed: (ulong)i + 1, yaw: i);
        }

        // The players walk in circles and a gun goes off now and then, so the zombies chase, listen, and plan the whole time.
        Action move = () =>
        {
            for (var p = 0; p < 4; p++)
            {
                var angle = (rig.Tick * 0.02f) + p;
                rig.Place(p, homes[p] + new Vector3(MathF.Cos(angle) * 6, 0, MathF.Sin(angle) * 6));
            }

            if (rig.Tick % 90 == 0)
            {
                rig.Ai.Perception.Emit(homes[(int)(rig.Tick / 90 % 4)], 40f);
            }
        };

        rig.Run(20 * Simulation.TickRateHz, move);
        var allocated = AllocationProbe.Measure(() => rig.Run(10 * Simulation.TickRateHz, move));

        Assert.Equal(0, allocated);
        Assert.Equal(200, rig.Ai.ActiveCount);
    }

    [Fact]
    public void ThePlanner_SearchesWithinItsBudget_AndFinishesALongRouteOverLaterTicks()
    {
        var rig = new Rig(chunkRadius: 4);

        // A zig-zag of walls makes a long route across several clusters.
        for (var i = 0; i < 6; i++)
        {
            var x = 10 + (i * 12);
            if (i % 2 == 0)
            {
                rig.Wall(x, -30, x, 80, 3);
            }
            else
            {
                rig.Wall(x, -20, x, 90, 3);
            }
        }

        var planner = new PathPlanner(new Navigation(rig.Terrain));
        var path = planner.CreatePath();
        var start = new NavCell(0, Floor, 30);
        var goal = new NavCell(90, Floor, 30);
        planner.Request(path, start, goal);

        // No time at all still allows one slice of the search, and the route is not found in it.
        planner.Run(TimeSpan.Zero);
        Assert.Equal(PathStatus.Pending, path.Status);
        Assert.Equal(1, planner.Pending);

        var runs = 1;
        for (; runs < 1000 && path.Status == PathStatus.Pending; runs++)
        {
            planner.Run(TimeSpan.Zero);
        }

        Assert.True(runs > 2, "A long route should take several slices.");
        Assert.Equal(PathStatus.Ready, path.Status);
        Assert.False(path.IsPartial);
        Assert.Equal(goal, path.End);
        var navigation = planner.Navigation;
        var from = start;
        while (path.HasNext)
        {
            var next = path.PeekNext();
            Assert.True(navigation.TryMoveCost(from, next, out _), $"The route jumps from {from} to {next}.");
            from = next;
            path.Advance();
        }
    }

    [Fact]
    public void AGoalBehindAWallTooHighToClimb_GivesAPartialRouteToTheNearestCell()
    {
        var rig = new Rig();
        rig.Wall(40, 40, 46, 40, 2);
        rig.Wall(40, 46, 46, 46, 2);
        rig.Wall(40, 40, 40, 46, 2);
        rig.Wall(46, 40, 46, 46, 2);
        var planner = new PathPlanner(new Navigation(rig.Terrain));
        var path = planner.CreatePath();

        planner.Request(path, new NavCell(43, Floor, 30), new NavCell(43, Floor, 43));
        planner.Run(TimeSpan.FromSeconds(1));

        Assert.Equal(PathStatus.Ready, path.Status);
        Assert.True(path.IsPartial);
        Assert.Equal(new NavCell(43, Floor, 39), path.End);
    }

    [Fact]
    public void ARouteSearchPausedInItsCoarsePhase_StartsAgain_WhenTheTerrainChanges()
    {
        var planner = new PathPlanner(new Navigation(new FlatTerrain()));
        var path = planner.CreatePath();
        var goal = new NavCell(200, Floor, 5);
        planner.Request(path, new NavCell(0, Floor, 0), goal);

        planner.Run(TimeSpan.FromSeconds(1), maxExpansions: 1);
        Assert.Equal(PathStatus.Pending, path.Status);

        // Chunks load or unload while the search is paused, so the regions it was walking are gone.
        planner.TerrainChanged();
        for (var runs = 0; runs < 1000 && path.Status == PathStatus.Pending; runs++)
        {
            planner.Run(TimeSpan.FromSeconds(1), maxExpansions: 64);
        }

        Assert.Equal(PathStatus.Ready, path.Status);
        Assert.False(path.IsPartial);
        Assert.Equal(goal, path.End);
    }

    [Fact]
    public void AFlowFieldCancelledAndRequestedForANewTarget_NeverLeadsTowardTheOldOne()
    {
        var planner = new PathPlanner(new Navigation(new FlatTerrain()));
        var field = planner.CreateFlowField();
        var oldTarget = new NavCell(0, Floor, 0);
        var newTarget = new NavCell(20, Floor, 0);

        // A horde's field goes stale and back to the pool, and a new horde takes it before the planner has dropped the old build.
        planner.Request(field, oldTarget, 100);
        planner.Cancel(field);
        planner.Request(field, newTarget, 100);
        for (var runs = 0; runs < 1000 && field.Version == 0; runs++)
        {
            planner.Run(TimeSpan.FromSeconds(1), maxExpansions: 64);
        }

        Assert.True(field.IsReady);
        Assert.Equal(newTarget, field.Target);
        Assert.True(field.TryGetNext(new NavCell(15, Floor, 0), out var next));
        Assert.Equal(16, next.X);
    }

    [Fact]
    public void AFlowFieldCancelledWhileBuilding_AndRequestedForANewTarget_NeverLeadsTowardTheOldOne()
    {
        var planner = new PathPlanner(new Navigation(new FlatTerrain()));
        var field = planner.CreateFlowField();
        var oldTarget = new NavCell(0, Floor, 0);
        var newTarget = new NavCell(20, Floor, 0);

        planner.Request(field, oldTarget, 100);
        planner.Run(TimeSpan.FromSeconds(1), maxExpansions: 1);
        Assert.Equal(0, field.Version);
        planner.Cancel(field);
        planner.Request(field, newTarget, 100);
        for (var runs = 0; runs < 1000 && field.Version == 0; runs++)
        {
            planner.Run(TimeSpan.FromSeconds(1), maxExpansions: 64);
        }

        Assert.True(field.IsReady);
        Assert.Equal(newTarget, field.Target);
        Assert.True(field.TryGetNext(new NavCell(15, Floor, 0), out var next));
        Assert.Equal(16, next.X);
    }

    /// <summary>Flat ground everywhere, with no chunks to load, for planner tests that reach far.</summary>
    private sealed class FlatTerrain : INavigationTerrain
    {
        public bool IsSolid(int x, int y, int z) => y < Floor;
    }

    [Fact]
    public void NavCellKeys_RoundTrip_ForNegativeCoordinates()
    {
        NavCell[] cells = [new(0, 0, 0), new(-1, 5, -1), new(8_000_000, 127, -8_000_000), new(-123, -4, 456)];

        Assert.All(cells, c => Assert.Equal(c, NavCell.FromKey(c.Key)));
    }
}
