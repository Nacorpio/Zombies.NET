using System.Text.RegularExpressions;

namespace Zombies.Domain.Mods;

/// <summary>Namespaced identifier of a definition, such as <c>base:item/canned_beans</c>. The namespace is the owning mod's id.</summary>
public readonly partial record struct ContentId
{
    private readonly string? _value;

    private ContentId(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public string Namespace => Value[..Value.IndexOf(':', StringComparison.Ordinal)];

    public string Path => Value[(Value.IndexOf(':', StringComparison.Ordinal) + 1)..];

    /// <summary>First path segment, such as <c>item</c> for <c>base:item/canned_beans</c>.</summary>
    public string Kind
    {
        get
        {
            var path = Path;
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? path : path[..slash];
        }
    }

    public static bool TryParse(string? value, out ContentId id)
    {
        if (value is not null && ContentIdPattern().IsMatch(value))
        {
            id = new ContentId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9_]+:[a-z0-9_]+(/[a-z0-9_]+)*$")]
    private static partial Regex ContentIdPattern();
}
