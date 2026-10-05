using System.Text.Json.Serialization;
using Zombies.Domain.Items;

namespace Zombies.Domain.Actions;

/// <summary>
/// A rule an Item action is checked against. The first group decides whether the action makes sense for a Stack at all;
/// the second decides whether it can run right now.
/// </summary>
[JsonConverter(typeof(ActionConditionJsonConverter))]
public enum ActionCondition
{
    /// <summary>The Stack holds more than one item.</summary>
    [JsonStringEnumMemberName("stack_count_above_one")]
    StackCountAboveOne,

    /// <summary>The item can be worn.</summary>
    [JsonStringEnumMemberName("wearable")]
    Wearable,

    /// <summary>The item is a weapon.</summary>
    [JsonStringEnumMemberName("weapon")]
    Weapon,

    /// <summary>The item can be eaten.</summary>
    [JsonStringEnumMemberName("edible")]
    Edible,

    /// <summary>The item can be drunk.</summary>
    [JsonStringEnumMemberName("drinkable")]
    Drinkable,

    /// <summary>The item can be eaten or drunk.</summary>
    [JsonStringEnumMemberName("consumable")]
    Consumable,

    /// <summary>The Container belongs to the player.</summary>
    [JsonStringEnumMemberName("own_container")]
    OwnContainer,

    /// <summary>The Container can be changed.</summary>
    [JsonStringEnumMemberName("writable_container")]
    WritableContainer,
}

public sealed class ActionConditionJsonConverter() : JsonStringEnumConverter<ActionCondition>(namingPolicy: null, allowIntegerValues: false);

/// <summary>Definition of an Item action: what a player can do with a Stack.</summary>
public sealed record ItemActionDefinition
{
    private static readonly ActionCondition[] Applicability =
    [
        ActionCondition.StackCountAboveOne,
        ActionCondition.Wearable,
        ActionCondition.Weapon,
        ActionCondition.Edible,
        ActionCondition.Drinkable,
        ActionCondition.Consumable,
    ];

    private static readonly ActionCondition[] Availability =
    [
        ActionCondition.OwnContainer,
        ActionCondition.WritableContainer,
    ];

    public ItemActionDefinition(
        string id,
        string label,
        string icon,
        string group,
        int order,
        IEnumerable<ActionCondition>? appliesWhen = null,
        IEnumerable<ActionCondition>? enabledWhen = null,
        string? disabledReason = null)
    {
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        if (!StatName.TryParse(icon, out _))
        {
            throw new ArgumentException($"'{icon}' is not a valid Icon name (expected lowercase letters, digits and '_').", nameof(icon));
        }

        AppliesWhen = [.. appliesWhen ?? []];
        EnabledWhen = [.. enabledWhen ?? []];
        foreach (var condition in AppliesWhen.Where(c => !Applicability.Contains(c)))
        {
            throw new ArgumentException($"'{condition}' decides whether an action can run now, so it belongs in 'enabledWhen'.", nameof(appliesWhen));
        }

        foreach (var condition in EnabledWhen.Where(c => !Availability.Contains(c)))
        {
            throw new ArgumentException($"'{condition}' decides whether an action makes sense, so it belongs in 'appliesWhen'.", nameof(enabledWhen));
        }

        Id = id;
        Label = label;
        Icon = icon;
        Group = group;
        Order = order;
        DisabledReason = disabledReason;
    }

    public string Id { get; }

    /// <summary>Localization key of the label, such as <c>action.drop</c>.</summary>
    public string Label { get; }

    /// <summary>Name of the Icon to show.</summary>
    public string Icon { get; }

    /// <summary>Actions in the same group are shown together.</summary>
    public string Group { get; }

    public int Order { get; }

    /// <summary>Conditions that must all hold for the action to be offered at all.</summary>
    public IReadOnlyList<ActionCondition> AppliesWhen { get; }

    /// <summary>Conditions that must all hold for the action to be enabled. When one fails, the action is shown disabled.</summary>
    public IReadOnlyList<ActionCondition> EnabledWhen { get; }

    /// <summary>Reason key shown when the action is disabled, or null to use the failing condition's default.</summary>
    public string? DisabledReason { get; }

    /// <summary>The reason key for a condition that is not met.</summary>
    public static string ReasonFor(ActionCondition condition) => condition switch
    {
        ActionCondition.OwnContainer => "not_your_container",
        ActionCondition.WritableContainer => "container_read_only",
        _ => "not_available",
    };
}

/// <summary>The Item actions the game knows.</summary>
public sealed class ItemActionCatalog
{
    private readonly Dictionary<string, ItemActionDefinition> _definitions = [];

    public ItemActionCatalog(IEnumerable<ItemActionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException($"Item action '{definition.Id}' is defined more than once.", nameof(definitions));
            }
        }
    }

    public IReadOnlyCollection<ItemActionDefinition> All => _definitions.Values;

    public bool TryGet(string id, out ItemActionDefinition definition) => _definitions.TryGetValue(id, out definition!);
}
