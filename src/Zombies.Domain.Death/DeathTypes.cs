using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;

namespace Zombies.Domain.Death;

/// <summary>What the Server does with a player whose Body died.</summary>
public enum DeathPolicy
{
    /// <summary>The player spectates, then respawns after <see cref="DeathOptions.RespawnDelay"/>.</summary>
    RespawnAfterDelay,

    /// <summary>The player spectates until they rejoin.</summary>
    SpectateOnly,
}

/// <summary>How death plays out. A world option selects these once world options exist.</summary>
public sealed record DeathOptions
{
    public DeathPolicy Policy { get; init; } = DeathPolicy.RespawnAfterDelay;

    /// <summary>How long a dead player spectates before respawning, when the policy respawns.</summary>
    public TimeSpan RespawnDelay { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A Corpse left where a player died: a Container of what they carried and wore, which any player can loot.</summary>
public sealed record Corpse(ContainerId Container, string Owner, Vector3 Position);

/// <summary>The record of one death, kept after the Corpse is looted away.</summary>
public sealed record Memorial(string Player, int DaysSurvived, int Kills, DeathCause Cause);

/// <summary>What one death left behind.</summary>
public sealed record DeathReport(Corpse Corpse, Memorial Memorial);

/// <summary>Stores the Corpses that still hold something. Their Containers are stored with the <see cref="IContainerRepository"/>.</summary>
public interface ICorpseRepository
{
    /// <summary>Stores the Corpse, replacing what was stored for its Container.</summary>
    void Save(Corpse corpse);

    void Remove(ContainerId container);

    IReadOnlyList<Corpse> All();
}

/// <summary>Stores the Memorials, oldest first.</summary>
public interface IMemorialRepository
{
    void Add(Memorial memorial);

    IReadOnlyList<Memorial> All();
}

public sealed class InMemoryCorpseRepository : ICorpseRepository
{
    private readonly Dictionary<ContainerId, Corpse> _corpses = [];

    public void Save(Corpse corpse)
    {
        ArgumentNullException.ThrowIfNull(corpse);
        _corpses[corpse.Container] = corpse;
    }

    public void Remove(ContainerId container) => _corpses.Remove(container);

    public IReadOnlyList<Corpse> All() => [.. _corpses.Values.OrderBy(c => c.Container.Value)];
}

public sealed class InMemoryMemorialRepository : IMemorialRepository
{
    private readonly List<Memorial> _memorials = [];

    public void Add(Memorial memorial)
    {
        ArgumentNullException.ThrowIfNull(memorial);
        _memorials.Add(memorial);
    }

    public IReadOnlyList<Memorial> All() => [.. _memorials];
}

/// <summary>Where the dead are stored: their corpse Containers, the Corpses, and the Memorials.</summary>
public sealed record DeathStores(IContainerRepository Containers, ICorpseRepository Corpses, IMemorialRepository Memorials)
{
    public static DeathStores InMemory() => new(new InMemoryContainerRepository(), new InMemoryCorpseRepository(), new InMemoryMemorialRepository());
}
