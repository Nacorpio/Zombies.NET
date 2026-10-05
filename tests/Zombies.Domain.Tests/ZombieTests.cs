using Zombies.Domain.Items;
using Zombies.Domain.Zombies;

namespace Zombies.Domain.Tests;

public sealed class ZombieTests
{
    private const string Walker = """
        {
          "id": "t:zombie/walker",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4, "maxLevel": 5, "perLevelBonus": 0.5 },
          "senses": { "sight": 20, "hearing": 30 },
          "traits": [ { "trait": "t:trait/shambler", "values": { "speedMultiplier": 0.8 } } ],
          "appearance": {
            "height": { "min": 0.9, "max": 1.1 },
            "build": { "min": 0.8, "max": 1.2 },
            "skinTones": ["#8fa07a", "#9aa88a", "#7d9272"]
          },
          "outfit": {
            "clothing": [
              { "item": "t:item/shirt", "weight": 3 },
              { "item": "t:item/jeans", "weight": 3 },
              { "item": "t:item/coat", "weight": 1 },
              { "weight": 1 }
            ],
            "clothingCount": { "min": 1, "max": 3 },
            "headwear": [ { "weight": 3 }, { "item": "t:item/cap", "weight": 1 } ],
            "backpacks": [ { "weight": 3 }, { "item": "t:item/pack", "weight": 1 } ]
          },
          "missingParts": [ { "part": "leftArm", "chance": 0.25 }, { "part": "rightLeg", "chance": 0.25 } ]
        }
        """;

    private static readonly ZombieTypeDefinition WalkerType = ZombieTypeJson.Parse(Walker);

    private static ZombieSpec Spec(ulong seed, int level = 1) => new(seed, "t:zombie/walker", level);

    [Fact]
    public void Parse_ReadsStatsTraitsAppearanceAndOutfit()
    {
        Assert.Equal("t:zombie/walker", WalkerType.Id);
        Assert.Equal(1.4, WalkerType.Speed);
        Assert.Equal(5, WalkerType.TopLevel);
        var trait = Assert.Single(WalkerType.Traits);
        Assert.Equal("t:trait/shambler", trait.Trait);
        Assert.Equal(0.8, trait.Values["speedMultiplier"]);
        Assert.Equal((900, 1100), WalkerType.HeightPermille);
        Assert.Equal(3, WalkerType.SkinTones.Count);
        Assert.Equal(0x8fa07au, WalkerType.SkinTones[0]);
        Assert.Equal(4, WalkerType.Outfit.Clothing.Count);
        Assert.Null(WalkerType.Outfit.Clothing[3].Item);
        Assert.Equal([BodyPart.LeftArm, BodyPart.RightLeg], WalkerType.MissingParts.Select(m => m.Part));
        Assert.Equal(2500, WalkerType.MissingParts[0].Basis);
    }

    [Fact]
    public void Stats_GrowWithLevel_AndStopAtTheTopLevel()
    {
        Assert.Equal(40, WalkerType.PartHealthAt(1));
        Assert.Equal(60, WalkerType.PartHealthAt(2));
        Assert.Equal(120, WalkerType.PartHealthAt(5));
        Assert.Equal(120, WalkerType.PartHealthAt(99));
        Assert.Equal(15, WalkerType.DamageAt(2));
    }

    [Theory]
    [InlineData("\"id\": \"t:zombie/walker\"", "\"id\": \"Walker\"")]
    [InlineData("\"partHealth\": 40", "\"partHealth\": 0")]
    [InlineData("\"maxLevel\": 5", "\"maxLevel\": 0")]
    [InlineData("\"skinTones\": [\"#8fa07a\", \"#9aa88a\", \"#7d9272\"]", "\"skinTones\": []")]
    [InlineData("\"#8fa07a\"", "\"green\"")]
    [InlineData("\"part\": \"leftArm\"", "\"part\": \"torso\"")]
    [InlineData("\"part\": \"leftArm\"", "\"part\": \"tail\"")]
    [InlineData("\"chance\": 0.25 }, { \"part\": \"rightLeg\"", "\"chance\": 1.5 }, { \"part\": \"rightLeg\"")]
    [InlineData("\"trait\": \"t:trait/shambler\"", "\"trait\": \"shambler\"")]
    [InlineData("\"min\": 0.9, \"max\": 1.1", "\"min\": 1.2, \"max\": 1.1")]
    [InlineData("{ \"item\": \"t:item/cap\", \"weight\": 1 }", "{ \"item\": \"t:item/cap\", \"weight\": 0 }")]
    [InlineData("\"clothingCount\": { \"min\": 1, \"max\": 3 }", "\"clothingCount\": { \"min\": 3, \"max\": 1 }")]
    public void Parse_RejectsDefinitionsThatBreakARule(string original, string replacement)
    {
        Assert.Contains(original, Walker, StringComparison.Ordinal);

        Assert.Throws<ZombieTypeDefinitionException>(() => ZombieTypeJson.Parse(Walker.Replace(original, replacement, StringComparison.Ordinal)));
    }

