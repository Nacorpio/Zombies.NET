using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zombies.Domain.Items;

public sealed class ModifierDefinitionException(string message) : Exception(message);

/// <summary>Definition of a Modifier: the Stat it changes and how. The source is given when it is applied.</summary>
public sealed record ModifierDefinition(string Id, StatName Stat, ModifierOperation Operation, double Value)
{
    /// <summary>The Modifier this definition grants when applied by <paramref name="source"/>.</summary>
    public Modifier From(ModifierSource source) => new(Stat, Operation, Value, source);
}

/// <summary>
/// JSON shape of a Modifier definition.
/// This type is the source of the generated JSON Schema, so keep it in step with <see cref="ModifierDefinitionJson"/>.
/// </summary>
public sealed record ModifierDefinitionDto
{
    /// <summary>Content ID in the form <c>namespace:modifier/name</c>, such as <c>base:modifier/suppressor_recoil</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Name of the Stat to change, such as <c>recoil</c>.</summary>
    public required string Stat { get; init; }

    /// <summary><c>add</c> to add the value, or <c>multiply</c> to scale the Stat by it.</summary>
    public required ModifierOperation Operation { get; init; }

    public required double Value { get; init; }
}

/// <summary>Parses a Modifier definition from JSON.</summary>
public static partial class ModifierDefinitionJson
{
    public static ModifierDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ModifierDefinitionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ModifierDefinitionDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ModifierDefinitionException($"Invalid Modifier definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new ModifierDefinitionException("A Modifier definition must be a JSON object.");
        }

        if (!ContentIdPattern().IsMatch(dto.Id))
        {
            throw new ModifierDefinitionException($"'id' value '{dto.Id}' is not a valid Content ID.");
        }

        if (!StatName.TryParse(dto.Stat, out var stat))
        {
            throw new ModifierDefinitionException($"Modifier '{dto.Id}' has an invalid 'stat' value '{dto.Stat}'.");
        }

        return new ModifierDefinition(dto.Id, stat, dto.Operation, dto.Value);
    }

    [GeneratedRegex("^[a-z0-9_]+:[a-z0-9_]+(/[a-z0-9_]+)*$")]
    private static partial Regex ContentIdPattern();
}
