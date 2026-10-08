using BenchmarkDotNet.Attributes;
using Zombies.Engine.Ecs;

namespace Zombies.Benchmarks;

public readonly record struct Position(float X, float Z);

public readonly record struct Velocity(float X, float Z);

/// <summary>Walking every entity that has a component, as a system does each tick, with the 200 zombies of the slice and ten times as many.</summary>
[MemoryDiagnoser]
public class EcsBenchmarks
{
    private EcsWorld _world = null!;

    [Params(200, 2_000)]
    public int Entities { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _world = new EcsWorld();
        for (var i = 0; i < Entities; i++)
        {
            var entity = _world.Create();
            _world.Set(entity, new Position(i, -i));
            if (i % 2 == 0)
            {
                _world.Set(entity, new Velocity(1, 1));
            }
        }
    }

    [Benchmark]
    public float QueryOneComponent()
    {
        var sum = 0f;
        foreach (var (_, position) in _world.Query<Position>())
        {
            sum += position.X + position.Z;
        }

        return sum;
    }

    [Benchmark]
    public float QueryAndLookUpAnother()
    {
        var sum = 0f;
        foreach (var (entity, position) in _world.Query<Position>())
        {
            if (_world.TryGet<Velocity>(entity, out var velocity))
            {
                sum += position.X + velocity.X;
            }
        }

        return sum;
    }
}