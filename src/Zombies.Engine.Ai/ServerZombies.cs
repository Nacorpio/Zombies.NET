using System.Numerics;
using System.Text;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Animation;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Ai;

/// <summary>
/// The Server's zombies, built the one way both a dedicated Server and the client's embedded Server build them: the Zombie
/// content from the mods, the base Traits plus every Code mod's Traits, the humanoid rig from the base mod, and the AI over the
/// terrain the Server has. Keeping this in one place is what makes solo and co-op behave identically (ADR 0003).
/// </summary>
public sealed class ServerZombies
{
    private ServerZombies(ZombieSystem system, ZombieAi ai, ChunkTerrain terrain)
    {
        System = system;
        Ai = ai;
        Terrain = terrain;
    }

    public ZombieSystem System { get; }

    public ZombieAi Ai { get; }

    /// <summary>The terrain the AI walks and sees through. The Server adds chunks to it as it generates them.</summary>
    public ChunkTerrain Terrain { get; }

    /// <summary>
    /// Builds the zombies for a Server. <paramref name="packages"/> are the mod packages the Server loaded, which supply the
    /// Zombie content and the humanoid rig; <paramref name="codeMods"/> supplies the Traits a Code mod registered.
    /// </summary>
    /// <exception cref="InvalidOperationException">The base mod has no humanoid rig, or a Zombie type names an unregistered Trait.</exception>
    public static ServerZombies Build(
        ServerWorld world,
        IReadOnlyList<ModPackage> packages,
        ModLoadResult mods,
        CodeModLoadResult codeMods,
        ZombieAiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(codeMods);

        var baseMod = packages.Single(p => p.Source == "base");
        var skeletonOk = Skeleton.TryParse(Asset(baseMod, "rigs/humanoid.skeleton.json"), out var skeleton, out var skeletonError);
        var clipsOk = ClipSet.TryParse(Asset(baseMod, "rigs/humanoid.clips.json"), out var clips, out var clipError);
        if (!skeletonOk || !clipsOk)
        {
            throw new InvalidOperationException($"The humanoid rig did not load: {skeletonError} {clipError}");
        }

        var traits = new TraitRegistry();
        BaseTraits.Register(traits);
        CodeModTraits.Register(traits, codeMods);

        var catalog = ZombieContentLoader.Load(mods.Registry);
        var system = new ZombieSystem(world, catalog, traits, skeleton, clips);
        var terrain = new ChunkTerrain();
        var ai = new ZombieAi(world, system, terrain, options: options);
        return new ServerZombies(system, ai, terrain);
    }

    /// <summary>
    /// Generates the terrain around <paramref name="center"/> and spawns the zombies of every Settlement the generated regions
    /// hold. Returns how many zombies spawned and any spawn that could not happen, so a run can report both.
    /// </summary>
    public ZombieSpawnReport Populate(WorldGenerator generator, ChunkCoord center, int radius, int worldDay = 0)
    {
        ArgumentNullException.ThrowIfNull(generator);
        Terrain.Generate(generator, center, radius);

        var spawned = new List<uint>();
        var problems = new List<string>();
        var seen = new HashSet<RegionCoord>();
        for (var z = center.Z - radius; z <= center.Z + radius; z++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                var region = RegionGrid.RegionOf(x * ChunkConstants.Size, z * ChunkConstants.Size);
                if (!seen.Add(region) || generator.SettlementIn(region) is not { } plan)
                {
                    continue;
                }

                var report = System.SpawnSettlement(plan, GroundHeight, worldDay);
                spawned.AddRange(report.Spawned);
                problems.AddRange(report.Problems);
            }
        }

        return new ZombieSpawnReport(spawned, problems);
    }

    /// <summary>The ground a zombie stands on at a column, or the terrain's surface when the column is not loaded.</summary>
    public float GroundHeight(int x, int z)
    {
        var surface = Terrain.SurfaceY(x, z);
        return surface < 0 ? 0f : surface;
    }

    /// <summary>
    /// Wires the world's Containers into a Server: the Containers of the generated regions, and the opener that fills one from
    /// its room's Area type the first time a player opens it. Call after <see cref="Populate"/> has generated the terrain.
    /// </summary>
    public static void WireContainers(GameServer server, WorldGenerator generator, ChunkCoord center, int radius, AreaLootService loot, IItemCatalog items)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(loot);
        ArgumentNullException.ThrowIfNull(items);

        var containers = new List<WorldContainer>();
        var seen = new HashSet<RegionCoord>();
        for (var z = center.Z - radius; z <= center.Z + radius; z++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                var region = RegionGrid.RegionOf(x * ChunkConstants.Size, z * ChunkConstants.Size);
                if (seen.Add(region))
                {
                    containers.AddRange(generator.ContainersIn(region));
                }
            }
        }

        server.WorldContainers = new FixedWorldContainers(containers);
        server.ContainerOpener = new WorldContainerOpener(loot, items, new InMemoryContainerRepository());
    }

    /// <summary>The Containers of the generated regions, which do not change while the Server runs.</summary>
    private sealed class FixedWorldContainers(IReadOnlyList<WorldContainer> containers) : IWorldContainerSource
    {
        public IReadOnlyList<WorldContainer> Near(Vector3 position, float radius)
        {
            var result = new List<WorldContainer>();
            var squared = radius * radius;
            foreach (var container in containers)
            {
                var dx = container.X + 0.5f - position.X;
                var dy = container.Y + 0.5f - position.Y;
                var dz = container.Z + 0.5f - position.Z;
                if ((dx * dx) + (dy * dy) + (dz * dz) <= squared)
                {
                    result.Add(container);
                }
            }

            return result;
        }
    }

    private static string Asset(ModPackage package, string path) =>
        Encoding.UTF8.GetString(package.Assets.Single(a => a.Path == path).Bytes).TrimStart('﻿');
}
