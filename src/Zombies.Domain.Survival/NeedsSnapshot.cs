namespace Zombies.Domain.Survival;

/// <summary>Everything needed to rebuild a <see cref="Needs"/>: how full, how hydrated, how warm, and how tired the body is. Whether it sleeps is not kept.</summary>
public sealed record NeedsSnapshot(double Satiety, double Hydration, double BodyCelsius, double Fatigue = 0);

/// <summary>Stores the Needs of each character, keyed by the character's id.</summary>
public interface INeedsRepository
{
    bool TryGet(long owner, out Needs needs);

    /// <summary>Stores the Needs, replacing what was stored for this owner.</summary>
    void Save(long owner, Needs needs);

    IReadOnlyList<long> Owners();
}
