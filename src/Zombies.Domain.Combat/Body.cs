using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

/// <summary>
/// The body of a character or zombie: health per Body part, Wounds that bleed, Missing parts, and blood volume.
/// Protection from worn items is passed in by the caller, so Combat never reads another context's state.
/// </summary>
public sealed class Body
{
    private sealed class PartState(double health)
    {
        public double Health { get; set; } = health;

        public bool IsMissing { get; set; }
    }

    private sealed class WoundState(WoundId id, BodyPart part, DamageType type, double severity, VolumeFlow bleedRate, bool isStump)
    {
        public WoundId Id { get; } = id;

        public BodyPart Part { get; } = part;

        public DamageType Type { get; } = type;

        public double Severity { get; } = severity;

        public VolumeFlow BleedRate { get; } = bleedRate;

        public bool IsStump { get; } = isStump;

        public bool IsBandaged { get; set; }

        public Wound ToView() => new(Id, Part, Type, Severity, BleedRate, IsBandaged, IsStump);
    }

    private readonly BodyConfig _config;
    private readonly Dictionary<BodyPart, PartState> _parts = [];
    private readonly List<WoundState> _wounds = [];
    private int _nextWoundId = 1;

    /// <param name="missingAtSpawn">Parts the body lacks from the start. The torso cannot be missing.</param>
    public Body(BodyId id, BodyConfig? config = null, IEnumerable<BodyPart>? missingAtSpawn = null)
    {
        _config = config ?? new BodyConfig();
        Id = id;
        BloodVolume = _config.BloodVolume;

        foreach (var part in Enum.GetValues<BodyPart>())
        {
            _parts[part] = new PartState(_config.PartHealth);
        }

        foreach (var part in missingAtSpawn ?? [])
        {
            if (part == BodyPart.Torso)
            {
                throw new ArgumentException("The torso cannot be missing.", nameof(missingAtSpawn));
            }

            _parts[part].Health = 0;
            _parts[part].IsMissing = true;
        }
    }

    public BodyId Id { get; }

    public bool IsAlive { get; private set; } = true;

    public Volume BloodVolume { get; private set; }

    public IReadOnlyList<Wound> Wounds => [.. _wounds.Select(w => w.ToView())];

    public IReadOnlyList<BodyPart> MissingParts => [.. _parts.Where(p => p.Value.IsMissing).Select(p => p.Key)];

    public VolumeFlow TotalBleedRate
    {
        get
        {
            var total = VolumeFlow.Zero;
            foreach (var wound in _wounds.Where(w => !w.IsBandaged))
            {
                total += wound.BleedRate;
            }

            return total;
        }
    }

    public bool IsMissing(BodyPart part) => _parts[part].IsMissing;

    public double Health(BodyPart part) => _parts[part].Health;

    /// <param name="protection">Fraction (0 to 1) of the damage absorbed by worn items on this part.</param>
    public CombatResult TakeHit(BodyPart part, DamageType type, double damage, double protection = 0)
    {
        if (damage <= 0 || double.IsNaN(damage))
        {
            return CombatResult.Failure(CombatError.InvalidDamage);
        }

        if (protection is < 0 or > 1)
        {
            return CombatResult.Failure(CombatError.InvalidProtection);
        }

        if (!IsAlive)
        {
            return CombatResult.Failure(CombatError.AlreadyDead);
        }

        var state = _parts[part];
        if (state.IsMissing)
        {
            return CombatResult.Failure(CombatError.PartMissing);
        }

        var effective = damage * (1 - protection);
        if (effective <= 0)
        {
            return CombatResult.Success([new DamageAbsorbed(Id, part, type)]);
        }

        var events = new List<IDomainEvent> { new DamageTaken(Id, part, type, effective) };
        state.Health = Math.Max(0, state.Health - effective);

        var bleed = VolumeFlow.FromMillilitersPerMinute(effective * _config.BleedPerDamage.GetValueOrDefault(type));
        if (bleed > VolumeFlow.Zero)
        {
            events.Add(AddWound(part, type, Math.Min(1, effective / _config.PartHealth), bleed, isStump: false));
        }

        if (state.Health <= 0)
        {
            Destroy(part, events);
        }

        return CombatResult.Success(events);
    }

    /// <summary>Stops every bleeding Wound on a body part. A bandaged Wound no longer bleeds.</summary>
    public CombatResult Bandage(BodyPart part)
    {
        if (!IsAlive)
        {
            return CombatResult.Failure(CombatError.AlreadyDead);
        }

        var bleeding = _wounds.Where(w => w.Part == part && !w.IsBandaged && w.BleedRate > VolumeFlow.Zero).ToList();
        if (bleeding.Count == 0)
        {
            return CombatResult.Failure(CombatError.NothingToBandage);
        }

        foreach (var wound in bleeding)
        {
            wound.IsBandaged = true;
        }

        return CombatResult.Success([new WoundsBandaged(Id, part, bleeding.Count)]);
    }

    /// <summary>Lets time pass: bleeding Wounds drain blood, and the body dies when blood runs too low.</summary>
    public CombatResult Advance(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return CombatResult.Failure(CombatError.InvalidDuration);
        }

        if (!IsAlive)
        {
            return CombatResult.Success([]);
        }

        var events = new List<IDomainEvent>();
        var lost = TotalBleedRate * Duration.FromSeconds(elapsed.TotalSeconds);
        if (lost > Volume.Zero)
        {
            BloodVolume = Volume.FromLiters(Math.Max(0, (BloodVolume - lost).Liters));
            events.Add(new BloodLost(Id, lost, BloodVolume));
        }

        if (BloodVolume <= _config.LethalBloodVolume)
        {
            Die(DeathCause.BloodLoss, events);
        }

        return CombatResult.Success(events);
    }

    private WoundCreated AddWound(BodyPart part, DamageType type, double severity, VolumeFlow bleed, bool isStump)
    {
        var id = new WoundId(_nextWoundId++);
        _wounds.Add(new WoundState(id, part, type, severity, bleed, isStump));
        return new WoundCreated(Id, id, part, type, bleed);
    }

    private void Destroy(BodyPart part, List<IDomainEvent> events)
    {
        if (part == BodyPart.Torso)
        {
            Die(DeathCause.Trauma, events);
            return;
        }

        _parts[part].IsMissing = true;
        _wounds.RemoveAll(w => w.Part == part);
        events.Add(new BodyPartLost(Id, part));
        events.Add(AddWound(part, DamageType.Cut, 1, _config.StumpBleedRate, isStump: true));

        if (part == BodyPart.Head)
        {
            Die(DeathCause.Trauma, events);
        }
    }

    private void Die(DeathCause cause, List<IDomainEvent> events)
    {
        if (!IsAlive)
        {
            return;
        }

        IsAlive = false;
        events.Add(new BodyDied(Id, cause));
    }
}
