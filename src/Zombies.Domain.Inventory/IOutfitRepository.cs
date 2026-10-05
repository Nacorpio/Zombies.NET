namespace Zombies.Domain.Inventory;

/// <summary>Stores the Outfit of each character, keyed by the character's id.</summary>
public interface IOutfitRepository
{
    bool TryGet(long owner, out Outfit outfit);

    /// <summary>Stores the Outfit, replacing what was stored for this owner.</summary>
    void Save(long owner, Outfit outfit);

    IReadOnlyList<long> Owners();
}
