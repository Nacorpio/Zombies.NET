namespace Zombies.Engine.Core;

/// <summary>
/// Turns real elapsed time into a whole number of <see cref="Simulation"/> ticks, carrying the remainder to the next call.
/// After a long stall it runs at most <see cref="MaxTicksPerAdvance"/> ticks and drops the rest, so a slow machine
/// falls behind instead of spiralling.
/// </summary>
public sealed class FixedStepClock
{
    public const int MaxTicksPerAdvance = 5;

    private static readonly TimeSpan Step = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Simulation.TickRateHz);

    private TimeSpan _carry;

    /// <summary>Time still owed to the next tick.</summary>
    public TimeSpan Carry => _carry;

    public int Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Elapsed time cannot be negative.");
        }

        _carry += elapsed;
        var ticks = (int)Math.Min(_carry.Ticks / Step.Ticks, MaxTicksPerAdvance);
        _carry = ticks == MaxTicksPerAdvance ? TimeSpan.Zero : _carry - (Step * ticks);
        return ticks;
    }
}
