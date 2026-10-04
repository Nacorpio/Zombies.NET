namespace Zombies.Engine.Core;

/// <summary>Measures managed bytes allocated on the calling thread by a piece of work.</summary>
public static class AllocationProbe
{
    public static long Measure(Action work)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Warms up, then returns bytes allocated by <paramref name="ticks"/> steady-state ticks.</summary>
    public static long MeasureSteadyState(Simulation simulation, long warmupTicks, long ticks)
    {
        simulation.Run(warmupTicks);
        return Measure(() => simulation.Run(ticks));
    }
}
