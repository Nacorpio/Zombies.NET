namespace Zombies.Domain.Zombies;

/// <summary>The seed, type, and Level from which a zombie's appearance, outfit, and Missing parts are derived.</summary>
public readonly record struct ZombieSpec(ulong Seed, string Type, int Level);