    [Fact]
    public void Parse_RejectsMissingRequiredFieldsAndNonObjects()
    {
        Assert.Throws<ZombieTypeDefinitionException>(() => ZombieTypeJson.Parse("""{ "id": "t:zombie/x" }"""));
        Assert.Throws<ZombieTypeDefinitionException>(() => ZombieTypeJson.Parse("null"));
        Assert.Throws<ZombieTypeDefinitionException>(() => ZombieTypeJson.Parse("[]"));
    }

    [Fact]
    public void Generate_GivesTheSameAppearanceForTheSameSpec()
    {
        for (ulong seed = 0; seed < 100; seed++)
        {
            var first = ZombieGenerator.Generate(WalkerType, Spec(seed, 3));
            var second = ZombieGenerator.Generate(WalkerType, Spec(seed, 3));

            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void Generate_VariesWithTheSeed()
    {
        var looks = Enumerable.Range(0, 100).Select(i => ZombieGenerator.Generate(WalkerType, Spec((ulong)i))).Distinct().ToList();

        Assert.True(looks.Count > 50, $"Only {looks.Count} of 100 seeds looked different.");
    }

    [Fact]
    public void Generate_KeepsEveryChoiceInsideWhatTheTypeAllows()
    {
        var items = new HashSet<string>(["t:item/shirt", "t:item/jeans", "t:item/coat"]);
        var sawHat = false;
        var sawPack = false;
        for (ulong seed = 0; seed < 500; seed++)
        {
            var look = ZombieGenerator.Generate(WalkerType, Spec(seed));

            Assert.InRange(look.HeightPermille, 900, 1100);
            Assert.InRange(look.BuildPermille, 800, 1200);
            Assert.Contains(look.SkinColor, WalkerType.SkinTones);
            Assert.InRange(look.Clothing.Count, 0, 3);
            Assert.All(look.Clothing, c => Assert.Contains(c.Value, items));
            Assert.Equal(look.Clothing.Count, look.Clothing.Distinct().Count());
            Assert.All(look.MissingParts, p => Assert.Contains(p, new[] { BodyPart.LeftArm, BodyPart.RightLeg }));
            sawHat |= look.Headwear == new ItemId("t:item/cap");
            sawPack |= look.Backpack == new ItemId("t:item/pack");
        }

        Assert.True(sawHat && sawPack);
    }

    [Fact]
    public void Generate_MissingPartsFollowTheirChance()
    {
        var never = ZombieTypeJson.Parse(Walker.Replace("0.25", "0", StringComparison.Ordinal));
        var always = ZombieTypeJson.Parse(Walker.Replace("0.25", "1", StringComparison.Ordinal));
        var some = 0;
        for (ulong seed = 0; seed < 400; seed++)
        {
            Assert.Empty(ZombieGenerator.Generate(never, Spec(seed)).MissingParts);
            Assert.Equal([BodyPart.LeftArm, BodyPart.RightLeg], ZombieGenerator.Generate(always, Spec(seed)).MissingParts);
            some += ZombieGenerator.Generate(WalkerType, Spec(seed)).MissingParts.Count(p => p == BodyPart.LeftArm);
        }

        Assert.InRange(some, 60, 140);
    }

    [Fact]
    public void Generate_HasTheSameLookAtEveryLevel()
    {
        Assert.Equal(ZombieGenerator.Generate(WalkerType, Spec(9, 1)), ZombieGenerator.Generate(WalkerType, Spec(9, 5)));
    }

    [Fact]
    public void Generate_RefusesASpecForAnotherTypeOrALevelOutOfRange()
    {
        Assert.Throws<ArgumentException>(() => ZombieGenerator.Generate(WalkerType, new ZombieSpec(1, "t:zombie/other", 1)));
        Assert.Throws<ArgumentException>(() => ZombieGenerator.Generate(WalkerType, Spec(1, 0)));
        Assert.Throws<ArgumentException>(() => ZombieGenerator.Generate(WalkerType, Spec(1, 6)));
    }

    [Fact]
    public void WornItems_AreClothingHeadwearAndBackpack()
    {
        var look = new ZombieAppearance(1000, 1000, 0, [new ItemId("t:item/shirt")], new ItemId("t:item/cap"), new ItemId("t:item/pack"), []);

        Assert.Equal(["t:item/shirt", "t:item/cap", "t:item/pack"], look.WornItems.Select(i => i.Value));
        Assert.Empty(new ZombieAppearance(1000, 1000, 0, [], null, null, []).WornItems);
    }

    [Fact]
    public void Catalog_NumbersTypesByContentIdOrder_AndRefusesDuplicates()
    {
        var other = ZombieTypeJson.Parse(Walker.Replace("t:zombie/walker", "t:zombie/another", StringComparison.Ordinal));
        var catalog = new ZombieCatalog([WalkerType, other]);

        Assert.Equal(0, catalog.IndexOf("t:zombie/another"));
        Assert.Equal(1, catalog.IndexOf("t:zombie/walker"));
        Assert.Equal(-1, catalog.IndexOf("t:zombie/none"));
        Assert.Same(WalkerType, catalog.At(1));
        Assert.True(catalog.TryGet("t:zombie/walker", out var found));
        Assert.Same(WalkerType, found);
        Assert.False(catalog.TryGet("t:zombie/none", out _));
        Assert.Throws<ArgumentException>(() => new ZombieCatalog([WalkerType, WalkerType]));
    }
}
