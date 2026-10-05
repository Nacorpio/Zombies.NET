using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Actions;

public sealed class ItemActionDefinitionException(string message) : Exception(message);

/// <summary>
/// JSON shape of an Item action definition. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="ItemActionJson"/>.
/// </summary>
public sealed record ItemActionDto
{
    /// <summary>Content ID in the form <c>namespace:item_action/name</c>, such as <c>base:item_action/drop</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Localization key of the label, such as <c>action.drop</c>.</summary>
    public required string Label { get; init; }

    /// <summary>Name of the Icon to show.</summary>
    public required string Icon { get; init; }

    /// <summary>Actions in the same group are shown together.</summary>
    public required string Group { get; init; }

    /// <summary>Where the action sits within its group.</summary>
    public int Order { get; init; }

    /// <summary>Conditions that must all hold for the action to be offered at all.</summary>
    public IReadOnlyList<ActionCondition> AppliesWhen { get; init; } = [];

    /// <summary>Conditions that must all hold for the action to be enabled.</summary>
    public IReadOnlyList<ActionCondition> EnabledWhen { get; init; } = [];

    /// <summary>Reason key shown when the action is disabled.</summary>
    public string? DisabledReason { get; init; }
}

/// <summary>Parses an Item action definition from JSON.</summary>
public static class ItemActionJson
{
    public static ItemActionDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ItemActionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ItemActionDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ItemActionDefinitionException($"Invalid Item action definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new ItemActionDefinitionException("An Item action definition must be a JSON object.");
        }

        try
        {
            return new ItemActionDefinition(dto.Id, dto.Label, dto.Icon, dto.Group, dto.Order, dto.AppliesWhen, dto.EnabledWhen, dto.DisabledReason);
        }
        catch (ArgumentException ex)
        {
            throw new ItemActionDefinitionException($"Item action '{dto.Id}' is invalid: {ex.Message}");
        }
    }
}
