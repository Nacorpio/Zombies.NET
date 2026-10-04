namespace Zombies.Domain.Items;

public interface IItemCatalog
{
    bool TryGet(ItemId id, out ItemDefinition definition);
}

public sealed class ItemCatalog : IItemCatalog
{
    private readonly Dictionary<ItemId, ItemDefinition> _definitions = [];

    public ItemCatalog(IEnumerable<ItemDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException($"Duplicate Item '{definition.Id}'.", nameof(definitions));
            }
        }
    }

    public bool TryGet(ItemId id, out ItemDefinition definition) => _definitions.TryGetValue(id, out definition!);
}
