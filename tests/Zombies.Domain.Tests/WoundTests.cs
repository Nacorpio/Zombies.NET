using Zombies.Domain.Combat;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class WoundTests
{
    private static readonly BodyId Id = new(1);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static WoundKindCatalog Kinds(double scratchWorsens = 0.5) => new(
        new[]
        {
            $$"""
            { "id": "test:wound_kind/scratch", "damageTypes": ["cut", "pierce"], "damageRange": { "max": 10 }, "bleedRate": 3, "healingTime": 600,
              "worsens": { "kind": "test:wound_kind/infected", "chance": {{scratchWorsens}} } }
            """,
            """
            { "id": "test:wound_kind/deep_cut", "damageTypes": ["cut"], "damageRange": { "min": 10 }, "bleedRate": 40, "healingTime": 3600 }
            """,
            """
            { "id": "test:wound_kind/bruise", "damageTypes": ["blunt"], "healingTime": 1200 }
            """,
            """
            { "id": "test:wound_kind/infected", "bleedRate": 5, "healingTime": 7200 }
            """,
        }.Select(WoundKindJson.Parse));

    private static readonly string[] TreatmentJsons =
    [
        """{ "id": "test:treatment/bandage", "stopsBleeding": true, "time": 5, "consumes": "test:item/bandage" }""",
        """{ "id": "test:treatment/antibiotics", "removes": ["test:wound_kind/infected"], "time": 10, "consumes": "test:item/antibiotics" }""",
        """{ "id": "test:treatment/stitch", "removes": ["test:wound_kind/deep_cut"], "adds": ["test:wound_kind/scratch"], "stopsBleeding": true, "time": 30, "consumes": "test:item/thread" }""",
    ];

    private static TreatmentCatalog Treatments(WoundKindCatalog kinds) => new(TreatmentJsons.Select(TreatmentJson.Parse), kinds);

    private static Body NewBody(WoundKindCatalog? kinds = null, ulong seed = 0) =>
        new(Id, new BodyConfig { WoundKinds = kinds ?? Kinds(), WoundSeed = seed });

    [Fact]
    public void Damage_CausesTheKindWhoseTypeAndRangeItMatches()
    {
        var body = NewBody();

        body.TakeHit(BodyPart.LeftArm, DamageType.Cut, 5);
        body.TakeHit(BodyPart.LeftArm, DamageType.Cut, 20);
        body.TakeHit(BodyPart.LeftArm, DamageType.Blunt, 20);

        Assert.Equal(
            ["test:wound_kind/scratch", "test:wound_kind/deep_cut", "test:wound_kind/bruise"],
            body.Wounds.Select(w => w.Kind));
        Assert.Equal([3.0, 40.0, 0.0], body.Wounds.Select(w => w.BleedRate.MillilitersPerMinute));
    }

    [Fact]
    public void Damage_ThatNoKindCauses_MakesAWoundWithNoKind_ThatBleedsByDamage()
    {
        var body = NewBody();

        body.TakeHit(BodyPart.Torso, DamageType.Bite, 10);

        var wound = Assert.Single(body.Wounds);
        Assert.Null(wound.Kind);
        Assert.Equal(50, wound.BleedRate.MillilitersPerMinute, 6);
    }

    [Fact]
    public void ABodyWithoutKinds_KeepsItsWoundsForever()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);

        body.Advance(TimeSpan.FromDays(30));

        Assert.NotNull(body.Wounds.SingleOrDefault());
    }

    [Fact]
    public void ABruise_HealsOnItsOwn_AfterItsHealingTime()
    {
        var body = NewBody();
        body.TakeHit(BodyPart.Torso, DamageType.Blunt, 10);

        body.Advance(TimeSpan.FromSeconds(1199));
        Assert.Single(body.Wounds);

        var result = body.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(body.Wounds);
        Assert.IsType<WoundHealed>(Assert.Single(result.Events, e => e is WoundHealed));
    }

    [Fact]
    public void AnUntreatedWound_WorsensWhenTheRollSaysSo_AndOtherwiseHeals()
    {
        var outcomes = Enumerable.Range(0, 64).Select(seed => Outcome((ulong)seed)).ToList();

        Assert.Contains("test:wound_kind/infected", outcomes);
        Assert.Contains(null, outcomes);

        static string? Outcome(ulong seed)
        {
            var body = NewBody(seed: seed);
            body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);
            body.Advance(TimeSpan.FromSeconds(600));
            return body.Wounds.SingleOrDefault()?.Kind;
        }
    }

    [Fact]
    public void Worsening_IsDeterministicFromTheSeed_WhateverTheSizeOfTheTimeSteps()
    {
        foreach (var seed in Enumerable.Range(0, 32))
        {
            var whole = NewBody(seed: (ulong)seed);
            var steps = NewBody(seed: (ulong)seed);
            var again = NewBody(seed: (ulong)seed);
            foreach (var body in new[] { whole, steps, again })
            {
                body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);
            }

            whole.Advance(TimeSpan.FromMinutes(90));
            again.Advance(TimeSpan.FromMinutes(90));
            for (var i = 0; i < 90; i++)
            {
                steps.Advance(Minute);
            }

            Assert.Equal(whole.Wounds.Select(w => w.Kind), again.Wounds.Select(w => w.Kind));
            Assert.Equal(whole.Wounds.Select(w => w.Kind), steps.Wounds.Select(w => w.Kind));
        }
    }

    [Fact]
    public void AWoundThatWorsens_BecomesTheNextKind_WithItsOwnBleedAndAge()
    {
        var body = NewBody(Kinds(scratchWorsens: 1));
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);

        var result = body.Advance(TimeSpan.FromSeconds(700));

        var wound = Assert.Single(body.Wounds);
        Assert.Equal("test:wound_kind/infected", wound.Kind);
        Assert.Equal(5, wound.BleedRate.MillilitersPerMinute);
        var worsened = Assert.Single(result.Events.OfType<WoundWorsened>());
        Assert.Equal(new WoundId(1), worsened.Was);
        Assert.Equal(wound.Id, worsened.Now);
        Assert.Equal(100, body.ToSnapshot().Wounds.Single().AgeSeconds, 6);
    }

    [Fact]
    public void ABandagedWound_DoesNotWorsen_ItHeals()
    {
        var treatments = Treatments(Kinds());
        var body = NewBody(Kinds(scratchWorsens: 1));
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);
        Assert.True(body.Treat(BodyPart.Torso, Get(treatments, "bandage")).IsSuccess);

        body.Advance(TimeSpan.FromSeconds(600));

        Assert.Empty(body.Wounds);
    }

    [Fact]
    public void TheBandageTreatment_ReproducesBandage()
    {
        var treatments = Treatments(Kinds());
        var bandaged = new Body(Id);
        var treated = new Body(Id);
        foreach (var body in new[] { bandaged, treated })
        {
            body.TakeHit(BodyPart.LeftArm, DamageType.Cut, 20);
            body.TakeHit(BodyPart.LeftArm, DamageType.Blunt, 10);
        }

        var expected = bandaged.Bandage(BodyPart.LeftArm);
        var result = treated.Treat(BodyPart.LeftArm, Get(treatments, "bandage"));

        Assert.True(result.IsSuccess);
        Assert.Equal(expected.Events.Single(), result.Events.OfType<WoundsBandaged>().Single());
        Assert.Equal(bandaged.Wounds, treated.Wounds);
        Assert.Equal(bandaged.TotalBleedRate, treated.TotalBleedRate);
    }

    [Fact]
    public void ATreatment_RemovesTheKindsItNames_AndLeavesTheOthers()
    {
        var kinds = Kinds(scratchWorsens: 1);
        var treatments = Treatments(kinds);
        var body = NewBody(kinds);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);
        body.Advance(TimeSpan.FromSeconds(600));
        body.TakeHit(BodyPart.Torso, DamageType.Blunt, 5);

        var result = body.Treat(BodyPart.Torso, Get(treatments, "antibiotics"));

        Assert.True(result.IsSuccess);
        Assert.Equal("test:wound_kind/bruise", Assert.Single(body.Wounds).Kind);
        Assert.Equal(new WoundsTreated(Id, BodyPart.Torso, "test:treatment/antibiotics", 1, 0, 0), Assert.Single(result.Events));
    }

    [Fact]
    public void ATreatment_CanAddKinds_AndStopBleedingInOneGo()
    {
        var kinds = Kinds();
        var body = NewBody(kinds);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 20);

        var result = body.Treat(BodyPart.Torso, Get(Treatments(kinds), "stitch"));

        Assert.True(result.IsSuccess);
        var wound = Assert.Single(body.Wounds);
        Assert.Equal("test:wound_kind/scratch", wound.Kind);
        Assert.Equal(DamageType.Cut, wound.Type);
        Assert.Contains(result.Events, e => e is WoundsTreated { Removed: 1, Added: 1 });
    }

    [Fact]
    public void ATreatment_OnAPartItDoesNotApplyTo_ChangesNothing()
    {
        var kinds = Kinds();
        var treatments = Treatments(kinds);
        var body = NewBody(kinds);
        body.TakeHit(BodyPart.LeftArm, DamageType.Cut, 5);
        var before = body.Wounds;

        Assert.Equal(CombatError.NothingToTreat, body.Treat(BodyPart.RightArm, Get(treatments, "bandage")).Error);
        Assert.Equal(CombatError.NothingToTreat, body.Treat(BodyPart.LeftArm, Get(treatments, "antibiotics")).Error);
        Assert.Equal(before, body.Wounds);
    }

    [Fact]
    public void ATreatmentThatAddsAKindTheBodyDoesNotKnow_Fails_AndChangesNothing()
    {
        var body = new Body(Id, new BodyConfig { WoundKinds = new WoundKindCatalog([]) });
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 20);
        var adding = new Treatment("test:treatment/adds", [], ["test:wound_kind/scratch"], stopsBleeding: true, TimeSpan.Zero, new ItemId("test:item/x"));

        Assert.Equal(CombatError.UnknownWoundKind, body.Treat(BodyPart.Torso, adding).Error);
        Assert.True(Assert.Single(body.Wounds).IsBleeding);
    }

    [Fact]
    public void ADeadBody_CannotBeTreated()
    {
        var kinds = Kinds();
        var body = NewBody(kinds);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);
        body.TakeHit(BodyPart.Head, DamageType.Blunt, 500);

        Assert.Equal(CombatError.AlreadyDead, body.Treat(BodyPart.Torso, Get(Treatments(kinds), "bandage")).Error);
    }

    [Fact]
    public void Available_ListsOnlyTreatmentsThatWouldDoSomething()
    {
        var kinds = Kinds();
        var treatments = Treatments(kinds);
        var body = NewBody(kinds);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 20);

        Assert.Equal(["test:treatment/bandage", "test:treatment/stitch"], treatments.Available(body, BodyPart.Torso).Select(t => t.Id));
        Assert.Empty(treatments.Available(body, BodyPart.Head));
    }

    [Fact]
    public void KindAndAge_SurviveASnapshot()
    {
        var kinds = Kinds();
        var body = NewBody(kinds);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);
        body.Advance(TimeSpan.FromSeconds(100));

        var restored = Body.Restore(body.ToSnapshot(), new BodyConfig { WoundKinds = kinds });

        Assert.Equal(body.Wounds, restored.Wounds);
        restored.Advance(TimeSpan.FromSeconds(499));
        Assert.Single(restored.Wounds);
        restored.Advance(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(restored.Wounds, w => w.Kind == "test:wound_kind/scratch");
    }

    [Fact]
    public void Catalog_RejectsDuplicatesAndKindsThatWorsenIntoNothing()
    {
        var scratch = WoundKindJson.Parse("""{ "id": "test:wound_kind/a", "healingTime": 1, "worsens": { "kind": "test:wound_kind/missing", "chance": 0.5 } }""");

        Assert.Throws<ArgumentException>(() => new WoundKindCatalog([scratch]));
        Assert.Throws<ArgumentException>(() => new WoundKindCatalog([Kinds().All.First(), Kinds().All.First()]));
    }

    [Fact]
    public void TreatmentCatalog_RejectsDuplicatesAndUnknownKinds()
    {
        var kinds = Kinds();
        var bandage = Get(Treatments(kinds), "bandage");
        var unknown = new Treatment("test:treatment/x", ["test:wound_kind/missing"], [], false, TimeSpan.Zero, new ItemId("test:item/x"));

        Assert.Throws<ArgumentException>(() => new TreatmentCatalog([bandage, bandage], kinds));
        Assert.Throws<ArgumentException>(() => new TreatmentCatalog([unknown], kinds));
    }

    [Theory]
    [InlineData("""{ "id": "test:wound_kind/x", "healingTime": 0 }""")]
    [InlineData("""{ "id": "test:wound_kind/x", "healingTime": 1, "damageTypes": ["fire"] }""")]
    [InlineData("""{ "id": "test:wound_kind/x", "healingTime": 1, "damageTypes": ["7"] }""")]
    [InlineData("""{ "id": "test:wound_kind/x", "healingTime": 1, "damageRange": { "min": 5, "max": 5 } }""")]
    [InlineData("""{ "id": "test:wound_kind/x", "healingTime": 1, "bleedRate": -1 }""")]
    [InlineData("""{ "id": "test:wound_kind/x", "healingTime": 1, "worsens": { "kind": "test:wound_kind/y", "chance": 0 } }""")]
    [InlineData("""{ "id": "Bad", "healingTime": 1 }""")]
    [InlineData("""{ "id": "test:wound_kind/x" }""")]
    public void InvalidWoundKinds_AreRejected(string json)
    {
        Assert.Throws<WoundKindDefinitionException>(() => WoundKindJson.Parse(json));
    }

    [Theory]
    [InlineData("""{ "id": "test:treatment/x", "time": 1, "consumes": "test:item/x" }""")]
    [InlineData("""{ "id": "test:treatment/x", "stopsBleeding": true, "time": -1, "consumes": "test:item/x" }""")]
    [InlineData("""{ "id": "test:treatment/x", "stopsBleeding": true, "time": 1, "consumes": "nope" }""")]
    [InlineData("""{ "id": "test:treatment/x", "removes": ["nope"], "time": 1, "consumes": "test:item/x" }""")]
    [InlineData("""{ "id": "test:treatment/x", "stopsBleeding": true, "time": 1 }""")]
    public void InvalidTreatments_AreRejected(string json)
    {
        Assert.Throws<TreatmentDefinitionException>(() => TreatmentJson.Parse(json));
    }

    private static Treatment Get(TreatmentCatalog catalog, string name)
    {
        Assert.True(catalog.TryGet($"test:treatment/{name}", out var treatment));
        return treatment;
    }
}
