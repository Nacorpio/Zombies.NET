namespace Zombies.Domain.Inventory;

/// <summary>
/// Stores Containers by id. The in-memory repository hands out the live Container, so commands change it in place; a
/// persistent repository hands out a fresh copy on every <see cref="TryGet"/>, so callers keep the Container they loaded
/// and call <see cref="Save"/> when they want its state stored.
/// </summary>
public interface IContainerRepository
{
    bool TryGet(ContainerId id, out Container container);

    bool TryAdd(Container container);

    /// <summary>Stores the Container, replacing what was stored under its id.</summary>
    void Save(Container container);

    IReadOnlyList<ContainerId> Ids();
}

public sealed class InMemoryContainerRepository : IContainerRepository
{
    private readonly Dictionary<ContainerId, Container> _containers = [];

    public bool TryGet(ContainerId id, out Container container) => _containers.TryGetValue(id, out container!);

    public bool TryAdd(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return _containers.TryAdd(container.Id, container);
    }

    public void Save(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _containers[container.Id] = container;
    }

    public IReadOnlyList<ContainerId> Ids() => [.. _containers.Keys];
}