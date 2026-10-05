using Zombies.Domain.Items;

namespace Zombies.Domain.Statistics;

// Facts about a player's run that no other context owns. The Server raises them for the player they happened to.

/// <summary>The player killed a zombie.</summary>
public sealed record ZombieKilled : IDomainEvent
{
    public static ZombieKilled Instance { get; } = new();
}

/// <summary>The player took items out of a Corpse into what they carry.</summary>
public sealed record ItemsLooted(int Count) : IDomainEvent;

/// <summary>
/// The player walked another <see cref="StepMeters"/> metres. The Server raises <see cref="Step"/> over and over, so
/// counting walked distance costs no allocation.
/// </summary>
public sealed record DistanceWalked(int Meters) : IDomainEvent
{
    public const int StepMeters = 10;

    public static DistanceWalked Step { get; } = new(StepMeters);
}

/// <summary>The player's Body lived through another full day.</summary>
public sealed record DaySurvived : IDomainEvent
{
    public static DaySurvived Instance { get; } = new();
}
