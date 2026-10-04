namespace Zombies.Domain.Items;

/// <summary>
/// What granted a Modifier, such as a fitted Attachment or a running Status effect.
/// Removing the source removes every Modifier it granted.
/// </summary>
public readonly record struct ModifierSource
{
    private readonly string? _value;

    public ModifierSource(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public override string ToString() => Value;
}
