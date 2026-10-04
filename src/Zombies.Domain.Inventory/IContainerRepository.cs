namespace Zombies.Domain.Inventory;

public interface IContainerRepository
{
    bool TryGet(ContainerId id, out Container container);

    bool TryAdd(Container container);
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
}
