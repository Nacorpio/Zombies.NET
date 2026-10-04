using System.Numerics;

namespace Zombies.Engine.Net;

/// <summary>
/// The Server's replicated entities, kept sorted by id so snapshots diff in one pass. Ids are never reused.
/// Later milestones move entities onto the ECS; this stays the replication view.
/// </summary>
public sealed class ServerWorld
{
    private readonly Dictionary<uint, int> _indexById = [];
    private EntityState[] _entities = new EntityState[64];
    private uint _nextId = 1;

    public int Count { get; private set; }

    public ReadOnlySpan<EntityState> Entities => _entities.AsSpan(0, Count);

    public uint Spawn(ushort kind, Vector3 position, float yaw)
    {
        if (Count == _entities.Length)
        {
            Array.Resize(ref _entities, _entities.Length * 2);
        }

        var id = _nextId++;
        _entities[Count] = new EntityState(id, kind, position, yaw);
        _indexById[id] = Count;
        Count++;
        return id;
    }

    public bool Despawn(uint id)
    {
        if (!_indexById.Remove(id, out var index))
        {
            return false;
        }

        Array.Copy(_entities, index + 1, _entities, index, Count - index - 1);
        Count--;
        for (var i = index; i < Count; i++)
        {
            _indexById[_entities[i].Id] = i;
        }

        return true;
    }

    public bool TryGet(uint id, out EntityState state)
    {
        if (_indexById.TryGetValue(id, out var index))
        {
            state = _entities[index];
            return true;
        }

        state = default;
        return false;
    }

    public void Move(uint id, Vector3 position, float yaw)
    {
        if (!_indexById.TryGetValue(id, out var index))
        {
            throw new KeyNotFoundException($"No entity has id {id}.");
        }

        _entities[index] = _entities[index] with { Position = position, Yaw = yaw };
    }
}
