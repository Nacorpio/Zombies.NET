using Zombies.Domain.Items;

namespace Zombies.Domain.Crafting;

/// <summary>A loot table an Area type can use, and how Danger level shifts its chance against the others.</summary>
public sealed record WeightedLootTable
{
    public WeightedLootTable(string table, int weight, int dangerWeightShift = 0)
    {
        if (!ItemId.TryParse(table, out _))
        {
            throw new ArgumentException($"'{table}' is not a valid Content ID.", nameof(table));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 1);
        Table = table;
        Weight = weight;
        DangerWeightShift = dangerWeightShift;
    }

    public string Table { get; }

    public int Weight { get; }

    /// <summary>Added to the weight for each point of Danger level. Negative values make the table rarer as danger rises.</summary>
    public int DangerWeightShift { get; }

    /// <summary>The weight at a Danger level. Zero means the table cannot be chosen at all.</summary>
    public int EffectiveWeight(int dangerLevel) => Math.Max(0, Weight + (DangerWeightShift * dangerLevel));
}

/// <summary>Which loot tables a container kind uses in an Area type.</summary>
public sealed record AreaLootRule
{
    public AreaLootRule(string containerKind, IEnumerable<WeightedLootTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (!StatName.TryParse(containerKind, out _))
        {
            throw new ArgumentException($"'{containerKind}' is not a valid container kind (expected lowercase letters, digits and '_').", nameof(containerKind));
        }

        var list = tables.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException($"Container kind '{containerKind}' needs at least one loot table.", nameof(tables));
        }

        ContainerKind = containerKind;
        Tables = list;
    }

    public string ContainerKind { get; }

    public IReadOnlyList<WeightedLootTable> Tables { get; }
}

/// <summary>Definition of an Area type: which loot tables each container kind uses, shifted by Danger level.</summary>
public sealed record AreaTypeDefinition
{
    public AreaTypeDefinition(string id, IEnumerable<AreaLootRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        var list = rules.ToList();
        if (list.Select(r => r.ContainerKind).Distinct().Count() != list.Count)
        {
            throw new ArgumentException($"Area type '{id}' has more than one rule for the same container kind.", nameof(rules));
        }

        Id = id;
        Rules = list;
    }

    public string Id { get; }

    public IReadOnlyList<AreaLootRule> Rules { get; }

    public bool TryGetRule(string containerKind, out AreaLootRule rule)
    {
        rule = Rules.FirstOrDefault(r => r.ContainerKind == containerKind)!;
        return rule is not null;
    }
}

/// <summary>The Area types the game knows.</summary>
public sealed class AreaTypeCatalog
{
    private readonly Dictionary<string, AreaTypeDefinition> _definitions = new(StringComparer.Ordinal);

    public AreaTypeCatalog(IEnumerable<AreaTypeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException($"Area type '{definition.Id}' is defined more than once.", nameof(definitions));
            }
        }
    }

    public IReadOnlyCollection<AreaTypeDefinition> All => _definitions.Values;

    public bool TryGet(string id, out AreaTypeDefinition definition) => _definitions.TryGetValue(id, out definition!);
}
