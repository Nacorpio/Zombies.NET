using UnitsNet;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Engine.Net;

namespace Zombies.Client;

/// <summary>
/// The Containers the Server says this player can see, as an <see cref="IContainerRepository"/> the inventory screen reads.
/// It is a read-only mirror: the Server owns the truth, and every change goes through a command whose answer replaces this.
/// </summary>
internal sealed class ReplicatedContainerRepository(IItemCatalog catalog) : IContainerRepository
{
    private readonly Dictionary<ContainerId, Container> _containers = [];

    /// <summary>Rebuilds the mirror from the Server's view. A Container the Server no longer sends is dropped.</summary>
    public void Apply(ContainerView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _containers.Clear();
        foreach (var replicated in view.Containers)
        {
            var container = new Container(replicated.Id, Mass.FromKilograms(40), Volume.FromLiters(60), catalog);
            foreach (var stack in replicated.Stacks)
            {
                container.TryAdd(stack.Item, stack.Count, stack.State);
            }

            _containers[replicated.Id] = container;
        }
    }

    public bool TryGet(ContainerId id, out Container found) => _containers.TryGetValue(id, out found!);

    /// <summary>Always false: the Server owns which Containers exist, so a client cannot add one.</summary>
    public bool TryAdd(Container added) => false;

    /// <summary>Does nothing: the Server owns the Containers, so a local change is discarded when the next view arrives.</summary>
    public void Save(Container saved)
    {
    }

    public IReadOnlyList<ContainerId> Ids() => [.. _containers.Keys];
}
