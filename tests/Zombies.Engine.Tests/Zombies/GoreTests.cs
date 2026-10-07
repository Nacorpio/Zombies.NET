using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Zombies;
using Zombies.Engine.Animation;
using Zombies.Engine.Core;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Physics.Gore;
using Zombies.Engine.Tests.Rigs;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests;

/// <summary>Ragdolls, blood, and the gore setting: client-only, seeded, capped, and never able to change what a hit does.</summary>
public sealed class GoreTests
{
    private const string Plain = """
        {
          "id": "t:zombie/plain",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4, "maxLevel": 5, "perLevelBonus": 0.5 },
          "senses": { "sight": 20, "hearing": 30 },
          "traits": [],
          "appearance": { "skinTones": ["#8fa07a"] },
          "outfit": {
            "clothing": [ { "item": "t:item/shirt", "weight": 1 } ],
            "clothingCount": { "min": 1, "max": 1 },
            "headwear": [],
            "backpacks": []
          },
          "missingParts": []
        }
        """;

    private static readonly RigPlacement Origin = new(Vector3.Zero, 0f, Vector3.One);

    private static ZombieCosmetic Event(CosmeticKind kind, BodyPart part = BodyPart.Torso, ulong seed = 1, float damage = 50f, Vector3? point = null) =>
        new(1, kind, part, point ?? new Vector3(0, 1, 0), Vector3.UnitZ, damage, seed);

    private static IReadOnlyList<RagdollBodySpec> Build(ZombieCosmetic cosmetic, GoreLevel level = GoreLevel.High, MissingPartSet missing = MissingPartSet.None, RigPlacement? placement = null)
    {
        var skeleton = RigTestData.Mini();
        return RagdollPlan.Build(skeleton, new RigPose(skeleton), placement ?? Origin, cosmetic, missing, GoreProfile.For(level));
    }

