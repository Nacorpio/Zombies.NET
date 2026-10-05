namespace Zombies.Engine.Ecs;

/// <summary>A handle to something in an <see cref="EcsWorld"/>. A destroyed entity's handle goes stale and is never mistaken for a later one.</summary>
public readonly record struct Entity(int Index, int Generation)
{
    public static Entity None => default;

    public bool IsNone => Generation == 0;
}

/// <summary>
/// A small entity-component store for per-frame simulation (ADR 0002). An entity is a handle, a component is a plain struct,
/// and anything, including a mod, can add a component type without this class knowing it. Domain aggregates stay outside it.
/// </summary>
public sealed class EcsWorld
{
    private interface IStore
    {
        void Remove(int index);
    }

    private sealed class Store<T> : IStore
        where T : struct
    {
        private readonly Dictionary<int, int> _slotOf = [];
        private readonly List<int> _owners = [];
        private readonly List<T> _values = [];

        public int Count => _owners.Count;

        public int OwnerAt(int slot) => _owners[slot];

        public T ValueAt(int slot) => _values[slot];

        public bool Has(int index) => _slotOf.ContainsKey(index);

        public void Set(int index, T value)
        {
            if (_slotOf.TryGetValue(index, out var slot))
            {
                _values[slot] = value;
                return;
            }

            _slotOf[index] = _owners.Count;
            _owners.Add(index);
            _values.Add(value);
        }

        public bool TryGet(int index, out T value)
        {
            if (_slotOf.TryGetValue(index, out var slot))
            {
                value = _values[slot];
                return true;
            }

            value = default;
            return false;
        }

        public void Remove(int index)
        {
            if (!_slotOf.Remove(index, out var slot))
            {
                return;
            }

            var last = _owners.Count - 1;
            if (slot != last)
            {
                _owners[slot] = _owners[last];
                _values[slot] = _values[last];
                _slotOf[_owners[slot]] = slot;
            }

            _owners.RemoveAt(last);
            _values.RemoveAt(last);
        }
    }

    private readonly List<int> _generations = [0];
    private readonly Stack<int> _free = [];
    private readonly Dictionary<Type, IStore> _stores = [];

    /// <summary>How many entities are alive.</summary>
    public int Count { get; private set; }

    public Entity Create()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else
        {
            index = _generations.Count;
            _generations.Add(0);
        }

        _generations[index]++;
        Count++;
        return new Entity(index, _generations[index]);
    }

    /// <summary>A slot's generation is odd while an entity lives in it and goes up again when it is destroyed, so old handles never match.</summary>
    public bool IsAlive(Entity entity) =>
        entity.Index > 0 && entity.Index < _generations.Count && _generations[entity.Index] == entity.Generation && (entity.Generation & 1) == 1;

    /// <summary>Destroys an entity and every component on it. False when the handle is stale.</summary>
    public bool Destroy(Entity entity)
    {
        if (!IsAlive(entity))
        {
            return false;
        }

        foreach (var store in _stores.Values)
        {
            store.Remove(entity.Index);
        }

        _generations[entity.Index]++;
        _free.Push(entity.Index);
        Count--;
        return true;
    }

    /// <summary>Adds a component to an entity, or replaces the one it has.</summary>
    public void Set<T>(Entity entity, T component)
        where T : struct
    {
        RequireAlive(entity);
        StoreOf<T>().Set(entity.Index, component);
    }

    public bool Has<T>(Entity entity)
        where T : struct =>
        IsAlive(entity) && _stores.TryGetValue(typeof(T), out var store) && ((Store<T>)store).Has(entity.Index);

    public bool TryGet<T>(Entity entity, out T component)
        where T : struct
    {
        if (IsAlive(entity) && _stores.TryGetValue(typeof(T), out var store))
        {
            return ((Store<T>)store).TryGet(entity.Index, out component);
        }

        component = default;
        return false;
    }

    public bool Remove<T>(Entity entity)
        where T : struct
    {
        if (!Has<T>(entity))
        {
            return false;
        }

        ((Store<T>)_stores[typeof(T)]).Remove(entity.Index);
        return true;
    }

    /// <summary>Every entity that has a component of type <typeparamref name="T"/>, with its value.</summary>
    public IEnumerable<(Entity Entity, T Component)> Query<T>()
        where T : struct
    {
        if (!_stores.TryGetValue(typeof(T), out var untyped))
        {
            yield break;
        }

        var store = (Store<T>)untyped;
        for (var slot = 0; slot < store.Count; slot++)
        {
            var index = store.OwnerAt(slot);
            yield return (new Entity(index, _generations[index]), store.ValueAt(slot));
        }
    }

    private Store<T> StoreOf<T>()
        where T : struct
    {
        if (!_stores.TryGetValue(typeof(T), out var store))
        {
            store = new Store<T>();
            _stores[typeof(T)] = store;
        }

        return (Store<T>)store;
    }

    private void RequireAlive(Entity entity)
    {
        if (!IsAlive(entity))
        {
            throw new ArgumentException("The entity was destroyed or never created.", nameof(entity));
        }
    }
}
