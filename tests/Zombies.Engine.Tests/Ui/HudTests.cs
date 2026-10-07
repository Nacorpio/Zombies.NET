using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class HudTests
{
    private static readonly ItemId Pistol = new("base:item/pistol_9mm");

    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "hud.health": "Health",
              "hud.blood": "Blood",
              "hud.bleeding": "Bleeding",
              "hud.hunger": "Hunger",
              "hud.thirst": "Thirst",
              "hud.warmth": "Warmth",
              "hud.ammo": "Ammo",
              "hud.condition": "Condition",
              "hud.weapon": "Weapon",
              "hud.no_weapon": "Unarmed",
              "hud.hunger.satiated": "Satiated",
              "hud.hunger.hungry": "Hungry",
              "hud.hunger.starving": "Starving",
              "hud.thirst.hydrated": "Hydrated",
              "hud.thirst.thirsty": "Thirsty",
              "hud.thirst.dehydrated": "Dehydrated",
              "hud.warmth.normal": "Comfortable",
              "hud.warmth.cold": "Cold",
              "hud.warmth.hypothermic": "Freezing",
              "hud.warmth.hot": "Hot",
              "hud.warmth.hyperthermic": "Overheating",
              "hud.part.head": "Head",
              "hud.part.torso": "Torso",
              "hud.part.left_arm": "Left arm",
              "hud.part.right_arm": "Right arm",
              "hud.part.left_leg": "Left leg",
              "hud.part.right_leg": "Right leg",
              "hud.part.missing": "Missing",
              "hud.part.bandaged": "Bandaged",
              "hud.score.cause.missing": "Missing",
              "hud.score.cause.injured": "Injured",
              "hud.score.cause.wounded": "Wounded",
              "hud.score.cause.encumbered": "Encumbered",
              "limb_score.test.limb_score.movement": "Movement",
              "wound_kind.test.wound_kind.scratch": "Scratch",
              "treatment.test.treatment.bandage": "Bandage",
              "treatment.test.treatment.dress": "Dress scratch" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private static HudModel Model(Body body, Needs? needs = null, ItemState? weapon = null) =>
        new(body, needs ?? new Needs(), weapon is null ? null : Pistol, weapon, English());

    [Fact]
    public void Health_IsTheWorstPart_AndFallsWhenAPartIsHurt()
    {
        var body = new Body(new BodyId(1));
        var model = Model(body);

        Assert.Equal(1, model.HealthFraction, 3);

        body.TakeHit(BodyPart.Head, DamageType.Blunt, 100);

        Assert.True(model.HealthFraction < 1);
        Assert.Equal(PaletteRole.Danger, model.HealthRole);
    }

    [Fact]
    public void Health_IsGoodWhileTheBodyIsWhole()
    {
        Assert.Equal(PaletteRole.Good, Model(new Body(new BodyId(1))).HealthRole);
    }

    [Fact]
    public void Health_WarnsBeforeItIsDangerous()
    {
        var body = new Body(new BodyId(1));
        body.TakeHit(BodyPart.Torso, DamageType.Blunt, 50);

        Assert.Equal(PaletteRole.Warning, Model(body).HealthRole);
    }

    [Fact]
    public void Health_CountsAPartThatIsGoneAsNoHealthAtAll()
    {
        var body = new Body(new BodyId(1), missingAtSpawn: [BodyPart.LeftArm]);

        Assert.Equal(0, Model(body).HealthFraction, 3);
        Assert.Equal(PaletteRole.Danger, Model(body).HealthRole);
    }

    [Fact]
    public void Blood_IsAFractionOfWhatTheBodyStartedWith()
    {
        var body = new Body(new BodyId(1));
        var model = Model(body);

        Assert.Equal(1, model.BloodFraction, 3);

        body.TakeHit(BodyPart.Torso, DamageType.Cut, 40);
        body.Advance(TimeSpan.FromMinutes(5));

        Assert.True(model.BloodFraction < 1);
    }

    [Fact]
    public void Bleeding_ShowsTheRateAndIsHiddenWhenNothingBleeds()
    {
        var body = new Body(new BodyId(1));
        var model = Model(body);

        Assert.False(model.IsBleeding);
        Assert.Equal(0, model.BleedFraction, 3);

        body.TakeHit(BodyPart.Torso, DamageType.Cut, 40);

        Assert.True(model.IsBleeding);
        Assert.True(model.BleedFraction > 0);
        Assert.Equal(PaletteRole.Danger, model.BleedRole);
    }

    [Fact]
    public void Bleeding_StopsWhenEveryWoundIsBandaged()
    {
        var body = new Body(new BodyId(1));
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 40);
        var model = Model(body);

        Assert.True(model.IsBleeding);

        body.Bandage(BodyPart.Torso);

        Assert.False(model.IsBleeding);
    }

    [Fact]
    public void Needs_ShowAsFractionsWithALevelName()
    {
        var needs = new Needs();
        var model = Model(new Body(new BodyId(1)), needs);

        Assert.Equal(1, model.SatietyFraction, 3);
        Assert.Equal("Satiated", model.HungerLabel);
        Assert.Equal("Hydrated", model.ThirstLabel);
        Assert.Equal(PaletteRole.Good, model.HungerRole);
    }

    [Fact]
    public void Needs_TurnToWarningAndThenDangerAsTheyEmpty()
    {
        var needs = new Needs();
        var model = Model(new Body(new BodyId(1)), needs);

        needs.Advance(TimeSpan.FromHours(6), Temperature.FromDegreesCelsius(20), ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1));

        Assert.Equal("Thirsty", model.ThirstLabel);
        Assert.Equal(PaletteRole.Warning, model.ThirstRole);

        needs.Advance(TimeSpan.FromHours(6), Temperature.FromDegreesCelsius(20), ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1));

        Assert.Equal("Dehydrated", model.ThirstLabel);
        Assert.Equal(PaletteRole.Danger, model.ThirstRole);
    }

    [Fact]
    public void Hunger_TurnsToWarningAndThenDangerAsItEmpties()
    {
        var needs = new Needs();
        var model = Model(new Body(new BodyId(1)), needs);

        needs.Advance(TimeSpan.FromHours(15), Temperature.FromDegreesCelsius(20), ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1));

        Assert.Equal("Hungry", model.HungerLabel);
        Assert.Equal(PaletteRole.Warning, model.HungerRole);

        needs.Advance(TimeSpan.FromHours(5), Temperature.FromDegreesCelsius(20), ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1));

        Assert.Equal("Starving", model.HungerLabel);
        Assert.Equal(PaletteRole.Danger, model.HungerRole);
    }

    [Fact]
    public void Warmth_NamesTheTemperatureLevel()
    {
        var model = Model(new Body(new BodyId(1)));

        Assert.Equal("Comfortable", model.WarmthLabel);
        Assert.Equal(PaletteRole.Good, model.WarmthRole);
    }

    [Fact]
    public void Ammo_ShowsTheRoundsInTheWeapon_AndIsHiddenWithoutOne()
    {
        var unarmed = Model(new Body(new BodyId(1)));
        Assert.Null(unarmed.Ammo);
        Assert.Equal("Unarmed", unarmed.WeaponLabel);

        var armed = Model(new Body(new BodyId(1)), weapon: ItemState.Create([new("rounds", 7), new("condition", 80)]));

        Assert.Equal(7, armed.Ammo);
        Assert.Equal(0.8, armed.ConditionFraction, 3);
        Assert.Equal(PaletteRole.Good, armed.ConditionRole);
    }

    [Fact]
    public void Condition_WarnsAndThenFailsAsTheWeaponWearsOut()
    {
        var worn = Model(new Body(new BodyId(1)), weapon: ItemState.Create([new("condition", 40)]));
        var broken = Model(new Body(new BodyId(1)), weapon: ItemState.Create([new("condition", 0)]));

        Assert.Equal(PaletteRole.Warning, worn.ConditionRole);
        Assert.Equal(PaletteRole.Danger, broken.ConditionRole);
    }

    [Fact]
    public void ConditionAndAmmo_StillShow_WhenAttachmentsAreFittedToTheWeapon()
    {
        var fitted = ItemState.Create([new("rounds", 5), new("condition", 50)], [new(new ItemId("base:item/suppressor"), 1)]);

        var model = Model(new Body(new BodyId(1)), weapon: fitted);

        Assert.Equal(5, model.Ammo);
        Assert.Equal(0.5, model.ConditionFraction, 3);
        Assert.Equal(PaletteRole.Warning, model.ConditionRole);
    }

    [Fact]
    public void BodyParts_AreListedWithTheirHealthAndState()
    {
        var body = new Body(new BodyId(1), missingAtSpawn: [BodyPart.LeftArm]);
        body.TakeHit(BodyPart.Head, DamageType.Blunt, 50);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 10);
        body.Bandage(BodyPart.Torso);

        var parts = Model(body).Parts;

        Assert.Equal(6, parts.Count);
        Assert.Equal("Head", parts[0].Label);
        Assert.Equal(0.5, parts[0].HealthFraction, 3);
        Assert.True(parts[0].IsBleeding);
        Assert.Equal("Bleeding", parts[0].StateLabel);
        Assert.Equal(PaletteRole.Danger, parts[0].Role);

        var arm = parts.Single(p => p.Part == BodyPart.LeftArm);
        Assert.True(arm.IsMissing);
        Assert.Equal("Missing", arm.StateLabel);

        var torso = parts.Single(p => p.Part == BodyPart.Torso);
        Assert.True(torso.IsBandaged);
        Assert.False(torso.IsBleeding);
        Assert.Equal("Bandaged", torso.StateLabel);
        Assert.Equal(PaletteRole.Good, torso.Role);
    }

    [Fact]
    public void BodyParts_WithNoWound_HaveNoStateLabel()
    {
        var parts = Model(new Body(new BodyId(1))).Parts;

        Assert.All(parts, p => Assert.Null(p.StateLabel));
        Assert.All(parts, p => Assert.Equal(1, p.HealthFraction, 3));
    }

    [Fact]
    public void BodyParts_AreInAStableOrder()
    {
        var parts = Model(new Body(new BodyId(1))).Parts;

        Assert.Equal(
            [BodyPart.Head, BodyPart.Torso, BodyPart.LeftArm, BodyPart.RightArm, BodyPart.LeftLeg, BodyPart.RightLeg],
            parts.Select(p => p.Part));
    }

    [Fact]
    public void ReducedScores_NameTheScoreAndWhyItIsReduced_AndHideFullOnes()
    {
        var movement = LimbScoreJson.Parse("""
            { "id": "test:limb_score/movement", "stat": "move_speed", "floor": 0.2, "encumbranceReduces": true,
              "parts": [ { "part": "leftLeg", "required": true }, { "part": "rightLeg", "required": true } ] }
            """);
        var boots = new WearableDefinition(new ItemId("test:item/boots"), ClothingLayer.Base, [BodyPart.RightLeg], ThermalResistance.Zero, encumbrance: 0.5);
        var body = new Body(new BodyId(1), missingAtSpawn: [BodyPart.LeftLeg]);

        var healthy = new HudModel(new Body(new BodyId(1)), new Needs(), null, null, English(), [movement], []);
        var hurt = new HudModel(body, new Needs(), null, null, English(), [movement], [boots]);

        Assert.Empty(healthy.ReducedScores);
        var score = Assert.Single(hurt.ReducedScores);
        Assert.Equal("Movement", score.Label);
        Assert.Equal(0.2, score.Value, 6);
        Assert.Equal(PaletteRole.Danger, score.Role);
        Assert.Equal([new LimbScoreReason("Left leg", "Missing"), new LimbScoreReason("Right leg", "Encumbered")], score.Reasons);
    }

    [Fact]
    public void BodyParts_ListTheirWoundKinds_AndOfferTheTreatmentsThatApply()
    {
        var kinds = new WoundKindCatalog([WoundKindJson.Parse("""{ "id": "test:wound_kind/scratch", "damageTypes": ["cut"], "bleedRate": 3, "healingTime": 600 }""")]);
        var treatments = new TreatmentCatalog(
            [
                TreatmentJson.Parse("""{ "id": "test:treatment/bandage", "stopsBleeding": true, "time": 5, "consumes": "test:item/bandage" }"""),
                TreatmentJson.Parse("""{ "id": "test:treatment/dress", "removes": ["test:wound_kind/scratch"], "time": 8, "consumes": "test:item/dressing" }"""),
            ],
            kinds);
        var body = new Body(new BodyId(1), new BodyConfig { WoundKinds = kinds });
        body.TakeHit(BodyPart.LeftArm, DamageType.Cut, 5);

        var model = new HudModel(body, new Needs(), null, null, English(), treatments: treatments, holds: item => item == new ItemId("test:item/bandage"));
        var parts = model.Parts;

        var arm = parts.Single(p => p.Part == BodyPart.LeftArm);
        Assert.Equal(["Scratch"], arm.WoundKinds);
        Assert.Equal(
            [new TreatmentOffer("test:treatment/bandage", "Bandage", HasItem: true), new TreatmentOffer("test:treatment/dress", "Dress scratch", HasItem: false)],
            arm.Treatments);
        var leg = parts.Single(p => p.Part == BodyPart.LeftLeg);
        Assert.Empty(leg.WoundKinds);
        Assert.Empty(leg.Treatments);
    }

    [Fact]
    public void ADyingBody_ShowsDangerEverywhere()
    {
        var body = new Body(new BodyId(1));
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 80);
        body.Advance(TimeSpan.FromMinutes(30));
        var model = Model(body);

        Assert.Equal(PaletteRole.Danger, model.HealthRole);
        Assert.Equal(PaletteRole.Danger, model.BloodRole);
        Assert.Equal(PaletteRole.Danger, model.BleedRole);
    }
}
