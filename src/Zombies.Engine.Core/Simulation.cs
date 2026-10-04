namespace Zombies.Engine.Core;

/// <summary>A unit of per-tick work, such as a system or a replicated world.</summary>
public interface ITickable
{
    void Tick(long tick);
}

/// <summary>Fixed-step simulation. Advances registered <see cref="ITickable"/> instances in order.</summary>
public sealed class Simulation
{
    public const int TickRateHz = 30;

    private readonly ITickable[] _tickables;

    public Simulation(params ITickable[] tickables) => _tickables = tickables;

    public long CurrentTick { get; private set; }

    public void Step()
    {
        var tickables = _tickables;
        for (var i = 0; i < tickables.Length; i++)
        {
            tickables[i].Tick(CurrentTick);
        }

        CurrentTick++;
    }

    public void Run(long ticks)
    {
        for (long i = 0; i < ticks; i++)
        {
            Step();
        }
    }
}
