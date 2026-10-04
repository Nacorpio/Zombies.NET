using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class ModifierTests
{
    private static readonly StatName Recoil = new("recoil");
    private static readonly StatName MoveSpeed = new("move_speed");

    private static readonly ModifierSource Suppressor = new("attachment:suppressor");
    private static readonly ModifierSource Grip = new("attachment:grip");
    private static readonly ModifierSource Painkiller = new("status_effect:painkiller");

    [Fact]
    public void EffectiveValue_WithoutModifiers_IsTheBaseValue()
    {
        var modifiers = new ModifierSet();

        Assert.Equal(10, modifiers.EffectiveValue(Recoil, 10));
    }

    [Fact]
    public void EffectiveValue_AddsEveryAddThenMultipliesByEveryMultiply()
    {
        var modifiers = new ModifierSet();
        modifiers.Add(new Modifier(Recoil, ModifierOperation.Multiply, 0.5, Suppressor));
        modifiers.Add(new Modifier(Recoil, ModifierOperation.Add, 2, Grip));
        modifiers.Add(new Modifier(Recoil, ModifierOperation.Add, -4, Suppressor));
        modifiers.Add(new Modifier(Recoil, ModifierOperation.Multiply, 3, Grip));

        // (10 + 2 - 4) * 0.5 * 3
        Assert.Equal(12, modifiers.EffectiveValue(Recoil, 10));
    }

    [Fact]
    public void EffectiveValue_OnlyUsesModifiersOnThatStat()
    {
        var modifiers = new ModifierSet();
        modifiers.Add(new Modifier(Recoil, ModifierOperation.Add, 5, Grip));
        modifiers.Add(new Modifier(MoveSpeed, ModifierOperation.Multiply, 1.5, Painkiller));

        Assert.Equal(15, modifiers.EffectiveValue(Recoil, 10));
        Assert.Equal(6, modifiers.EffectiveValue(MoveSpeed, 4));
    }

    [Fact]
    public void EffectiveValue_IsTheSameWhateverOrderModifiersWereAdded()
    {
        Modifier[] all =
        [
            new(Recoil, ModifierOperation.Add, 0.1, Grip),
            new(Recoil, ModifierOperation.Add, 0.2, Suppressor),
            new(Recoil, ModifierOperation.Add, 0.3, Painkiller),
            new(Recoil, ModifierOperation.Add, 1e16, Grip),
            new(Recoil, ModifierOperation.Add, -1e16, Suppressor),
            new(Recoil, ModifierOperation.Multiply, 1.1, Grip),
            new(Recoil, ModifierOperation.Multiply, 0.7, Painkiller),
            new(Recoil, ModifierOperation.Multiply, 1.3, Suppressor),
        ];

        double Effective(IEnumerable<Modifier> order)
        {
            var modifiers = new ModifierSet();
            foreach (var modifier in order)
            {
                modifiers.Add(modifier);
            }

            return modifiers.EffectiveValue(Recoil, 0.7);
        }

        var expected = Effective(all);
        Assert.Equal(expected, Effective(Enumerable.Reverse(all)));
        Assert.Equal(expected, Effective([.. all.Skip(3), .. all.Take(3)]));
        Assert.Equal(expected, Effective([all[4], all[0], all[7], all[3], all[5], all[1], all[6], all[2]]));
    }

    [Fact]
    public void RemoveSource_RemovesExactlyThatSourcesModifiers()
    {
        var modifiers = new ModifierSet();
        modifiers.Add(new Modifier(Recoil, ModifierOperation.Add, 5, Grip));
        modifiers.Add(new Modifier(Recoil, ModifierOperation.Multiply, 0.5, Suppressor));
        modifiers.Add(new Modifier(MoveSpeed, ModifierOperation.Add, -1, Suppressor));
        modifiers.Add(new Modifier(MoveSpeed, ModifierOperation.Multiply, 2, Painkiller));

        var removed = modifiers.RemoveSource(Suppressor);

        Assert.Equal(2, removed);
        Assert.Equal(15, modifiers.EffectiveValue(Recoil, 10));
        Assert.Equal(8, modifiers.EffectiveValue(MoveSpeed, 4));
        Assert.Equal(
            [new Modifier(Recoil, ModifierOperation.Add, 5, Grip), new Modifier(MoveSpeed, ModifierOperation.Multiply, 2, Painkiller)],
            modifiers.All);
    }

    [Fact]
    public void RemoveSource_ForAnUnknownSource_ChangesNothing()
    {
        var modifiers = new ModifierSet();
        modifiers.Add(new Modifier(Recoil, ModifierOperation.Add, 5, Grip));

        Assert.Equal(0, modifiers.RemoveSource(Painkiller));
        Assert.Equal(15, modifiers.EffectiveValue(Recoil, 10));
    }

    [Fact]
    public void Add_TheSameModifierTwice_AppliesItTwice()
    {
        var modifiers = new ModifierSet();
        var stim = new Modifier(MoveSpeed, ModifierOperation.Add, 1, Painkiller);
        modifiers.Add(stim);
        modifiers.Add(stim);

        Assert.Equal(6, modifiers.EffectiveValue(MoveSpeed, 4));
        Assert.Equal(2, modifiers.RemoveSource(Painkiller));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Modifier_RejectsNonFiniteValues(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Modifier(Recoil, ModifierOperation.Add, value, Grip));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Recoil")]
    [InlineData("move speed")]
    [InlineData("base:recoil")]
    public void StatName_RejectsInvalidNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new StatName(name));
    }

    [Fact]
    public void ModifierSource_RejectsBlankSources()
    {
        Assert.Throws<ArgumentException>(() => new ModifierSource(" "));
    }

    [Fact]
    public void Parse_ReadsADefinitionThatAppliesFromAnySource()
    {
        var definition = ModifierDefinitionJson.Parse("""
            { "id": "base:modifier/suppressor_recoil", "stat": "recoil", "operation": "multiply", "value": 0.8 }
            """);

        Assert.Equal("base:modifier/suppressor_recoil", definition.Id);
        Assert.Equal(new Modifier(Recoil, ModifierOperation.Multiply, 0.8, Suppressor), definition.From(Suppressor));
    }

    [Fact]
    public void Parse_ReadsAnAddOperation()
    {
        var definition = ModifierDefinitionJson.Parse("""
            { "id": "base:modifier/painkiller_speed", "stat": "move_speed", "operation": "add", "value": -0.5 }
            """);

        Assert.Equal(new Modifier(MoveSpeed, ModifierOperation.Add, -0.5, Painkiller), definition.From(Painkiller));
    }

    [Theory]
    [InlineData("""{ "id": "Bad Id", "stat": "recoil", "operation": "add", "value": 1 }""")]
    [InlineData("""{ "id": "base:modifier/x", "stat": "Recoil", "operation": "add", "value": 1 }""")]
    [InlineData("""{ "id": "base:modifier/x", "stat": "recoil", "operation": "divide", "value": 1 }""")]
    [InlineData("""{ "id": "base:modifier/x", "stat": "recoil", "operation": 0, "value": 1 }""")]
    [InlineData("""{ "id": "base:modifier/x", "stat": "recoil", "operation": "add", "value": "1" }""")]
    [InlineData("""{ "id": "base:modifier/x", "stat": "recoil", "operation": "add" }""")]
    [InlineData("""{ "id": "base:modifier/x", "operation": "add", "value": 1 }""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public void Parse_RejectsInvalidDefinitions(string json)
    {
        Assert.Throws<ModifierDefinitionException>(() => ModifierDefinitionJson.Parse(json));
    }
}
