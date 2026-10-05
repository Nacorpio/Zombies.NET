using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Mods;

/// <summary>
/// JSON shape of a <c>migration</c> definition: where a Content ID that a mod renamed or removed has gone.
/// Give <see cref="To"/> for a rename, or set <see cref="Removed"/> when nothing replaces it.
/// This type is the source of the generated JSON Schema, so keep it in step with <see cref="ContentIdMigrations"/>.
/// </summary>
public sealed record MigrationDto
{
    /// <summary>Content ID of this migration, in its mod's namespace, such as <c>mymod:migration/rusty_knife</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The old Content ID that saves may still hold, such as <c>mymod:item/rusty_knife</c>.</summary>
    public required string From { get; init; }

    /// <summary>The Content ID that replaces it. Leave out when <see cref="Removed"/> is true.</summary>
    public string? To { get; init; }

    /// <summary>Whether the old Content ID is gone for good, so saved things that use it are dropped. Defaults to false.</summary>
    public bool Removed { get; init; }
}

/// <summary>
/// Where every renamed or removed Content ID went, built from the <c>migration</c> definitions of the loaded mods.
/// A chain such as A to B to C resolves straight to C.
/// </summary>
public sealed class ContentIdMigrations
{
    public const string Kind = "migration";

    public static readonly ContentIdMigrations Empty = new([]);

    private readonly Dictionary<string, string?> _resolved;

    private ContentIdMigrations(Dictionary<string, string?> resolved) => _resolved = resolved;

    public int Count => _resolved.Count;

    /// <returns>The Content ID that replaces <paramref name="id"/>, the same id when no migration applies, or null when it was removed.</returns>
    public string? Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _resolved.TryGetValue(id, out var target) ? target : id;
    }

    /// <summary>Reads every migration in <paramref name="definitions"/>, adds a <see cref="ModLoadError"/> for each that is invalid or loops back on itself, and flattens the chains.</summary>
    internal static ContentIdMigrations Build(IEnumerable<Definition> definitions, List<ModLoadError> errors)
    {
        var steps = new Dictionary<ContentId, ContentId?>();
        var definedBy = new Dictionary<ContentId, string>();
        foreach (var definition in definitions.Where(d => string.Equals(d.Id.Kind, Kind, StringComparison.Ordinal)).OrderBy(d => d.Id.Value, StringComparer.Ordinal))
        {
            if (!TryRead(definition, out var from, out var to, out var problem))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.InvalidDefinition, definition.DefinedBy, null, $"Migration '{definition.Id}' {problem}"));
            }
            else if (!steps.TryAdd(from, to))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.DuplicateMigration, definition.DefinedBy, null, $"'{from}' is already migrated by '{definedBy[from]}'; '{definition.Id}' cannot migrate it again."));
            }
            else
            {
                definedBy[from] = definition.DefinedBy;
            }
        }

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        var looped = false;
        foreach (var from in steps.Keys.OrderBy(k => k.Value, StringComparer.Ordinal))
        {
            if (Follow(from, steps, out var end, out var cycle))
            {
                resolved[from.Value] = end?.Value;
                continue;
            }

            looped = true;

            // Every member of a cycle finds it, and so does every chain that leads into it; report it once, from its smallest member.
            if (cycle[0] == from && cycle.Select(c => c.Value).Order(StringComparer.Ordinal).First() == from.Value)
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.MigrationCycle, definedBy[from], null, $"Migrations form a cycle: {string.Join(" -> ", cycle)} -> {cycle[0]}."));
            }
        }

        return looped ? Empty : new ContentIdMigrations(resolved);
    }

    /// <summary>Walks from <paramref name="from"/> to where it ends up, which is null when removed. Fails with the cycle it ran into.</summary>
    private static bool Follow(ContentId from, Dictionary<ContentId, ContentId?> steps, out ContentId? end, out List<ContentId> cycle)
    {
        var chain = new List<ContentId> { from };
        var current = from;
        while (steps.TryGetValue(current, out var next))
        {
            if (next is not { } following)
            {
                end = null;
                cycle = [];
                return true;
            }

            if (chain.Contains(following))
            {
                end = null;
                cycle = chain[chain.IndexOf(following)..];
                return false;
            }

            chain.Add(following);
            current = following;
        }

        end = current;
        cycle = [];
        return true;
    }

    private static bool TryRead(Definition definition, out ContentId from, out ContentId? to, out string problem)
    {
        from = default;
        to = null;
        MigrationDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MigrationDto>(definition.Json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            problem = $"is not valid: {ex.Message}";
            return false;
        }

        if (dto is null || !ContentId.TryParse(dto.From, out from))
        {
            problem = "needs 'from' to be a Content ID.";
            return false;
        }

        if (dto.Removed == (dto.To is not null))
        {
            problem = "needs exactly one of 'to' and 'removed'.";
            return false;
        }

        if (dto.To is not null)
        {
            if (!ContentId.TryParse(dto.To, out var target))
            {
                problem = "needs 'to' to be a Content ID.";
                return false;
            }

            to = target;
        }

        problem = string.Empty;
        return true;
    }
}
