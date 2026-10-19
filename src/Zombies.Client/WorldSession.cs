using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Ai;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Physics;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Voxel;

namespace Zombies.Client;

/// <summary>Everything the client needs to show the world: the loaded mods, the Server it is joined to, the generator, worker threads, and the manager that streams chunks to the GPU.</summary>
internal sealed class WorldSession : IDisposable
{
    private readonly ChunkPipeline _pipeline;
    private readonly PhysicsWorld _physics;
    private readonly HashSet<ChunkCoord> _physicsChunks = [];
    private readonly WorldGenerator _generator;
    private readonly LiteNetTransport? _remoteTransport;

    public WorldSession(ClientOptions options, IWorldRenderer renderer)
    {
        var packages = DirectoryModSource.Read(options.ModsDirectory ?? DirectoryModSource.Find(AppContext.BaseDirectory));
        var mods = ModLoader.Load(packages);
        if (!mods.IsSuccess)
        {
            throw new InvalidOperationException("The mods could not be loaded:" + Environment.NewLine + string.Join(Environment.NewLine, mods.Errors));
        }

        // Solo play runs the client and the Server in one process, so every Code mod's code runs here. A client of a dedicated
        // Server runs only the client-side mods, exactly as the Server ran only the server-side ones.
        var role = options.Connect is null ? ProcessRole.Solo : ProcessRole.Client;
        var codeMods = CodeModLoader.Load(packages, mods, role);
        if (!codeMods.IsSuccess)
        {
            throw new InvalidOperationException("The code mods could not be loaded:" + Environment.NewLine + string.Join(Environment.NewLine, codeMods.Problems));
        }

        var identity = GameIdentity.From(mods, WorldGenerator.GeneratorVersion, new WorldOptions(WorldOptionCatalog.From(mods.Registry), []));
        var playerName = string.IsNullOrEmpty(options.PlayerName)
            ? (string.IsNullOrEmpty(Environment.UserName) ? "player" : Environment.UserName)
            : options.PlayerName;

        if (options.Connect is { } address)
        {
            // Join a dedicated Server: the seed and the mod verification come from it, as the solo path takes them from the embedded one.
            var (host, port) = ParseAddress(address);
            _remoteTransport = LiteNetTransport.Connect(host, port, options.ConnectionKey);
            Client = new GameClient(_remoteTransport, identity, playerName);
            for (var i = 0; i < 600 && Client.State == ClientState.Connecting; i++)
            {
                Client.Poll();
                Thread.Sleep(5);
            }

            if (Client.State != ClientState.Joined)
            {
                throw new JoinRefusedException(Client.Refusal, Client.RefusalDetail);
            }
        }
        else
        {
            // Solo play: join an embedded Server and take the world seed from it, as a client of a dedicated server would.
            var solo = new ServerOptions(identity, options.Seed)
            {
                Items = new ItemCatalog(mods.Registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json))),
                Effects = StatusEffectContentLoader.Load(mods.Registry),
                StartingItems = StartingItems(mods),
            };

            Solo = new EmbeddedServer(solo, playerName);
            CodeModMessages.Register(Solo.Server.Commands, codeMods);
            Client = Solo.Client;

