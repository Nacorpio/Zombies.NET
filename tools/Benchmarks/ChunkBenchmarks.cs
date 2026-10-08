using BenchmarkDotNet.Attributes;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Voxel;

namespace Zombies.Benchmarks;

/// <summary>
/// Lighting and greedy meshing of a patch of chunks, the work a mesh worker does for every chunk, against the 2 ms per chunk budget.
/// One chunk alone is not representative (the spawn chunk is flat and bare), so an operation meshes a 5 by 5 patch, which has to stay under
/// <see cref="Chunks"/> times 2 ms. The generated ring around the patch gives every chunk in it real neighbours.
/// </summary>
[MemoryDiagnoser]
public class ChunkBenchmarks
{
    /// <summary>How many chunks one operation lights and meshes.</summary>
    public const int Chunks = 25;

    private const ulong Seed = 12345;
    private const int Radius = 2;

    private readonly List<(Chunk Chunk, ChunkNeighbors Neighbors)> _patch = [];

    [GlobalSetup]
    public void Setup()
    {
        var loaded = ModLoader.Load(DirectoryModSource.Read(DirectoryModSource.Find(AppContext.BaseDirectory)));
        if (!loaded.IsSuccess)
        {
            throw new InvalidOperationException("The mods could not be loaded: " + string.Join("; ", loaded.Errors));
        }

        var biomes = new BiomeCatalog(loaded.Registry.OfKind("biome").Select(d => BiomeJson.Parse(d.Json)));
        var generator = new WorldGenerator(Seed, biomes);
        var generated = new Dictionary<ChunkCoord, Chunk>();
        for (var z = -Radius - 1; z <= Radius + 1; z++)
        {
            for (var x = -Radius - 1; x <= Radius + 1; x++)
            {
                generated[new ChunkCoord(x, z)] = generator.Generate(new ChunkCoord(x, z));
            }
        }

        for (var z = -Radius; z <= Radius; z++)
        {
            for (var x = -Radius; x <= Radius; x++)
            {
                _patch.Add((
                    generated[new ChunkCoord(x, z)],
                    new ChunkNeighbors(
                        generated[new ChunkCoord(x + 1, z)],
                        generated[new ChunkCoord(x - 1, z)],
                        generated[new ChunkCoord(x, z + 1)],
                        generated[new ChunkCoord(x, z - 1)])));
            }
        }
    }

    [Benchmark]
    public int LightAndMeshPatchOf25Chunks()
    {
        var vertices = 0;
        foreach (var (chunk, neighbors) in _patch)
        {
            var light = ChunkLighting.Compute(chunk, neighbors);
            vertices += ChunkMesher.Build(chunk, light, neighbors).VertexCount;
        }

        return vertices;
    }
}