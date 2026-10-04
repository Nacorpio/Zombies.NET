using System.Text.RegularExpressions;

namespace Zombies.Domain.Items;

/// <summary>Name of a Stat that Modifiers can change, such as <c>recoil</c> or <c>move_speed</c>.</summary>
public readonly partial record struct StatName
{
    private readonly string? _value;

    public StatName(string value)
        : this(Validated(value), validated: true)
    {
    }

    private StatName(string value, bool validated)
    {
        _ = validated;
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public static bool TryParse(string? value, out StatName name)
    {
        if (value is not null && StatNamePattern().IsMatch(value))
        {
            name = new StatName(value, validated: true);
            return true;
        }

        name = default;
        return false;
    }

    public override string ToString() => Value;

    private static string Validated(string value) =>
        TryParse(value, out _)
            ? value
            : throw new ArgumentException($"'{value}' is not a valid Stat name (expected lowercase letters, digits and '_').", nameof(value));

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex StatNamePattern();
}
