namespace Zombies.Domain.Inventory;

/// <summary>The Item state of one Stack as saved: its named values and the Items attached to it, as Content IDs.</summary>
public sealed record ItemStateSnapshot(IReadOnlyList<KeyValuePair<string, int>> Values, IReadOnlyList<KeyValuePair<string, int>> Attached);

/// <summary>One Stack as saved: its id within the Container, the Item's Content ID, how many, and its Item state if it has one.</summary>
public sealed record StackSnapshot(int Id, string Item, int Count, ItemStateSnapshot? State = null);

/// <summary>Everything needed to rebuild a <see cref="Container"/>. Limits are in kilograms and cubic meters.</summary>
public sealed record ContainerSnapshot(long Id, double MassLimitKg, double VolumeLimitM3, int NextStackId, IReadOnlyList<StackSnapshot> Stacks);

/// <summary>One worn Item as saved, with its Wear state and the Content IDs of its Faults, if any.</summary>
public sealed record WornSnapshot(string Item, double Wetness, double Condition, IReadOnlyList<string>? Faults = null);

/// <summary>Everything needed to rebuild an <see cref="Outfit"/>, in the order the items were put on.</summary>
public sealed record OutfitSnapshot(IReadOnlyList<WornSnapshot> Worn);