    private static ZombieSystem System(out ServerWorld world)
    {
        world = new ServerWorld();
        var registry = new TraitRegistry();
        BaseTraits.Register(registry);
        return new ZombieSystem(world, new ZombieCatalog([ZombieTypeJson.Parse(Plain)]), registry, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
    }

    // ---- Decal pool ----

    [Fact]
    public void TheDecalPool_NeverGrowsPastItsCapacity_AndEvictsTheOldestFirst()
    {
        var pool = new DecalPool(4);

        for (var i = 0; i < 10; i++)
        {
            pool.Add(new BloodDecal(new Vector3(i, 0, 0), Vector3.UnitY, 0.2f, 0));
        }

        Assert.Equal(4, pool.Capacity);
        Assert.Equal(4, pool.Count);
        Assert.Equal(6, pool.Evicted);
        Assert.Equal([6f, 7f, 8f, 9f], Enumerable.Range(0, pool.Count).Select(i => pool[i].Position.X));
    }

    [Fact]
    public void TheDecalPool_KeepsEverythingBelowItsCapacity()
    {
        var pool = new DecalPool(4);
        pool.Add(new BloodDecal(Vector3.Zero, Vector3.UnitY, 0.2f, 0));
        pool.Add(new BloodDecal(Vector3.One, Vector3.UnitY, 0.2f, 0));

        Assert.Equal(2, pool.Count);
        Assert.Equal(0, pool.Evicted);
        Assert.Equal(Vector3.Zero, pool[0].Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecalPool(0));
    }

    [Fact]
    public void AnEndlessFight_KeepsTheDirectorsDecalsAtTheCap()
    {
        var director = new GoreDirector(GoreLevel.High, decalCapacity: 16);

        for (ulong seed = 0; seed < 200; seed++)
        {
            director.Play(Event(CosmeticKind.Hit, seed: seed), null);
        }

        Assert.Equal(16, director.Decals.Count);
        Assert.True(director.Decals.Evicted > 0);
    }

    // ---- Gore scaling ----

    [Fact]
    public void GoreScalesVisualsUp_FromOffToLowToHigh()
    {
        foreach (var kind in new[] { CosmeticKind.Hit, CosmeticKind.PartLost, CosmeticKind.Death })
        {
            var off = GoreEffects.For(Event(kind), GoreProfile.For(GoreLevel.Off));
            var low = GoreEffects.For(Event(kind), GoreProfile.For(GoreLevel.Low));
            var high = GoreEffects.For(Event(kind), GoreProfile.For(GoreLevel.High));

            Assert.Empty(off.Decals);
            Assert.Null(off.Spray);
            Assert.True(low.Decals.Count > 0 && high.Decals.Count > low.Decals.Count, kind.ToString());
            Assert.True(high.Spray!.Value.Count > low.Spray!.Value.Count, kind.ToString());
        }
    }

    [Fact]
    public void ADeathSplattersMoreThanAHit()
    {
        var profile = GoreProfile.For(GoreLevel.Low);

        var hit = GoreEffects.For(Event(CosmeticKind.Hit), profile);
        var death = GoreEffects.For(Event(CosmeticKind.Death), profile);

        Assert.True(death.Decals.Count > hit.Decals.Count);
        Assert.True(death.Spray!.Value.Count > hit.Spray!.Value.Count);
    }

    [Fact]
    public void WithGoreOff_NothingIsDrawnButAZombieStillCollapses()
    {
        var director = new GoreDirector(GoreLevel.Off);
        var skeleton = RigTestData.Mini();
        var look = new ZombieLook(skeleton, new RigPose(skeleton), Origin, MissingPartSet.None);

        director.Play(Event(CosmeticKind.Hit), look);
        director.Play(Event(CosmeticKind.Death), look);

        Assert.Equal(0, director.Decals.Count);
        Assert.Empty(director.PendingSprays);
        Assert.Single(director.PendingBodies);
    }

    [Fact]
    public void TheGoreLevel_CanChangeWhileTheGameRuns()
    {
        var director = new GoreDirector(GoreLevel.Off);
        director.Play(Event(CosmeticKind.Hit), null);
        director.Level = GoreLevel.High;
        director.Play(Event(CosmeticKind.Hit, seed: 2), null);

        Assert.Equal(GoreProfile.For(GoreLevel.High).DecalsPerHit, director.Decals.Count);
    }

    // ---- Seed determinism ----

    [Fact]
    public void TheSameEventAndSeed_MakeTheSameDecalsAndSpray_OnEveryClient()
    {
        var profile = GoreProfile.For(GoreLevel.High);

        var first = GoreEffects.For(Event(CosmeticKind.Death, seed: 77), profile);
        var second = GoreEffects.For(Event(CosmeticKind.Death, seed: 77), profile);
        var other = GoreEffects.For(Event(CosmeticKind.Death, seed: 78), profile);

        Assert.Equal(first.Decals, second.Decals);
        Assert.Equal(first.Spray, second.Spray);
        Assert.NotEqual(first.Decals, other.Decals);
        Assert.NotEqual(first.Spray!.Value.Seed, other.Spray!.Value.Seed);
    }

    [Fact]
    public void TheSameEventAndSeed_MakeTheSameRagdoll()
    {
        var first = Build(Event(CosmeticKind.Death, seed: 5));
        var second = Build(Event(CosmeticKind.Death, seed: 5));
        var other = Build(Event(CosmeticKind.Death, seed: 6));

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void TheServerSeedsEachCosmeticEventFromTheZombiesSeed_SoEverySystemAgrees()
    {
        var first = Fight(out _);
        var second = Fight(out _);

        Assert.Equal(first.Select(e => e.Seed), second.Select(e => e.Seed));
        Assert.Equal(first.Count, first.Select(e => e.Seed).Distinct().Count());
    }

    // ---- Ragdoll start pose and impulse ----

    [Fact]
    public void ARagdollStartsEveryBoneWhereTheLastPoseHadIt()
    {
        var skeleton = RigTestData.Mini();
        var pose = new RigPose(skeleton);

        var bodies = Build(Event(CosmeticKind.Death));

        Assert.Equal(skeleton.Bones.Count, bodies.Count);
        foreach (var body in bodies)
        {
            RigTestData.Near(pose.World(body.Bone).Position, body.Position);
            Assert.True(MathF.Abs(Quaternion.Dot(pose.World(body.Bone).Rotation, body.Rotation)) > 0.9999f);
            Assert.Equal(RagdollGroup.Corpse, body.Group);
        }
    }

    [Fact]
    public void ARagdoll_FollowsWhereTheZombieStoodAndWhichWayItFaced()
    {
        var skeleton = RigTestData.Mini();
        var pose = new RigPose(skeleton);
        var placement = new RigPlacement(new Vector3(10, 64, -3), MathF.PI / 2f, Vector3.One);

        var bodies = Build(Event(CosmeticKind.Death), placement: placement);

        var head = bodies.Single(b => b.Bone == skeleton.IndexOf("head"));
        RigTestData.Near(placement.ToWorld(pose.World("head").Position), head.Position);
        Assert.Equal(new Vector3(10, 66, -3), head.Position);
        var handLeft = bodies.Single(b => b.Bone == skeleton.IndexOf("arm_l"));

        // Yawed a quarter turn, the zombie's left (-X in the rig) is toward -Z in the world.
        RigTestData.Near(new Vector3(10, 65, -3) + new Vector3(0, 0, -1) * 1f, handLeft.Position, 1.01f);
    }

    [Fact]
    public void ARagdoll_IsPushedAlongTheHit_AndHardestNearWhereItLanded()
    {
        var skeleton = RigTestData.Mini();
        var hitAtHead = Event(CosmeticKind.Death, BodyPart.Head, point: new Vector3(0, 2, 0));

        var bodies = Build(hitAtHead);

        var head = bodies.Single(b => b.Bone == skeleton.IndexOf("head"));
        var foot = bodies.Single(b => b.Bone == skeleton.IndexOf("foot_l"));
        Assert.True(head.LinearVelocity.Z > 0, "Pushed along the hit direction.");
        Assert.True(head.LinearVelocity.Length() > foot.LinearVelocity.Length());
        Assert.All(bodies, b => Assert.True(b.LinearVelocity.Z > 0));
    }

    [Fact]
    public void AHarderHit_PushesHarder_UpToALimit()
    {
        float Speed(float damage) => Build(Event(CosmeticKind.Death, damage: damage)).Max(b => b.LinearVelocity.Length());

        Assert.True(Speed(100) > Speed(20));
        Assert.Equal(Speed(10_000), Speed(1_000_000), 0.001f);
    }

    [Fact]
    public void ADeadZombieThatLostAnArm_RagdollsWithoutIt()
    {
        var skeleton = RigTestData.Mini();

        var bodies = Build(Event(CosmeticKind.Death), missing: MissingPartSet.LeftArm);

        Assert.DoesNotContain(bodies, b => b.Bone == skeleton.IndexOf("arm_l") || b.Bone == skeleton.IndexOf("hand_l"));
        Assert.Equal(skeleton.Bones.Count - 2, bodies.Count);
    }

    [Fact]
    public void ARagdollsBodiesKnowTheirParentBones()
    {
        var skeleton = RigTestData.Mini();

        var bodies = Build(Event(CosmeticKind.Death));

        Assert.Equal(-1, bodies[0].Parent);
        foreach (var (body, index) in bodies.Select((b, i) => (b, i)).Skip(1))
        {
            Assert.Equal(skeleton.Bones[body.Bone].Parent, bodies[body.Parent].Bone);
            Assert.True(body.Parent < index);
        }
    }

    // ---- Dismemberment ----

    [Fact]
    public void ADismemberedPart_BecomesItsOwnBodies_SeparateFromTheCorpse()
    {
        var skeleton = RigTestData.Mini();

        var arm = Build(Event(CosmeticKind.PartLost, BodyPart.LeftArm));

        Assert.Equal(["arm_l", "hand_l"], arm.Select(b => skeleton.Bones[b.Bone].Name));
        Assert.All(arm, b => Assert.Equal(RagdollGroup.Severed, b.Group));
        Assert.All(arm, b => Assert.Equal(BodyPart.LeftArm, b.Part));
        Assert.Equal(-1, arm[0].Parent);
        Assert.Equal(0, arm[1].Parent);

        // The corpse that follows, with the arm already gone, does not include it.
        var corpse = Build(Event(CosmeticKind.Death), missing: MissingPartSet.LeftArm);
        Assert.Empty(corpse.Select(b => b.Bone).Intersect(arm.Select(b => b.Bone)));
    }

    [Fact]
    public void ASeveredPart_IsKickedHarderThanTheBodyItCameFrom()
    {
        var hit = Event(CosmeticKind.PartLost, BodyPart.LeftArm, point: new Vector3(-1, 2, 0));
        var severed = Build(hit);
        var corpse = Build(hit with { Kind = CosmeticKind.Death });

        Assert.True(severed[0].LinearVelocity.Length() > corpse.Single(b => b.Bone == severed[0].Bone).LinearVelocity.Length());
    }

    [Fact]
    public void ALostPart_MakesNoBodiesWhenGoreIsOff_AndAHitMakesNone()
    {
        Assert.Empty(Build(Event(CosmeticKind.PartLost, BodyPart.Head), GoreLevel.Off));
        Assert.NotEmpty(Build(Event(CosmeticKind.PartLost, BodyPart.Head), GoreLevel.Low));
        Assert.Empty(Build(Event(CosmeticKind.Hit, BodyPart.Head)));
    }

    // ---- Server cosmetic events ----

    private static List<ZombieCosmetic> Fight(out ZombieSystem system)
    {
        system = System(out _);
        Assert.True(system.TrySpawn(new ZombieSpec(9, "t:zombie/plain", 1), Vector3.Zero, 0f, out _, out var problem), problem);
        var events = new List<ZombieCosmetic>();
        system.Cosmetic += events.Add;
        system.Hit(new Vector3(-0.5f, 1.0f, -5), Vector3.UnitZ, 20f, DamageType.Pierce, 100);
        system.Hit(new Vector3(0, 1.6f, -5), Vector3.UnitZ, 20f, DamageType.Pierce, 100);
        return events;
    }

    [Fact]
    public void ALimbShotOffThenAHeadshot_RaiseHit_PartLost_Hit_PartLost_Death_InOrder()
    {
        var events = Fight(out _);

        Assert.Equal(
            [CosmeticKind.Hit, CosmeticKind.PartLost, CosmeticKind.Hit, CosmeticKind.PartLost, CosmeticKind.Death],
            events.Select(e => e.Kind));
        Assert.Equal(BodyPart.LeftArm, events[1].Part);
        Assert.Equal(BodyPart.Head, events[3].Part);
        Assert.All(events, e => Assert.Equal(Vector3.UnitZ, e.Direction));
    }

    // ---- Gore never changes hit results ----

    private sealed record Outcome(List<ZombieHit> Hits, List<ZombieState> States, List<int> Missing);

    private static (Outcome Outcome, GoreDirector Director) Scripted(GoreLevel level)
    {
        var system = System(out var world);
        var ids = new List<uint>();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(system.TrySpawn(new ZombieSpec((ulong)(100 + i), "t:zombie/plain", 1 + i), new Vector3(i * 10, 0, 0), 0f, out var id, out var problem), problem);
            ids.Add(id);
        }

        var director = new GoreDirector(level);
        var skeleton = RigTestData.BaseSkeleton("humanoid");
        system.Cosmetic += cosmetic => director.Play(cosmetic, new ZombieLook(skeleton, new RigPose(skeleton), Origin, MissingPartSet.None));

        var hits = new List<ZombieHit>();
        for (var round = 0; round < 3; round++)
        {
            foreach (var x in new[] { 0f, 10f, 20f, 30f })
            {
                foreach (var height in new[] { 1.6f, 1.0f, 0.4f })
                {
                    var hit = system.Hit(new Vector3(x - (round * 0.1f), height, -5), Vector3.UnitZ, 20f, DamageType.Pierce, 25 + (round * 20));
                    if (hit is not null)
                    {
                        hits.Add(hit);
                    }
                }
            }
        }

        var states = ids.Select(id => { Assert.True(world.TryGet(id, out var state)); return state.Zombie; }).ToList();
        var missing = ids.Select(id => { Assert.True(system.TryGetBody(id, out var body)); return body.MissingParts.Count; }).ToList();
        return (new Outcome(hits, states, missing), director);
    }

    [Fact]
    public void TheServersHitResults_AreIdentical_ForGoreOffLowAndHigh()
    {
        var (off, offDirector) = Scripted(GoreLevel.Off);
        var (low, lowDirector) = Scripted(GoreLevel.Low);
        var (high, highDirector) = Scripted(GoreLevel.High);

        Assert.NotEmpty(off.Hits);
        Assert.Contains(off.Hits, h => h.LostPart);
        Assert.Contains(off.Hits, h => h.Killed);
        Assert.Equal(off.Hits.Count, low.Hits.Count);
        Assert.Equal(off.Hits.Count, high.Hits.Count);
        for (var i = 0; i < off.Hits.Count; i++)
        {
            AssertSameHit(off.Hits[i], low.Hits[i]);
            AssertSameHit(off.Hits[i], high.Hits[i]);
        }

        Assert.Equal(off.States, low.States);
        Assert.Equal(off.States, high.States);
        Assert.Equal(off.Missing, low.Missing);
        Assert.Equal(off.Missing, high.Missing);

        // The visuals did differ, so the equality above is not because gore did nothing.
        Assert.Equal(0, offDirector.Decals.Count);
        Assert.True(lowDirector.Decals.Count > 0);
        Assert.True(highDirector.Decals.Count > lowDirector.Decals.Count);
    }

    private static void AssertSameHit(ZombieHit expected, ZombieHit actual)
    {
        Assert.Equal(expected.Entity, actual.Entity);
        Assert.Equal(expected.Part, actual.Part);
        Assert.Equal(expected.Distance, actual.Distance);
        Assert.Equal(expected.Point, actual.Point);
        Assert.Equal(expected.Killed, actual.Killed);
        Assert.Equal(expected.LostPart, actual.LostPart);
        Assert.Equal(expected.Events.Select(e => e.GetType()), actual.Events.Select(e => e.GetType()));
    }

    // ---- The setting ----

    [Fact]
    public void GoreDefaultsToLow_AndRoundTripsThroughTheOptionsFile()
    {
        Assert.Equal(GoreLevel.Low, new GameSettings().Gore);

        foreach (var level in Enum.GetValues<GoreLevel>())
        {
            Assert.True(SettingsJson.TryParse(SettingsJson.Serialize(new GameSettings { Gore = level }), out var loaded, out var error), error);
            Assert.Equal(level, loaded.Gore);
        }
    }

    [Theory]
    [InlineData("""{ "gore": "extreme" }""")]
    [InlineData("""{ "gore": 2 }""")]
    [InlineData("""{ "gore": "2" }""")]
    public void AnInvalidGoreSetting_IsRefusedWithAReason(string json)
    {
        Assert.False(SettingsJson.TryParse(json, out _, out var error));
        Assert.Contains("gore", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRangeGoreValue_FallsBackToLow()
    {
        Assert.Equal(GoreLevel.Low, (new GameSettings { Gore = (GoreLevel)9 }).Sanitized().Gore);
    }

    [Fact]
    public void TheEditor_CyclesGoreOffLowHigh_AndRaisesChanged()
    {
        var editor = new SettingsEditor(new GameSettings { Gore = GoreLevel.Off });
        var changes = 0;
        editor.Changed += () => changes++;

        editor.NextGore();
        Assert.Equal(GoreLevel.Low, editor.Current.Gore);
        editor.NextGore();
        Assert.Equal(GoreLevel.High, editor.Current.Gore);
        editor.NextGore();
        Assert.Equal(GoreLevel.Off, editor.Current.Gore);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void BothLanguages_HaveTheGoreLabelAndEveryLevel_AndTheOptionsScreenShowsIt()
    {
        var packages = Zombies.Engine.Core.Modding.DirectoryModSource.Read(Path.Combine(RigTestData.RepoRoot(), "mods"));
        var result = LocalizationLoader.Load(packages, Zombies.Domain.Mods.ModLoader.Load(packages));
        Assert.Empty(result.Problems);

        foreach (var language in new[] { "en", "sv" })
        {
            result.Localizer.Language = language;
            foreach (var key in new[] { "options.gore", "options.gore.off", "options.gore.low", "options.gore.high" })
            {
                Assert.NotEqual(key, result.Localizer.Get(key));
            }
        }

        var layout = File.ReadAllText(Path.Combine(RigTestData.RepoRoot(), "mods", "base", "ui", "options.json"));
        Assert.Contains("\"options.gore\"", layout, StringComparison.Ordinal);
    }
}
