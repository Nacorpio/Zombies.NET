using System.Numerics;
using Zombies.Domain.Items;
using Zombies.Domain.Zombies;

namespace Zombies.Domain.Tests;

public sealed class WeakpointTests
{
    private static string Set(string weakpoints) => $$"""{ "id": "t:weakpoint_set/humanoid", "weakpoints": [ {{weakpoints}} ] }""";

    private static string Eyes(string extra = "") => $$"""
        { "name": "eyes", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.5 }, "size": 0.2, "criticalMultiplier": 3{{extra}} }
        """;

    private static readonly WeakpointSet Eyed = WeakpointSetJson.Parse(Set(Eyes(", \"rangedDifficulty\": 0.5, \"effect\": \"stagger\", \"effectThreshold\": 12, \"effectChance\": 0.25")));

    private static void AssertInvalid(string json, string expected) =>
        Assert.Contains(expected, Assert.Throws<ZombieTypeDefinitionException>(() => WeakpointSetJson.Parse(json)).Message, StringComparison.Ordinal);

    [Fact]
    public void Parse_ReadsTheRegionMultiplierDifficultiesAndEffect()
    {
        var eyes = Assert.Single(Eyed.Weakpoints);

        Assert.Equal("t:weakpoint_set/humanoid", Eyed.Id);
        Assert.Equal("eyes", eyes.Name);
        Assert.Equal(BodyPart.Head, eyes.Part);
        Assert.Equal(new Vector3(0.5f), eyes.Center);
        Assert.Equal(0.2f, eyes.Size);
        Assert.Equal(3, eyes.CriticalMultiplier);
        Assert.Equal(0.5, eyes.RangedDifficulty);
        Assert.Equal(0, eyes.MeleeDifficulty);
        Assert.Equal(WeakpointEffect.Stagger, eyes.Effect);
        Assert.Equal(12, eyes.EffectThreshold);
        Assert.Equal(2500, eyes.EffectChanceBasis);
    }

    [Fact]
    public void Find_ReturnsTheWeakpointARayPassesThrough_AndNothingForARayThatMisses()
    {
        // The region is 0.2 of the box around its center, so 0.4 to 0.6 on a melee attack, from the front along +Z.
        Assert.Equal("eyes", Eyed.Find(BodyPart.Head, new Vector3(0.5f, 0.5f, -1), Vector3.UnitZ, AttackKind.Melee)?.Name);
        Assert.Null(Eyed.Find(BodyPart.Head, new Vector3(0.65f, 0.5f, -1), Vector3.UnitZ, AttackKind.Melee));
        Assert.Null(Eyed.Find(BodyPart.Head, new Vector3(0.5f, 0.5f, -1), -Vector3.UnitZ, AttackKind.Melee));
        Assert.Null(Eyed.Find(BodyPart.Torso, new Vector3(0.5f, 0.5f, -1), Vector3.UnitZ, AttackKind.Melee));
    }

    [Fact]
    public void Find_ShrinksTheRegionByTheDifficultyOfTheAttackKind()
    {
        // Ranged difficulty 0.5 halves the cube to 0.45 to 0.55.
        Assert.Null(Eyed.Find(BodyPart.Head, new Vector3(0.58f, 0.5f, -1), Vector3.UnitZ, AttackKind.Ranged));
        Assert.NotNull(Eyed.Find(BodyPart.Head, new Vector3(0.58f, 0.5f, -1), Vector3.UnitZ, AttackKind.Melee));
    }

    [Fact]
    public void Find_PrefersTheWeakpointTheRayEntersFirst()
    {
        var set = WeakpointSetJson.Parse(Set(
            """
            { "name": "back", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.8 }, "size": 0.2 },
            { "name": "front", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.2 }, "size": 0.2 }
            """));

        Assert.Equal("front", set.Find(BodyPart.Head, new Vector3(0.5f, 0.5f, 0), Vector3.UnitZ, AttackKind.Melee)?.Name);
        Assert.Equal("back", set.Find(BodyPart.Head, new Vector3(0.5f, 0.5f, 1), -Vector3.UnitZ, AttackKind.Melee)?.Name);
    }

    [Theory]
    [InlineData("""{ "name": "eyes", "part": "head", "center": { "x": 0.5, "y": 1.5, "z": 0.5 }, "size": 0.2 }""", "center")]
    [InlineData("""{ "name": "eyes", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.5 }, "size": 0 }""", "size")]
    [InlineData("""{ "name": "eyes", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.5 }, "size": 0.2, "criticalMultiplier": 0.5 }""", "multiplier")]
    [InlineData("""{ "name": "eyes", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.5 }, "size": 0.2, "meleeDifficulty": 1 }""", "difficulty")]
    [InlineData("""{ "name": "eyes", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.5 }, "size": 0.2, "effectChance": 0.5 }""", "no effect")]
    [InlineData("""{ "name": "eyes", "part": "head", "center": { "x": 0.5, "y": 0.5, "z": 0.5 }, "size": 0.2, "effect": "explode" }""", "explode")]
    [InlineData("""{ "name": "eyes", "part": "nose", "center": { "x": 0.5, "y": 0.5, "z": 0.5 }, "size": 0.2 }""", "nose")]
    public void AWeakpointThatMakesNoSense_IsRejectedAtLoad(string weakpoint, string expected)
    {
        AssertInvalid(Set(weakpoint), expected);
    }

    [Fact]
    public void ASetNeedsWeakpointsWithDistinctNames()
    {
        AssertInvalid(Set(string.Empty), "1 to");
        AssertInvalid(Set($"{Eyes()}, {Eyes()}"), "twice");
    }

    [Fact]
    public void AZombieTypeNamesItsSetByContentId()
    {
        const string Zombie = """
            {
              "id": "t:zombie/walker",
              "stats": { "partHealth": 40, "damage": 10, "speed": 1.4 },
              "senses": { "sight": 20, "hearing": 30 },
              "appearance": { "skinTones": ["#8fa07a"] },
              "weakpointSet": "t:weakpoint_set/humanoid"
            }
            """;

        Assert.Equal("t:weakpoint_set/humanoid", ZombieTypeJson.Parse(Zombie).WeakpointSet);
        Assert.Throws<ZombieTypeDefinitionException>(() => ZombieTypeJson.Parse(Zombie.Replace("t:weakpoint_set/humanoid", "Not An Id", StringComparison.Ordinal)));
    }
}
