using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class LimbScoreTests
{
    private static readonly ModifierSource Limbs = new("limb_scores");

    private static readonly LimbScoreDefinition Movement = LimbScoreJson.Parse("""
        { "id": "test:limb_score/movement", "stat": "move_speed", "floor": 0.2, "woundsReduce": true, "encumbranceReduces": true,
          "parts": [ { "part": "leftLeg", "required": true }, { "part": "rightLeg", "required": true } ] }
        """);

    private static readonly LimbScoreDefinition Grip = LimbScoreJson.Parse("""
        { "id": "test:limb_score/grip", "stat": "handling", "floor": 0.1,
          "parts": [ { "part": "leftArm" }, { "part": "rightArm", "weight": 3 } ] }
        """);

    private static readonly LimbScoreDefinition MovementPlain = LimbScoreJson.Parse("""
        { "id": "test:limb_score/movement_plain", "stat": "move_speed", "floor": 0.2,
          "parts": [ { "part": "leftLeg", "required": true }, { "part": "rightLeg", "required": true } ] }
        """);

    private static readonly LimbScoreDefinition MovementWoundsOnly = LimbScoreJson.Parse("""
        { "id": "test:limb_score/movement_wounds", "stat": "move_speed", "floor": 0.2, "woundsReduce": true,
          "parts": [ { "part": "leftLeg", "required": true }, { "part": "rightLeg", "required": true } ] }
        """);

    private static readonly LimbScoreDefinition GripFull = LimbScoreJson.Parse("""
        { "id": "test:limb_score/grip_full", "stat": "handling", "floor": 0.1, "woundsReduce": true, "encumbranceReduces": true,
          "parts": [ { "part": "leftArm" }, { "part": "rightArm", "weight": 3 } ] }
        """);

    private static readonly WearableDefinition Boots = new(new ItemId("test:item/boots"), ClothingLayer.Base, [BodyPart.LeftLeg, BodyPart.RightLeg], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1), encumbrance: 0.25);

    private static Body NewBody(params BodyPart[] missing) => new(new BodyId(1), missingAtSpawn: missing);

    private static LimbScore Score(LimbScoreDefinition definition, Body body, params WearableDefinition[] worn) =>
        Assert.Single(LimbScores.Compute([definition], body, worn));

    [Fact]
    public void AHealthyBody_ScoresFull_WithNothingHoldingItBack()
    {
        var score = Score(Movement, NewBody());

        Assert.Equal(1, score.Value);
        Assert.False(score.IsReduced);
        Assert.Empty(score.Reductions);
    }

    [Fact]
    public void AnInjuredPart_ReducesTheScoreByItsShare()
    {
        var body = NewBody();
        body.TakeHit(BodyPart.LeftLeg, DamageType.Blunt, 50);

        var legs = Score(MovementPlain, body);

        Assert.Equal(0.2 + (0.8 * 0.75), legs.Value, 6);
        Assert.Equal(1, Score(Grip, body).Value);
        Assert.Equal([new LimbScoreReduction(BodyPart.LeftLeg, LimbScoreCause.Injured)], legs.Reductions);
    }

    [Fact]
    public void Wounds_ReduceTheScoreOnlyWhenTheDefinitionSaysSo()
    {
        var body = NewBody();
        body.TakeHit(BodyPart.LeftLeg, DamageType.Cut, 20);

        var withWounds = Score(MovementWoundsOnly, body);
        var without = Score(MovementPlain, body);

        Assert.True(withWounds.Value < without.Value);
        Assert.Contains(new LimbScoreReduction(BodyPart.LeftLeg, LimbScoreCause.Wounded), withWounds.Reductions);
        Assert.DoesNotContain(without.Reductions, r => r.Cause == LimbScoreCause.Wounded);
    }

    [Fact]
    public void ABandagedWound_StillReducesTheScore_UntilItHeals()
    {
        var body = NewBody();
        body.TakeHit(BodyPart.LeftLeg, DamageType.Cut, 20);
        var before = Score(Movement, body).Value;

        body.Bandage(BodyPart.LeftLeg);

        Assert.Equal(before, Score(Movement, body).Value);
    }

    [Fact]
    public void AMissingRequiredPart_DropsTheScoreToItsFloor()
    {
        var score = Score(Movement, NewBody(BodyPart.RightLeg));

        Assert.Equal(0.2, score.Value);
        Assert.Equal([new LimbScoreReduction(BodyPart.RightLeg, LimbScoreCause.Missing)], score.Reductions);
    }

    [Fact]
    public void AMissingPartThatIsNotRequired_OnlyTakesItsShareAway()
    {
        var score = Score(Grip, NewBody(BodyPart.LeftArm));

        Assert.Equal(0.1 + (0.9 * 0.75), score.Value, 6);
    }

    [Fact]
    public void LosingEveryContributingPart_LeavesTheFloor()
    {
        Assert.Equal(0.1, Score(Grip, NewBody(BodyPart.LeftArm, BodyPart.RightArm)).Value, 6);
    }

    [Fact]
    public void WornItems_ReduceTheScoreByTheirEncumbrance_OnlyWhenTheDefinitionSaysSo()
    {
        var body = NewBody();

        var encumbered = Score(Movement, body, Boots);
        var ignored = Score(MovementWoundsOnly, body, Boots);

        Assert.Equal(0.2 + (0.8 * 0.75), encumbered.Value, 6);
        Assert.Contains(new LimbScoreReduction(BodyPart.RightLeg, LimbScoreCause.Encumbered), encumbered.Reductions);
        Assert.Equal(1, ignored.Value);
    }

    [Fact]
    public void WoundsMissingPartsAndClothing_CombineAndNeverGoBelowTheFloor()
    {
        var body = NewBody(BodyPart.LeftArm);
        body.TakeHit(BodyPart.RightArm, DamageType.Cut, 60);
        var heavy = new WearableDefinition(new ItemId("test:item/plate"), ClothingLayer.Armor, [BodyPart.RightArm], ThermalResistance.Zero, encumbrance: 0.9);

        var grip = Score(GripFull, body, heavy);

        Assert.InRange(grip.Value, 0.1, 0.3);
        Assert.Equal(
            [LimbScoreCause.Missing, LimbScoreCause.Injured, LimbScoreCause.Wounded, LimbScoreCause.Encumbered],
            grip.Reductions.Select(r => r.Cause));
    }

    [Fact]
    public void Scores_AreExposedAsMultiplyingModifiers_OnTheirStat()
    {
        var scores = LimbScores.Compute([Movement, Grip], NewBody(BodyPart.LeftLeg), []);

        var modifiers = LimbScores.ToModifiers(scores, Limbs);

        Assert.Equal(4.3 * 0.2, modifiers.EffectiveValue(new StatName("move_speed"), 4.3), 6);
        Assert.Equal(10, modifiers.EffectiveValue(new StatName("handling"), 10));
        Assert.All(modifiers.All, m => Assert.Equal(ModifierOperation.Multiply, m.Operation));
        Assert.All(modifiers.All, m => Assert.Equal(Limbs, m.Source));
    }

    [Fact]
    public void Compute_DoesNotChangeTheBody()
    {
        var body = NewBody();
        body.TakeHit(BodyPart.LeftLeg, DamageType.Cut, 20);
        var before = body.ToSnapshot();

        LimbScores.Compute([Movement, Grip], body, [Boots]);

        Assert.Equal(before.Wounds, body.ToSnapshot().Wounds);
        Assert.Equal(before.Parts, body.ToSnapshot().Parts);
    }

    [Fact]
    public void AHurtArm_WeakensWeaponHandling_ThroughTheGripScore()
    {
        var pistol = new ItemId("base:item/pistol");
        var service = new WeaponService(new WeaponCatalog(
            [new WeaponCategory("base:weapon_category/pistol", handling: 10, mounts: [])],
            [new WeaponDefinition(pistol, "base:weapon_category/pistol", damage: 30, DamageType.Pierce, rateOfFire: 2, reach: 25, noise: 90)],
            []));
        var body = NewBody(BodyPart.LeftArm);
        var grip = LimbScores.ToModifiers(LimbScores.Compute([Grip], body, []), Limbs);

        Assert.True(service.TryGetEffectiveStats(pistol, null, out var steady));
        Assert.True(service.TryGetEffectiveStats(pistol, null, out var shaky, grip));

        Assert.Equal(10, steady.Handling);
        Assert.Equal(10 * (0.1 + (0.9 * 0.75)), shaky.Handling, 6);
        Assert.Equal(steady.Damage, shaky.Damage);
    }

    [Theory]
    [InlineData("""{ "id": "test:limb_score/x", "stat": "s", "parts": [] }""")]
    [InlineData("""{ "id": "test:limb_score/x", "stat": "s", "parts": [ { "part": "tail" } ] }""")]
    [InlineData("""{ "id": "test:limb_score/x", "stat": "s", "floor": 1.5, "parts": [ { "part": "head" } ] }""")]
    [InlineData("""{ "id": "test:limb_score/x", "stat": "Bad Stat", "parts": [ { "part": "head" } ] }""")]
    [InlineData("""{ "id": "test:limb_score/x", "stat": "s", "parts": [ { "part": "head" }, { "part": "head" } ] }""")]
    [InlineData("""{ "id": "test:limb_score/x", "stat": "s", "parts": [ { "part": "head", "weight": 0 } ] }""")]
    [InlineData("not json")]
    public void InvalidDefinitions_AreRejected(string json)
    {
        Assert.Throws<LimbScoreDefinitionException>(() => LimbScoreJson.Parse(json));
    }
}
