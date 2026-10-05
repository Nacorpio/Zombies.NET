namespace Zombies.Engine.Ai;

public enum DoorState
{
    Closed,
    Open,
    Broken,
}

/// <summary>
/// A door two blocks high standing in a doorway, with its bottom block at <see cref="Cell"/>. A closed door stops a creature
/// and blocks sight; a zombie that wants to pass hits it until it breaks. A broken door stays open for good.
/// </summary>
public sealed class Door
{
    internal Door(int id, NavCell cell, double health)
    {
        Id = id;
        Cell = cell;
        MaxHealth = health;
        Health = health;
    }

    public int Id { get; }

    public NavCell Cell { get; }

    public double MaxHealth { get; }

    public double Health { get; internal set; }

    public DoorState State { get; internal set; }

    public bool IsClosed => State == DoorState.Closed;

    /// <summary>Opens the door. False when it is broken.</summary>
    public bool Open()
    {
        if (State == DoorState.Broken)
        {
            return false;
        }

        State = DoorState.Open;
        return true;
    }

    /// <summary>Closes the door. False when it is broken, which cannot close again.</summary>
    public bool Close()
    {
        if (State == DoorState.Broken)
        {
            return false;
        }

        State = DoorState.Closed;
        return true;
    }
}

/// <summary>The doors in the world, by the blocks they fill. Navigation plans through a closed door at a cost, because it can be broken.</summary>
public sealed class DoorMap
{
    public const int DoorHeight = 2;

    private readonly Dictionary<long, Door> _byCell = [];
    private readonly List<Door> _doors = [];

    public int Count => _doors.Count;

    public IReadOnlyList<Door> All => _doors;

    /// <summary>Raised once when a door breaks.</summary>
    public event Action<Door>? Broken;

    /// <summary>Places a closed door whose bottom block is at the given position.</summary>
    /// <exception cref="InvalidOperationException">Another door already fills one of its blocks.</exception>
    public Door Add(int x, int y, int z, double health)
    {
        if (!double.IsFinite(health) || health <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(health), "A door needs health above zero.");
        }

        for (var dy = 0; dy < DoorHeight; dy++)
        {
            if (_byCell.ContainsKey(new NavCell(x, y + dy, z).Key))
            {
                throw new InvalidOperationException($"A door already stands at {x},{y + dy},{z}.");
            }
        }

        var door = new Door(_doors.Count + 1, new NavCell(x, y, z), health);
        _doors.Add(door);
        for (var dy = 0; dy < DoorHeight; dy++)
        {
            _byCell[new NavCell(x, y + dy, z).Key] = door;
        }

        return door;
    }

    public bool TryGetAt(int x, int y, int z, out Door door) => _byCell.TryGetValue(new NavCell(x, y, z).Key, out door!);

    /// <summary>Whether a closed door fills the block.</summary>
    public bool IsClosedAt(int x, int y, int z) => _byCell.Count > 0 && _byCell.TryGetValue(new NavCell(x, y, z).Key, out var door) && door.IsClosed;

    /// <summary>Takes health from a closed door. True when this blow broke it.</summary>
    public bool Damage(Door door, double amount)
    {
        ArgumentNullException.ThrowIfNull(door);
        if (!door.IsClosed || !double.IsFinite(amount) || amount <= 0)
        {
            return false;
        }

        door.Health = Math.Max(0, door.Health - amount);
        if (door.Health > 0)
        {
            return false;
        }

        door.State = DoorState.Broken;
        Broken?.Invoke(door);
        return true;
    }
}
