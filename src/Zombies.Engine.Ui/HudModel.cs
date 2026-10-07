using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.Survival;

namespace Zombies.Engine.Ui;

/// <summary>A Treatment the health screen offers for a body part. It can be started only when the player holds the Item it consumes.</summary>
public sealed record TreatmentOffer(string Id, string Label, bool HasItem);

/// <summary>One body part as the health screen shows it, with the kinds of its Wounds and the Treatments that would help.</summary>
public sealed record BodyPartStatus(
    BodyPart Part,
    string Label,
    double HealthFraction,
    bool IsMissing,
    bool IsBleeding,
    bool IsBandaged,
    string? StateLabel,
    PaletteRole Role,
    IReadOnlyList<string> WoundKinds,
    IReadOnlyList<TreatmentOffer> Treatments);

/// <summary>Why a body part holds a Limb score back, as the health screen words it: the part and the cause.</summary>
public sealed record LimbScoreReason(string Part, string Cause);

/// <summary>A Limb score that is below full, with the reasons it is.</summary>
public sealed record LimbScoreStatus(string Label, double Value, IReadOnlyList<LimbScoreReason> Reasons, PaletteRole Role);

/// <summary>
/// One active Status effect as the HUD lists it. <see cref="Label"/> is the line in the list; <see cref="Tooltip"/> names the effect, says
/// what stage it is in, and says what cures it.
/// </summary>
public sealed record StatusEffectStatus(string Effect, string Name, string? Stage, int Stacks, PaletteRole Role, string Label, string CureHint, Tooltip Tooltip);

