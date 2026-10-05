using System.Collections.Immutable;

namespace Zombies.Domain.Items;

/// <summary>
/// Immutable state carried by one particular Item, such as a weapon's condition or a magazine's rounds, plus the Items attached to it.
/// Two states are equal when they hold the same values and attachments.
/// </summary>
public sealed class ItemState : IEquatable<ItemState>
{
    private ItemState(ImmutableSortedDictionary<string, int> values, ImmutableSortedDictionary<ItemId, int> attached)
    {
        Values = values;
        Attached = attached;
    }

    /// <summary>Named numeric values, for example <c>condition</c> or <c>rounds</c>.</summary>
    public IReadOnlyDictionary<string, int> Values { get; }

    /// <summary>Items attached to this one and how many of each, per unit of the owning Item.</summary>
    public IReadOnlyDictionary<ItemId, int> Attached { get; }

    public bool IsEmpty => Values.Count == 0 && Attached.Count == 0;

    public static ItemState Create(
        IEnumerable<KeyValuePair<string, int>>? values = null,
        IEnumerable<KeyValuePair<ItemId, int>>? attached = null)
    {
        var sortedValues = (values ?? []).ToImmutableSortedDictionary(StringComparer.Ordinal);
        var sortedAttached = ImmutableSortedDictionary.CreateRange(
            Comparer<ItemId>.Create((a, b) => string.CompareOrdinal(a.Value, b.Value)),
            attached ?? []);

        foreach (var count in sortedAttached.Values)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        }

        return new ItemState(sortedValues, sortedAttached);
    }

    public bool Equals(ItemState? other) =>
        other is not null
        && Values.SequenceEqual(other.Values)
        && Attached.SequenceEqual(other.Attached);

    public override bool Equals(object? obj) => Equals(obj as ItemState);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (name, value) in Values)
        {
            hash.Add(name, StringComparer.Ordinal);
            hash.Add(value);
        }

        foreach (var (item, count) in Attached)
        {
            hash.Add(item);
            hash.Add(count);
        }

        return hash.ToHashCode();
    }

    public override string ToString() =>
        $"ItemState({string.Join(", ", Values.Select(v => $"{v.Key}={v.Value}"))}; attached: {string.Join(", ", Attached.Select(a => $"{a.Key}x{a.Value}"))})";
}