            // The world's zombies, built the same way the dedicated Server builds them, so solo and co-op match. They live in the
            // Server's own world, so the AI moves the entities the Server replicates.
            Zombies = ServerZombies.Build(Solo.Server.World, packages, mods, codeMods);
            Solo.Server.Zombies = Zombies.System;
            Solo.Add(Zombies.System);
            Solo.Add(Zombies.Ai);
            Zombies.Ai.PlayerAttacked += attack =>
            {
                if (Solo.Server.TryGetPlayer(new ConnectionId((int)attack.Player), out var bitten))
                {
                    Solo.Server.Bite(bitten, Zombies.System, attack.Zombie, attack.Part);
                }
            };
        }

        ModMessages = codeMods.Messages;

        var biomes = new BiomeCatalog(mods.Registry.OfKind("biome").Select(d => BiomeJson.Parse(d.Json)));
        _generator = new WorldGenerator(Client.WorldSeed, biomes, SettlementContentLoader.Load(mods.Registry));
        _pipeline = new ChunkPipeline(_generator);
        Manager = new ChunkRenderManager(_pipeline, renderer, new ChunkStreamer(options.ViewDistance));
        ViewDistance = options.ViewDistance;

        if (Solo is not null)
        {
            var spawn = Solo.Server.Options.SpawnPoint;
            var spawnChunk = new ChunkCoord((int)spawn.X >> 4, (int)spawn.Z >> 4);
            Zombies!.Populate(_generator, spawnChunk, radius: 4);

            // The world's Containers, filled from each room's Area type the first time a player opens one.
            var areas = new AreaTypeCatalog(mods.Registry.OfKind("area_type").Select(d => AreaTypeJson.Parse(d.Json)));
            var areaLoot = new AreaLootService(areas, new LootService(new LootTableCatalog(mods.Registry.OfKind("loot").Select(d => LootTableJson.Parse(d.Json)))));
            ServerZombies.WireContainers(Solo.Server, _generator, spawnChunk, radius: 4, areaLoot, Solo.Server.Options.Items);
        }

        // The player walks on Jolt terrain, and the Server moves them with the same character controller.
        _physics = new PhysicsWorld();
        if (Solo is not null)
        {
            Solo.Server.Collision = _physics;
        }

        Client.Collision = _physics.Create(Client.Local.State.Position, PlayerMovement.StandingHeight);
    }

    /// <summary>The embedded Server, or null when this client joined a dedicated Server.</summary>
    public EmbeddedServer? Solo { get; }

    /// <summary>The client, over the embedded Server's in-memory transport or a dedicated Server's UDP socket.</summary>
    public GameClient Client { get; }

    /// <summary>The world's zombies, which the Server ticks and the client draws. Null when joined to a dedicated Server, which owns them.</summary>
    public ServerZombies? Zombies { get; }

    /// <summary>The numbers of the Code mods' messages, for sending one with <see cref="GameClient.Send{TMessage}(ModMessageTable, in TMessage)"/>.</summary>
    public ModMessageTable ModMessages { get; }

    public ChunkRenderManager Manager { get; }

    public int ViewDistance { get; }

    public int WorkerCount => _pipeline.WorkerCount;

    /// <summary>Chunks whose terrain is in the physics world.</summary>
    public int PhysicsChunkCount => _physics.TerrainChunkCount;

    /// <summary>Runs the Server ticks that <paramref name="elapsed"/> real time owes, then lets the client read what they sent.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (Solo is not null)
        {
            Solo.Advance(elapsed);
        }
        else
        {
            Client.Poll();
        }
    }

    /// <summary>
    /// The world Container the player is looking at, from the Server's own world, so the client can show a prompt and ask to
    /// open it. Null when this client joined a dedicated Server, which owns the Containers and answers the open command itself.
    /// </summary>
    public WorldContainer? TargetedContainer(Vector3 eye, Vector3 direction)
    {
        if (Solo is null)
        {
            return null;
        }

        var near = Solo.Server.WorldContainers.Near(eye, ContainerTargeting.DefaultReach + 1f);
        return ContainerTargeting.Find(near, eye, direction, Solo.Server.ContainerReach);
    }

    private static (string Host, int Port) ParseAddress(string address)
    {
        var colon = address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(address[(colon + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException($"--connect needs HOST:PORT, not '{address}'.");
        }

        return (address[..colon], port);
    }

    /// <summary>The items every player starts with, so a fresh world has something to use and a weapon to hold.</summary>
    private static List<StartingItem> StartingItems(ModLoadResult mods)
    {
        var catalog = new ItemCatalog(mods.Registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        var items = new List<StartingItem>();
        foreach (var (id, count) in new[]
        {
            ("base:item/canned_beans", 3),
            ("base:item/bandage", 4),
            ("base:item/water_bottle", 1),
            ("base:item/crowbar", 1),
            ("base:item/suppressor", 1),
            ("base:item/red_dot_sight", 1),
        })
        {
            if (ItemId.TryParse(id, out var item) && catalog.TryGet(item, out _))
            {
                items.Add(new StartingItem(item, count));
            }
        }

        if (ItemId.TryParse("base:item/pistol_9mm", out var pistol) && catalog.TryGet(pistol, out _))
        {
            items.Add(new StartingItem(pistol, 1, ItemState.Create([new(WeaponService.ConditionValue, 72), new(WeaponService.RoundsValue, 9)])));
        }

        return items;
    }

    /// <summary>
    /// Adds the terrain of every loaded chunk to the physics world and drops the terrain of chunks that streamed out, so
    /// the player always walks on what is drawn.
    /// </summary>
    public void SyncPhysicsTerrain()
    {
        foreach (var chunk in Manager.Chunks)
        {
            if (_physicsChunks.Add(chunk.Coord))
            {
                _physics.AddTerrain(_pipeline.GetChunk(chunk.Coord));
            }
        }

        if (_physicsChunks.Count == Manager.LoadedCount)
        {
            return;
        }

        _physicsChunks.RemoveWhere(coord =>
        {
            if (Manager.Chunks.Any(c => c.Coord == coord))
            {
                return false;
            }

            _physics.RemoveTerrain(coord);
            return true;
        });
    }

    /// <summary>Steps the physics world once per simulation tick, after the players have moved.</summary>
    public void StepPhysics(TimeSpan elapsed)
    {
        var ticks = (int)Math.Min(Math.Round(elapsed.TotalSeconds * Simulation.TickRateHz), 5);
        for (var i = 0; i < ticks; i++)
        {
            _physics.Step(PlayerMovement.StepSeconds);
        }
    }

    public void Dispose()
    {
        _physics.Dispose();
        _pipeline.Dispose();
        Solo?.Dispose();
        _remoteTransport?.Dispose();
    }
}

/// <summary>A dedicated Server refused the join, with the reason it gave, so the client can say why instead of just logging it.</summary>
internal sealed class JoinRefusedException(JoinRefusal? reason, string? detail)
    : Exception($"The Server refused the join: {reason} {detail}".Trim())
{
    public JoinRefusal? Reason { get; } = reason;

    public string? Detail { get; } = detail;
}

/// <summary>
/// Turns the keyboard and mouse into <see cref="PlayerInput"/> and sends it, so the Server moves the player and the client
/// predicts the same move. W A S D walk, Shift sprints, Ctrl crouches, Q and E lean, Space jumps, Tab captures the mouse.
/// </summary>
internal static class PlayerController
{
    public static PlayerInput Read(InputState input, float yaw, float pitch, bool mouseCaptured)
    {
        var forward = (input.IsDown(Key.W) ? 1f : 0f) - (input.IsDown(Key.S) ? 1f : 0f);
        var strafe = (input.IsDown(Key.D) ? 1f : 0f) - (input.IsDown(Key.A) ? 1f : 0f);
        return new PlayerInput(
            forward,
            strafe,
            yaw,
            pitch,
            input.IsDown(Key.LeftShift),
            input.IsDown(Key.LeftCtrl),
            input.IsDown(Key.Q),
            input.IsDown(Key.E),
            input.IsDown(Key.Space));
    }

    /// <summary>Places the camera at the player's eye, leaning sideways when asked.</summary>
    public static void ApplyToCamera(Camera camera, in PlayerMoveState state, float lean)
    {
        var eye = PlayerMovement.EyePosition(state);
        var right = new Vector3(MathF.Cos(state.Yaw), 0, MathF.Sin(state.Yaw));
        camera.Position = eye + (right * lean);
        camera.Yaw = state.Yaw;
        camera.Pitch = state.Pitch;
    }
}

/// <summary>Moves the camera from keys and mouse. W A S D fly, Space and Ctrl go up and down, Shift is fast.</summary>
internal static class FlyController
{
    public const float WalkSpeed = 24f;
    public const float FastSpeed = 90f;

    public static void Update(Camera camera, InputState input, float seconds, bool mouseCaptured)
    {
        if (mouseCaptured)
        {
            camera.Look(input.MouseDeltaX, input.MouseDeltaY);
        }

        var speed = (input.IsDown(Key.LeftShift) ? FastSpeed : WalkSpeed) * seconds;
        var right = (input.IsDown(Key.D) ? 1 : 0) - (input.IsDown(Key.A) ? 1 : 0);
        var forward = (input.IsDown(Key.W) ? 1 : 0) - (input.IsDown(Key.S) ? 1 : 0);
        var up = (input.IsDown(Key.Space) ? 1 : 0) - (input.IsDown(Key.LeftCtrl) ? 1 : 0);
        camera.Fly(right * speed, up * speed, forward * speed);
    }
}

internal static class SkyColors
{
    /// <summary>Linear light to the gamma-encoded byte color the swapchain expects, since the terrain shader encodes the same way.</summary>
    public static Rgba Encode(Vector3 linear) => new(
        Channel(linear.X),
        Channel(linear.Y),
        Channel(linear.Z));

    private static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(MathF.Pow(MathF.Max(value, 0f), 1f / 2.2f) * 255f), 0f, 255f);
}
