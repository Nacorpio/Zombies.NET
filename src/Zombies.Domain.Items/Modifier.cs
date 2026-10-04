using System.Text.Json.Serialization;

namespace Zombies.Domain.Items;

[JsonConverter(typeof(ModifierOperationJsonConverter))]
public enum ModifierOperation
{
    /// <summary>Adds the value to the Stat.</summary>
    [JsonStringEnumMemberName("add")]
    Add,

    /// <summary>Scales the Stat by the value, so 0.8 is 20% less.</summary>
    [JsonStringEnumMemberName("multiply")]
    Multiply,
}

/// <summary>Reads and writes <see cref="ModifierOperation"/> as <c>"add"</c> or <c>"multiply"</c>, never as a number.</summary>
public sealed class ModifierOperationJsonConverter() : JsonStringEnumConverter<ModifierOperation>(namingPolicy: null, allowIntegerValues: false);

/// <summary>A change to one Stat, granted by a source. See <see cref="ModifierSet"/> for how Modifiers combine.</summary>
public sealed record Modifier
{
    public Modifier(StatName stat, ModifierOperation operation, double value, ModifierSource source)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown Modifier operation.");
        }

        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A Modifier value must be a finite number.");
        }

        Stat = stat;
        Operation = operation;
        Value = value;
        Source = source;
    }

    public StatName Stat { get; }

    public ModifierOperation Operation { get; }

    public double Value { get; }

    public ModifierSource Source { get; }
}
