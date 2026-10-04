using Zombies.Engine.Core;

namespace Zombies.Engine.Tests;

public sealed class SimulationTests
{
    private sealed class CountingSystem : ITickable
    {
        public long Count;
        public long LastTick = -1;

        public void Tick(long tick)
        {
            Count++;
            LastTick = tick;
        }
    }

    private sealed class AllocatingSystem : ITickable
    {
        public object? Sink;

        public void Tick(long tick) => Sink = new byte[128];
    }

    [Fact]
    public void Run_AdvancesEveryTickableOncePerTick()
    {
        var system = new CountingSystem();
        var simulation = new Simulation(system);

        simulation.Run(10);

        Assert.Equal(10, simulation.CurrentTick);
        Assert.Equal(10, system.Count);
        Assert.Equal(9, system.LastTick);
    }

    [Fact]
    public void SteadyStateTick_OfNonAllocatingSystem_AllocatesNothing()
    {
        var simulation = new Simulation(new CountingSystem());

        var allocated = AllocationProbe.MeasureSteadyState(simulation, warmupTicks: 30, ticks: 300);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void AllocationProbe_DetectsAllocatingSystem()
    {
        var simulation = new Simulation(new AllocatingSystem());

        var allocated = AllocationProbe.MeasureSteadyState(simulation, warmupTicks: 30, ticks: 300);

        Assert.True(allocated > 0, "probe must fail a tick that allocates");
    }
}

public sealed class FixedStepClockTests
{
    [Fact]
    public void Advance_RunsOneTickPerThirtiethOfASecond_AndCarriesTheRest()
    {
        var clock = new FixedStepClock();

        Assert.Equal(0, clock.Advance(TimeSpan.FromMilliseconds(20)));
        Assert.Equal(1, clock.Advance(TimeSpan.FromMilliseconds(20)));
        Assert.Equal(30, Enumerable.Range(0, 60).Sum(_ => clock.Advance(TimeSpan.FromSeconds(1.0 / 60))));
    }

    [Fact]
    public void Advance_AfterAStall_RunsAtMostACappedNumberOfTicks()
    {
        var clock = new FixedStepClock();

        Assert.Equal(FixedStepClock.MaxTicksPerAdvance, clock.Advance(TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.Zero, clock.Carry);
    }
}
