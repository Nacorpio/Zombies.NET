using System.Text.RegularExpressions;

namespace Zombies.Domain.Items;

/// <summary>Namespaced Content ID of an Item definition, such as <c>base:item/canned_beans</c>.</summary>
public readonly partial record struct ItemId
{
    private readonly string? _value;

    public ItemId(string value)
        : this(Validated(value), validated: true)
    {
    }

    private ItemId(string value, bool validated)
    {
        _ = validated;
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public static bool TryParse(string? value, out ItemId id)
    {
        if (value is not null && ContentIdPattern().IsMatch(value))
        {
            id = new ItemId(value, validated: true);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;

    private static string Validated(string value) =>
        TryParse(value, out _)
            ? value
            : throw new ArgumentException($"'{value}' is not a valid Content ID (expected 'namespace:path').", nameof(value));

    [GeneratedRegex("^[a-z0-9_]+:[a-z0-9_]+(/[a-z0-9_]+)*$")]
    private static partial Regex ContentIdPattern();
}
