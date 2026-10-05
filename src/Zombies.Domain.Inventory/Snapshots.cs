namespace Zombies.Domain.Inventory;

/// <summary>One Stack as saved: its id within the Container, the Item's Content ID, and how many.</summary>
public sealed record StackSnapshot(int Id, string Item, int Count);

/// <summary>Everything needed to rebuild a <see cref="Container"/>. Limits are in kilograms and cubic meters.</summary>
public sealed record ContainerSnapshot(long Id, double MassLimitKg, double VolumeLimitM3, int NextStackId, IReadOnlyList<StackSnapshot> Stacks);

/// <summary>One worn Item as saved, with its Wear state.</summary>
public sealed record WornSnapshot(string Item, double Wetness, double Condition);

/// <summary>Everything needed to rebuild an <see cref="Outfit"/>, in the order the items were put on.</summary>
public sealed record OutfitSnapshot(IReadOnlyList<WornSnapshot> Worn);
