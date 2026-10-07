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

    private sealed class WoundState(WoundId id, BodyPart part, DamageType type, double severity, VolumeFlow bleedRate, bool isStump, string? kind = null)
    {
        public WoundId Id { get; } = id;

        public BodyPart Part { get; } = part;

        public DamageType Type { get; } = type;

        public double Severity { get; } = severity;

        public VolumeFlow BleedRate { get; } = bleedRate;

        public bool IsStump { get; } = isStump;

        public string? Kind { get; } = kind;

        public bool IsBandaged { get; set; }

        /// <summary>How long the Wound has existed as its current kind.</summary>
        public TimeSpan Age { get; set; }

        public Wound ToView() => new(Id, Part, Type, Severity, BleedRate, IsBandaged, IsStump, Kind);
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

    /// <summary>Whether any Wound is on the Body, without building the list <see cref="Wounds"/> does.</summary>
    public bool HasWounds => _wounds.Count > 0;

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

    public BodySnapshot ToSnapshot() => new(
        Id.Value,
        IsAlive,
        BloodVolume.Liters,
        _nextWoundId,
        [.. _parts.OrderBy(p => p.Key).Select(p => new PartSnapshot(p.Key, p.Value.Health, p.Value.IsMissing))],
        [.. _wounds.Select(w => new WoundSnapshot(w.Id.Value, w.Part, w.Type, w.Severity, w.BleedRate.MillilitersPerMinute, w.IsBandaged, w.IsStump, w.Kind, w.Age.TotalSeconds))]);

    /// <summary>Rebuilds a Body from a snapshot. Throws <see cref="ArgumentException"/> when the snapshot is not a state a Body can be in.</summary>
    public static Body Restore(BodySnapshot snapshot, BodyConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var body = new Body(new BodyId(snapshot.Id), config);
        if (!double.IsFinite(snapshot.BloodLiters) || snapshot.BloodLiters < 0)
        {
            throw new ArgumentException("Blood volume must be finite and not negative.", nameof(snapshot));
        }

        body.BloodVolume = Volume.FromLiters(snapshot.BloodLiters);
        body.IsAlive = snapshot.IsAlive;

        var seenParts = new HashSet<BodyPart>();
        foreach (var part in snapshot.Parts)
        {
            if (!Enum.IsDefined(part.Part) || !seenParts.Add(part.Part) || !double.IsFinite(part.Health) || part.Health < 0 || (part.IsMissing && part.Part == BodyPart.Torso))
            {
                throw new ArgumentException($"The saved state of {part.Part} is not valid.", nameof(snapshot));
            }

            body._parts[part.Part].Health = part.Health;
            body._parts[part.Part].IsMissing = part.IsMissing;
        }

        var seenWounds = new HashSet<int>();
        foreach (var wound in snapshot.Wounds)
        {
            if (!Enum.IsDefined(wound.Part) || !Enum.IsDefined(wound.Type) || wound.Id < 1 || !seenWounds.Add(wound.Id)
                || !double.IsFinite(wound.Severity) || !double.IsFinite(wound.BleedMillilitersPerMinute) || wound.BleedMillilitersPerMinute < 0
                || (wound.Kind is not null && !ItemId.TryParse(wound.Kind, out _)) || !double.IsFinite(wound.AgeSeconds) || wound.AgeSeconds < 0 || wound.AgeSeconds >= TimeSpan.MaxValue.TotalSeconds)
            {
                throw new ArgumentException($"Wound {wound.Id} is not valid.", nameof(snapshot));
            }

            body._wounds.Add(new WoundState(new WoundId(wound.Id), wound.Part, wound.Type, wound.Severity, VolumeFlow.FromMillilitersPerMinute(wound.BleedMillilitersPerMinute), wound.IsStump, wound.Kind)
            {
                IsBandaged = wound.IsBandaged,
                Age = TimeSpan.FromSeconds(wound.AgeSeconds),
            });
        }

        body._nextWoundId = Math.Max(snapshot.NextWoundId, seenWounds.Count == 0 ? 1 : seenWounds.Max() + 1);
        return body;
    }

    public double Health(BodyPart part) => _parts[part].Health;

    /// <summary>Health of a part as a fraction of its full health. A Missing part has none.</summary>
    public double HealthFraction(BodyPart part) => Math.Clamp(_parts[part].Health / _config.PartHealth, 0, 1);

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

        var kind = _config.WoundKinds?.Causing(type, effective);
        var bleed = kind?.BleedRate ?? VolumeFlow.FromMillilitersPerMinute(effective * _config.BleedPerDamage.GetValueOrDefault(type));
        if (kind is not null || bleed > VolumeFlow.Zero)
        {
            events.Add(AddWound(part, type, Math.Min(1, effective / _config.PartHealth), bleed, isStump: false, kind?.Id));
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

        var bleeding = BleedingOn(part);
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

    /// <summary>Whether <see cref="Treat"/> would change anything on this body part.</summary>
    public bool CanTreat(BodyPart part, Treatment treatment)
    {
        ArgumentNullException.ThrowIfNull(treatment);
        return IsAlive && !NothingToTreat(part, treatment, out _, out _);
    }

    /// <summary>
    /// Applies a Treatment to a body part: removes the Wounds of the kinds it removes, stops the bleeding if it does, and
    /// leaves the kinds it adds. The caller takes the Treatment's time and consumes its Item.
    /// </summary>
    public CombatResult Treat(BodyPart part, Treatment treatment)
    {
        ArgumentNullException.ThrowIfNull(treatment);
        if (!IsAlive)
        {
            return CombatResult.Failure(CombatError.AlreadyDead);
        }

        if (NothingToTreat(part, treatment, out var removed, out var bandaged))
        {
            return CombatResult.Failure(CombatError.NothingToTreat);
        }

        var added = new List<WoundKindDefinition>();
        foreach (var kind in treatment.Adds)
        {
            if (_config.WoundKinds is not { } kinds || !kinds.TryGet(kind, out var definition))
            {
                return CombatResult.Failure(CombatError.UnknownWoundKind);
            }

            added.Add(definition);
        }

        var events = new List<IDomainEvent>();
        var template = removed.Concat(bandaged).First();
        foreach (var wound in bandaged)
        {
            wound.IsBandaged = true;
        }

        _wounds.RemoveAll(removed.Contains);
        if (bandaged.Count > 0)
        {
            events.Add(new WoundsBandaged(Id, part, bandaged.Count));
        }

        events.Add(new WoundsTreated(Id, part, treatment.Id, removed.Count, bandaged.Count, added.Count));
        foreach (var kind in added)
        {
            events.Add(AddWound(part, template.Type, template.Severity, kind.BleedRate, isStump: false, kind.Id));
        }

        return CombatResult.Success(events);
    }

    /// <summary>Lets time pass: bleeding Wounds drain blood, Wounds heal or worsen, and the body dies when blood runs too low.</summary>
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

        if (IsAlive)
        {
            AgeWounds(elapsed, events);
        }

        return CombatResult.Success(events);
    }

    /// <summary>
    /// Each Wound of a known kind that reaches its healing time heals, unless it is untreated and the roll for this Wound
    /// says it worsens into the next kind. The roll depends only on the seed and the Wound, never on how time was split into steps.
    /// </summary>
    private void AgeWounds(TimeSpan elapsed, List<IDomainEvent> events)
    {
        if (_config.WoundKinds is not { } kinds)
        {
            return;
        }

        foreach (var wound in _wounds.Where(w => w.Kind is not null).ToList())
        {
            var current = wound;
            current.Age += elapsed;
            while (current.Kind is { } id && kinds.TryGet(id, out var kind) && current.Age >= kind.HealingTime)
            {
                var carried = current.Age - kind.HealingTime;
                if (!current.IsBandaged && kind.Worsening is { } worsening && kinds.TryGet(worsening.Kind, out var next) && Roll(current.Id) < worsening.Chance)
                {
                    var worse = new WoundState(new WoundId(_nextWoundId++), current.Part, current.Type, current.Severity, next.BleedRate, isStump: false, next.Id) { Age = carried };
                    _wounds[_wounds.IndexOf(current)] = worse;
                    events.Add(new WoundWorsened(Id, current.Id, worse.Id, current.Part, next.Id));
                    current = worse;
                }
                else
                {
                    _wounds.Remove(current);
                    events.Add(new WoundHealed(Id, current.Id, current.Part));
                    break;
                }
            }
        }
    }

    /// <summary>A repeatable number from 0 up to but not including 1 for one Wound, from the configured seed (SplitMix64).</summary>
    private double Roll(WoundId wound)
    {
        var z = unchecked(_config.WoundSeed + ((ulong)wound.Value * 0x9E3779B97F4A7C15UL));
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    private List<WoundState> BleedingOn(BodyPart part) =>
        [.. _wounds.Where(w => w.Part == part && !w.IsBandaged && w.BleedRate > VolumeFlow.Zero)];

    /// <summary>Finds what a Treatment would act on, and whether that is nothing at all.</summary>
    private bool NothingToTreat(BodyPart part, Treatment treatment, out List<WoundState> removed, out List<WoundState> bandaged)
    {
        removed = [.. _wounds.Where(w => w.Part == part && w.Kind is { } kind && treatment.Removes.Contains(kind))];
        bandaged = treatment.StopsBleeding ? [.. BleedingOn(part).Except(removed)] : [];
        return removed.Count == 0 && bandaged.Count == 0;
    }

    private WoundCreated AddWound(BodyPart part, DamageType type, double severity, VolumeFlow bleed, bool isStump, string? kind = null)
    {
        var id = new WoundId(_nextWoundId++);
        _wounds.Add(new WoundState(id, part, type, severity, bleed, isStump, kind));
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
