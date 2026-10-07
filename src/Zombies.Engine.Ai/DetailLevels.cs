using System.Numerics;

namespace Zombies.Engine.Ai;

/// <summary>How much of a tick a creature's mind gets. Only the nearest creatures to a player run in full.</summary>
public enum DetailLevel
{
    /// <summary>Hears sounds and walks a route it already has, a few times a second. Never looks.</summary>
    Dormant,

    /// <summary>Looks and moves less often, and follows routes and flow fields.</summary>
    Reduced,

    /// <summary>Looks and moves every tick.</summary>
    Full,
}

/// <summary>
/// The hard caps on detail: each player owns the creatures nearer to them than to any other player, and of those only the
/// <see cref="FullPerPlayer"/> nearest within <see cref="FullRadius"/> run in full and the next <see cref="ReducedPerPlayer"/> within
/// <see cref="ReducedRadius"/> run reduced. Every other creature is dormant, however many there are.
/// </summary>
public sealed record DetailCaps
{
    public int FullPerPlayer { get; init; } = 12;

    public int ReducedPerPlayer { get; init; } = 40;

    public float FullRadius { get; init; } = 48f;

    public float ReducedRadius { get; init; } = 128f;
}

/// <summary>Assigns <see cref="DetailLevel"/>s under <see cref="DetailCaps"/>. Reuses its buffers, so it allocates only when the crowd grows.</summary>
public sealed class DetailAssigner
{
    private const float DistanceScale = 1024f;
    private const int PlayerShift = 40;

    private long[] _keys = new long[256];
    private int[] _order = new int[256];

    public DetailAssigner(DetailCaps? caps = null)
    {
        Caps = caps ?? new DetailCaps();
        if (Caps.FullPerPlayer < 0 || Caps.ReducedPerPlayer < 0 || !(Caps.FullRadius >= 0) || !(Caps.ReducedRadius >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(caps), "Caps and radii cannot be negative.");
        }
    }

    public DetailCaps Caps { get; }

    /// <summary>Writes a level for each creature in <paramref name="creatures"/>, given where the players are.</summary>
    public void Assign(ReadOnlySpan<Vector3> players, ReadOnlySpan<Vector3> creatures, Span<DetailLevel> levels)
    {
        if (levels.Length < creatures.Length)
        {
            throw new ArgumentException("There must be a level for every creature.", nameof(levels));
        }

        levels[..creatures.Length].Clear();
        if (players.IsEmpty || creatures.IsEmpty)
        {
            return;
        }

        if (_keys.Length < creatures.Length)
        {
            _keys = new long[Math.Max(creatures.Length, _keys.Length * 2)];
            _order = new int[_keys.Length];
        }

        for (var i = 0; i < creatures.Length; i++)
        {
            var nearest = 0;
            var nearestDistance = float.PositiveInfinity;
            for (var p = 0; p < players.Length; p++)
            {
                var distance = Vector3.Distance(players[p], creatures[i]);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = p;
                }
            }

            var scaled = (long)Math.Min(nearestDistance * DistanceScale, (1L << PlayerShift) - 1);
            _keys[i] = ((long)nearest << PlayerShift) | scaled;
            _order[i] = i;
        }

        // Sorting by player and then distance puts each player's creatures together, nearest first.
        Array.Sort(_keys, _order, 0, creatures.Length);
        var player = -1;
        int full = 0, reduced = 0;
        for (var i = 0; i < creatures.Length; i++)
        {
            var owner = (int)(_keys[i] >> PlayerShift);
            if (owner != player)
            {
                player = owner;
                full = 0;
                reduced = 0;
            }

            var distance = (_keys[i] & ((1L << PlayerShift) - 1)) / DistanceScale;
            if (full < Caps.FullPerPlayer && distance <= Caps.FullRadius)
            {
                levels[_order[i]] = DetailLevel.Full;
                full++;
            }
            else if (reduced < Caps.ReducedPerPlayer && distance <= Caps.ReducedRadius)
            {
                levels[_order[i]] = DetailLevel.Reduced;
                reduced++;
            }
        }
    }
}
