using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;

namespace Zombies.Engine.Ui;

/// <summary>One body part as the health screen shows it.</summary>
public sealed record BodyPartStatus(BodyPart Part, string Label, double HealthFraction, bool IsMissing, bool IsBleeding, bool IsBandaged, string? StateLabel, PaletteRole Role);

/// <summary>Why a body part holds a Limb score back, as the health screen words it: the part and the cause.</summary>
public sealed record LimbScoreReason(string Part, string Cause);

/// <summary>A Limb score that is below full, with the reasons it is.</summary>
public sealed record LimbScoreStatus(string Label, double Value, IReadOnlyList<LimbScoreReason> Reasons, PaletteRole Role);

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
    IReadOnlyList<WearableDefinition>? worn = null)
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

    /// <summary>Every body part in a fixed order, with its health and what is wrong with it.</summary>
    public IReadOnlyList<BodyPartStatus> Parts =>
        [.. Enum.GetValues<BodyPart>().Select(Part)];

    /// <summary>Every Limb score that is below full, with the body parts and causes behind it.</summary>
    public IReadOnlyList<LimbScoreStatus> ReducedScores =>
        [.. LimbScores.Compute(limbScores ?? [], body, worn ?? []).Where(s => s.IsReduced).Select(Score)];

    private LimbScoreStatus Score(LimbScore score) => new(
        localizer.Get($"limb_score.{score.Definition.Replace(':', '.').Replace('/', '.')}"),
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

        return new BodyPartStatus(part, localizer.Get($"hud.part.{Snake(part.ToString())}"), health, missing, bleeding, bandaged, state is null ? null : localizer.Get(state), role);
    }

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
