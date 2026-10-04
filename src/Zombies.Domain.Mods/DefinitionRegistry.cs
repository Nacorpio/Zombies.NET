namespace Zombies.Domain.Mods;

/// <summary>A final definition after every mod has been applied: its JSON, who defined it, and who changed it.</summary>
public sealed record Definition(ContentId Id, string Json, string DefinedBy, IReadOnlyList<string> ModifiedBy);

public sealed class DefinitionRegistry
{
    public static readonly DefinitionRegistry Empty = new([]);

    private readonly Dictionary<ContentId, Definition> _definitions;

    internal DefinitionRegistry(IEnumerable<Definition> definitions) =>
        _definitions = definitions.ToDictionary(d => d.Id);

    public int Count => _definitions.Count;

    /// <summary>Every definition, ordered by Content ID.</summary>
    public IReadOnlyList<Definition> Definitions =>
        [.. _definitions.Values.OrderBy(d => d.Id.Value, StringComparer.Ordinal)];

    public bool TryGet(ContentId id, out Definition definition) => _definitions.TryGetValue(id, out definition!);

    /// <summary>Definitions whose Content ID has the given first path segment, such as <c>item</c>.</summary>
    public IReadOnlyList<Definition> OfKind(string kind) =>
        [.. Definitions.Where(d => string.Equals(d.Id.Kind, kind, StringComparison.Ordinal))];
}
