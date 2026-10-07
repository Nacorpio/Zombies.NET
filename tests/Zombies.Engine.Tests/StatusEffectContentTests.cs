using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Zombies;
using Zombies.Engine.Core.Modding;

namespace Zombies.Engine.Tests;

/// <summary>The status effect content of the base mod and the checks that tie what names an effect to the effects that exist.</summary>
public sealed class StatusEffectContentTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static DefinitionRegistry BaseRegistry()
    {
        var mods = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods")));
        Assert.True(mods.IsSuccess, string.Join(Environment.NewLine, mods.Errors));
        return mods.Registry;
    }

    private static DefinitionRegistry Registry(params (string Path, string Json)[] files)
    {
        var package = new ModPackage("test", """{"id":"base","name":"base","version":"1.0.0","side":"both","dependencies":[]}""", [.. files.Select(f => new ModFile(f.Path, f.Json))]);
        var mods = ModLoader.Load([package]);
        Assert.True(mods.IsSuccess, string.Join(Environment.NewLine, mods.Errors));
        return mods.Registry;
    }

    private const string Poison = """{ "id": "base:status_effect/poison", "category": "ailment", "duration": 60 }""";

    private const string Zombie = """
        { "id": "base:zombie/biter", "stats": { "partHealth": 40, "damage": 10, "speed": 1.4 }, "senses": { "sight": 20, "hearing": 30 },
          "appearance": { "skinTones": ["#8fa07a"] }, "bite": { "effect": "base:status_effect/venom", "chance": 0.5 } }
        """;

    [Fact]
    public void TheBaseMod_LoadsItsEffects_AndEveryReferenceToOneResolves()
    {
        var catalog = StatusEffectContentLoader.Load(BaseRegistry());

        Assert.Equal(
            ["base:status_effect/food_poisoning", "base:status_effect/infection", "base:status_effect/painkiller"],
            catalog.All.Select(d => d.Id).Order());
    }

    [Fact]
    public void ZombiesThatBite_InfectWithAChanceTheirDataSets()
    {
        var registry = BaseRegistry();

        var bites = registry.OfKind("zombie").Select(d => ZombieTypeJson.Parse(d.Json)).ToDictionary(z => z.Id, z => z.Bite);

        Assert.Equal(new ZombieBite("base:status_effect/infection", 3000), bites["base:zombie/walker"]);
        Assert.Equal(new ZombieBite("base:status_effect/infection", 3000), bites["base:zombie/runner"]);
        Assert.Equal(new ZombieBite("base:status_effect/infection", 1500), bites["base:zombie/bloater"]);
    }

    [Fact]
    public void ConsumablesOfTheBaseMod_ApplyAndCureEffects()
    {
        var items = BaseRegistry().OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)).ToDictionary(i => i.Id.Value);

        Assert.Equal(new ConsumeEffect("base:status_effect/painkiller", 10_000), Assert.Single(items["base:item/painkillers"].OnConsume));
        Assert.Equal(new ConsumeEffect("base:status_effect/food_poisoning", 7500), Assert.Single(items["base:item/spoiled_food"].OnConsume));
        Assert.True(items["base:item/antibiotics"].Consumable);
        Assert.Empty(items["base:item/canned_beans"].OnConsume);
    }

    [Fact]
    public void AZombieBitingWithAnUnknownEffect_IsRefusedWhenTheModsLoad()
    {
        var registry = Registry(("data/status_effect/poison.json", Poison), ("data/zombie/biter.json", Zombie));

        var ex = Assert.Throws<ArgumentException>(() => StatusEffectContentLoader.Load(registry));

        Assert.Contains("base:zombie/biter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("base:status_effect/venom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemApplyingAnUnknownEffect_IsRefusedWhenTheModsLoad()
    {
        var registry = Registry(
            ("data/status_effect/poison.json", Poison),
            ("data/item/pill.json", """{ "id": "base:item/pill", "mass": "1 g", "volume": "1 ml", "edible": true, "onConsume": [ { "effect": "base:status_effect/venom" } ] }"""));

        var ex = Assert.Throws<ArgumentException>(() => StatusEffectContentLoader.Load(registry));

        Assert.Contains("base:item/pill", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEffectCuredByAnUnknownItem_IsRefusedWhenTheModsLoad()
    {
        var registry = Registry(("data/status_effect/poison.json", """{ "id": "base:status_effect/poison", "category": "ailment", "curedBy": { "items": [ "base:item/nothing" ] } }"""));

        var ex = Assert.Throws<ArgumentException>(() => StatusEffectContentLoader.Load(registry));

        Assert.Contains("base:item/nothing", ex.Message, StringComparison.Ordinal);
    }
}