/// <summary>
/// What the HUD shows: health, blood, bleeding, hunger, thirst, warmth, and the weapon in hand. It reads the Body, the
/// Needs, and the weapon's Item state and turns them into fractions, colors, and localized words, so the drawing code
/// only has to place them.
/// </summary>
public sealed class HudModel(
    Body body,
    Needs needs,
    ItemId? weapon,
    ItemState? weaponState,
    Localizer localizer,
    IReadOnlyList<LimbScoreDefinition>? limbScores = null,
    IReadOnlyList<WearableDefinition>? worn = null,
    TreatmentCatalog? treatments = null,
    Func<ItemId, bool>? holds = null,
    IReadOnlyList<ActiveEffect>? effects = null,
    StatusEffectCatalog? effectCatalog = null)
{
    /// <summary>Health below this fraction is a warning.</summary>
    public const double WarningThreshold = 0.6;

    /// <summary>Health below this fraction is dangerous.</summary>
    public const double DangerThreshold = 0.3;

    /// <summary>Bleeding at or above this many millilitres per minute is dangerous.</summary>
    public const double HeavyBleedMillilitersPerMinute = 20;

    private const double StartingBloodLiters = 5;

    public double HealthFraction => Fraction(WorstPartHealth(), 100);

    public PaletteRole HealthRole => Role(HealthFraction);

    public double BloodFraction => Fraction(body.BloodVolume.Liters, StartingBloodLiters);

    public PaletteRole BloodRole => Role(BloodFraction);

    public bool IsBleeding => body.TotalBleedRate > VolumeFlow.Zero;

    /// <summary>How fast blood is being lost, as a fraction of the rate that counts as heavy bleeding.</summary>
    public double BleedFraction => Fraction(body.TotalBleedRate.MillilitersPerMinute, HeavyBleedMillilitersPerMinute);

    public PaletteRole BleedRole => IsBleeding ? PaletteRole.Danger : PaletteRole.Good;

    public double SatietyFraction => needs.Satiety;

    public string HungerLabel => localizer.Get($"hud.hunger.{Snake(needs.Hunger.ToString())}");

    public PaletteRole HungerRole => RoleOf(needs.Hunger);

    public double HydrationFraction => needs.Hydration;

    public string ThirstLabel => localizer.Get($"hud.thirst.{Snake(needs.Thirst.ToString())}");

    public PaletteRole ThirstRole => RoleOf(needs.Thirst);

    public string WarmthLabel => localizer.Get($"hud.warmth.{Snake(needs.Warmth.ToString())}");

    public PaletteRole WarmthRole => needs.Warmth switch
    {
        TemperatureLevel.Normal => PaletteRole.Good,
        TemperatureLevel.Cold or TemperatureLevel.Hot => PaletteRole.Warning,
        _ => PaletteRole.Danger,
    };

    /// <summary>The rounds left in the weapon, or null when nothing is held.</summary>
    public int? Ammo => weapon is null ? null : WeaponService.RoundsOf(weaponState);

    public double ConditionFraction => weapon is null ? 0 : Fraction(WeaponService.ConditionOf(weaponState), WeaponService.MaxCondition);

    public PaletteRole ConditionRole => Role(ConditionFraction);

    public string WeaponLabel => weapon is null ? localizer.Get("hud.no_weapon") : localizer.Get($"item.{weapon.Value.Value.Replace(':', '.').Replace('/', '.')}");

    /// <summary>
    /// The Status effects on the player, worst first and buffs last. The list is empty until the Server has told the client of one:
    /// the Server owns the effects and replicates them to the affected player alone.
    /// </summary>
    public IReadOnlyList<StatusEffectStatus> StatusEffects =>
    [
        .. (effects ?? [])
            .Select(Effect)
            .OrderBy(e => e.Role == PaletteRole.Good ? 1 : 0)
            .ThenBy(e => e.Role == PaletteRole.Danger ? 0 : 1)
            .ThenBy(e => e.Effect, StringComparer.Ordinal),
    ];

    private StatusEffectStatus Effect(ActiveEffect active)
    {
        var known = effectCatalog is not null && effectCatalog.TryGet(active.Effect, out _);
        var definition = known ? effectCatalog!.All.First(d => d.Id == active.Effect) : null;
        var key = ContentKey("status_effect", active.Effect);
        var name = localizer.Get(key);
        var stage = active.Stage is null ? null : localizer.Get($"{key}.stage.{active.Stage}");
        var hint = CureHint(definition);

        var body = localizer.Get($"{key}.tooltip");
        if (stage is not null)
        {
            body = $"{localizer.Format("hud.effect.stage", stage)} {body}";
        }

        var label = stage is null ? name : $"{name}: {stage}";
        if (active.Stacks > 1)
        {
            label = $"{label} x{active.Stacks}";
        }

        return new StatusEffectStatus(active.Effect, name, stage is null ? null : active.Stage, active.Stacks, EffectRole(definition, active.Stage), label, hint, Tooltip.Create(name, body, hint));
    }

    /// <summary>A buff is good. An ailment is a warning, and a danger once it has reached the last of several stages.</summary>
    private static PaletteRole EffectRole(StatusEffectDefinition? definition, string? stage)
    {
        if (definition is null)
        {
            return PaletteRole.Warning;
        }

        if (definition.Category == EffectCategory.Buff)
        {
            return PaletteRole.Good;
        }

        var index = stage is null ? -1 : definition.Stages.ToList().FindIndex(s => s.Name == stage);
        return index > 0 && index == definition.Stages.Count - 1 ? PaletteRole.Danger : PaletteRole.Warning;
    }

    /// <summary>What ends the effect: the items that cure it, the effects that cure it, or time. It is one short line, since a tooltip is read at a glance.</summary>
    private string CureHint(StatusEffectDefinition? definition)
    {
        if (definition is null)
        {
            return localizer.Get("hud.effect.cure_none");
        }

        if (definition.CuredByItems.Count > 0)
        {
            return localizer.Format("hud.effect.cure_item", string.Join(", ", definition.CuredByItems.Select(i => localizer.Get(ContentKey("item", i.Value)))));
        }

        if (definition.CuredByEffects.Count > 0)
        {
            return localizer.Format("hud.effect.cure_effect", string.Join(", ", definition.CuredByEffects.Select(e => localizer.Get(ContentKey("status_effect", e)))));
        }

        return localizer.Get(definition.Duration is null ? "hud.effect.cure_none" : "hud.effect.cure_time");
    }

    /// <summary>Every body part in a fixed order, with its health and what is wrong with it.</summary>
    public IReadOnlyList<BodyPartStatus> Parts =>
        [.. Enum.GetValues<BodyPart>().Select(Part)];

    /// <summary>Every Limb score that is below full, with the body parts and causes behind it.</summary>
    public IReadOnlyList<LimbScoreStatus> ReducedScores =>
        [.. LimbScores.Compute(limbScores ?? [], body, worn ?? []).Where(s => s.IsReduced).Select(Score)];

    private LimbScoreStatus Score(LimbScore score) => new(
        localizer.Get(ContentKey("limb_score", score.Definition)),
        score.Value,
        [.. score.Reductions.Select(r => new LimbScoreReason(localizer.Get($"hud.part.{Snake(r.Part.ToString())}"), localizer.Get($"hud.score.cause.{Snake(r.Cause.ToString())}")))],
        Role(score.Value));

    private BodyPartStatus Part(BodyPart part)
    {
        var missing = body.IsMissing(part);
        var health = Fraction(body.Health(part), 100);
        var bleeding = body.Wounds.Any(w => w.Part == part && !w.IsBandaged && w.BleedRate > VolumeFlow.Zero);
        var bandaged = body.Wounds.Any(w => w.Part == part && w.IsBandaged);

        var state = missing ? "hud.part.missing" : bleeding ? "hud.bleeding" : bandaged ? "hud.part.bandaged" : null;
        var role = missing || bleeding ? PaletteRole.Danger : bandaged ? PaletteRole.Good : Role(health);

        var kinds = body.Wounds.Where(w => w.Part == part && w.Kind is not null).Select(w => localizer.Get(ContentKey("wound_kind", w.Kind!))).Distinct();
        var offers = (treatments?.Available(body, part) ?? []).Select(t => new TreatmentOffer(t.Id, localizer.Get(ContentKey("treatment", t.Id)), holds?.Invoke(t.Consumes) ?? false));

        return new BodyPartStatus(part, localizer.Get($"hud.part.{Snake(part.ToString())}"), health, missing, bleeding, bandaged, state is null ? null : localizer.Get(state), role, [.. kinds], [.. offers]);
    }

    /// <summary>The text key of a definition, such as <c>wound_kind.base.wound_kind.scratch</c>.</summary>
    private static string ContentKey(string kind, string id) => $"{kind}.{id.Replace(':', '.').Replace('/', '.')}";

    /// <summary>The health of the part that is worst off. A part that is gone counts as no health at all.</summary>
    private double WorstPartHealth()
    {
        var worst = 100.0;
        foreach (var part in Enum.GetValues<BodyPart>())
        {
            worst = Math.Min(worst, body.IsMissing(part) ? 0 : body.Health(part));
        }

        return worst;
    }

    private static PaletteRole Role(double fraction) =>
        fraction < DangerThreshold ? PaletteRole.Danger : fraction < WarningThreshold ? PaletteRole.Warning : PaletteRole.Good;

    /// <summary>Hunger and thirst already have their own levels, so the color follows the level rather than the raw fraction.</summary>
    private static PaletteRole RoleOf(HungerLevel level) => level switch
    {
        HungerLevel.Satiated => PaletteRole.Good,
        HungerLevel.Hungry => PaletteRole.Warning,
        _ => PaletteRole.Danger,
    };

    private static PaletteRole RoleOf(ThirstLevel level) => level switch
    {
        ThirstLevel.Hydrated => PaletteRole.Good,
        ThirstLevel.Thirsty => PaletteRole.Warning,
        _ => PaletteRole.Danger,
    };

    private static double Fraction(double value, double limit) => limit <= 0 ? 0 : Math.Clamp(value / limit, 0, 1);

    private static string Snake(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
