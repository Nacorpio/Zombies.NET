namespace Zombies.Engine.Ecs;

/// <summary>A slow, shuffling zombie. Scales the speed of its Zombie type.</summary>
public readonly record struct Shambler(double SpeedMultiplier);

/// <summary>A zombie that runs. Scales the speed of its Zombie type.</summary>
public readonly record struct Runner(double SpeedMultiplier);

/// <summary>A zombie that bursts when it dies, hurting what stands within the radius.</summary>
public readonly record struct Bloater(double BurstRadiusMeters, double BurstDamage);

/// <summary>The Traits the base game ships. Each one is only an ECS component; what reads it is a system.</summary>
public static class BaseTraits
{
    public const string ShamblerId = "base:trait/shambler";
    public const string RunnerId = "base:trait/runner";
    public const string BloaterId = "base:trait/bloater";

    public static void Register(TraitRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ShamblerId, (world, entity, values) => world.Set(entity, new Shambler(Value(values, "speedMultiplier", 0.6))));
        registry.Register(RunnerId, (world, entity, values) => world.Set(entity, new Runner(Value(values, "speedMultiplier", 2.2))));
        registry.Register(BloaterId, (world, entity, values) => world.Set(entity, new Bloater(Value(values, "burstRadius", 3), Value(values, "burstDamage", 20))));
    }

    private static double Value(IReadOnlyDictionary<string, double> values, string name, double fallback) =>
        values.TryGetValue(name, out var value) ? value : fallback;
}
