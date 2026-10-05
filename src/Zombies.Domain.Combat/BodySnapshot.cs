using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

/// <summary>One Body part as saved: its health and whether it is a Missing part.</summary>
public sealed record PartSnapshot(BodyPart Part, double Health, bool IsMissing);

/// <summary>One Wound as saved. The bleed rate is in millilitres per minute, and the age is how long it has been its current kind.</summary>
public sealed record WoundSnapshot(int Id, BodyPart Part, DamageType Type, double Severity, double BleedMillilitersPerMinute, bool IsBandaged, bool IsStump, string? Kind = null, double AgeSeconds = 0);

/// <summary>Everything needed to rebuild a <see cref="Body"/>. Tuning (<see cref="BodyConfig"/>) is not saved; it comes from the mods.</summary>
public sealed record BodySnapshot(long Id, bool IsAlive, double BloodLiters, int NextWoundId, IReadOnlyList<PartSnapshot> Parts, IReadOnlyList<WoundSnapshot> Wounds);

/// <summary>Stores Bodies by id.</summary>
public interface IBodyRepository
{
    bool TryGet(BodyId id, out Body body);

    /// <summary>Stores the Body, replacing what was stored under its id.</summary>
    void Save(Body body);

    IReadOnlyList<BodyId> Ids();
}
