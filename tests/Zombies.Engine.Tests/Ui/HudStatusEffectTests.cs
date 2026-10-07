using Zombies.Domain.Combat;
using Zombies.Domain.Mods;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.Survival;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

/// <summary>What the HUD lists for the player's Status effects, decided in HudModel so it can be tested without a window.</summary>
public sealed class HudStatusEffectTests
{
    private const string Infection = "base:status_effect/infection";
    private const string FoodPoisoning = "base:status_effect/food_poisoning";
    private const string Painkiller = "base:status_effect/painkiller";

    private static readonly string[] PoisonAndAntidote =
    [
        """{ "id": "t:status_effect/poison", "category": "ailment", "duration": 60, "curedBy": { "effects": [ "t:status_effect/antidote" ] } }""",
        """{ "id": "t:status_effect/antidote", "category": "buff", "duration": 10 }""",
    ];

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private sealed record Content(StatusEffectCatalog Effects, Localizer Localizer, ModLoadResult Mods);

    private static Content LoadBase()
    {
        var packages = DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods"));
        var mods = ModLoader.Load(packages);
        Assert.True(mods.IsSuccess, string.Join(Environment.NewLine, mods.Errors));
        var localization = LocalizationLoader.Load(packages, mods);
        Assert.Empty(localization.Problems);
        return new Content(StatusEffectContentLoader.Load(mods.Registry), localization.Localizer, mods);
    }

    private static ActiveEffect Active(string effect, string? stage = null, int stacks = 1, TimeSpan? remaining = null) =>
        new(effect, stacks, TimeSpan.Zero, remaining, stage);

    private static HudModel Hud(Content content, params ActiveEffect[] active) =>
        new(new Body(new BodyId(1)), new Needs(), null, null, content.Localizer, effects: active, effectCatalog: content.Effects);

    [Fact]
    public void WithNoEffects_TheListIsEmpty()
    {
        Assert.Empty(Hud(LoadBase()).StatusEffects);
    }

    [Fact]
    public void AnInfection_IsListedWithItsNameStageAndCureHint()
    {
        var content = LoadBase();

        var effect = Assert.Single(Hud(content, Active(Infection, "severe")).StatusEffects);

        Assert.Equal("Infection", effect.Name);
        Assert.Equal("severe", effect.Stage);
        Assert.Equal("Infection: Severe", effect.Label);
        Assert.Equal("Cure: Antibiotics", effect.CureHint);
        Assert.Equal("Infection", effect.Tooltip.Title);
        Assert.Contains("Stage: Severe.", string.Join(' ', effect.Tooltip.Lines));
        Assert.Equal("Cure: Antibiotics", effect.Tooltip.Caption);
    }

    [Fact]
    public void TheTooltipNamesTheEffectItsStageAndWhatCuresIt_InSwedishToo()
    {
        var content = LoadBase();
        content.Localizer.Language = "sv";

        var effect = Assert.Single(Hud(content, Active(Infection, "mild")).StatusEffects);

        Assert.Equal("Infektion", effect.Tooltip.Title);
        Assert.Contains("Stadium: Lindrigt.", string.Join(' ', effect.Tooltip.Lines));
        Assert.Equal("Botas med: Antibiotika", effect.Tooltip.Caption);
        Assert.Equal("Infektion: Lindrigt", effect.Label);
    }

    [Fact]
    public void AnEffectThatEndsByItself_SaysThatInsteadOfACure()
    {
        var content = LoadBase();

        var poisoning = Assert.Single(Hud(content, Active(FoodPoisoning, remaining: TimeSpan.FromSeconds(90))).StatusEffects);

        Assert.Equal("Food poisoning", poisoning.Name);
        Assert.Null(poisoning.Stage);
        Assert.Equal("Wears off with time", poisoning.CureHint);
        Assert.Equal(PaletteRole.Warning, poisoning.Role);
    }

    [Fact]
    public void AnAilmentTurnsDangerousAtItsLastStage_AndABuffIsGood()
    {
        var content = LoadBase();

        var roles = Hud(content, Active(Infection, "mild"), Active(Painkiller, remaining: TimeSpan.FromSeconds(100))).StatusEffects.ToDictionary(e => e.Effect, e => e.Role);
        var severe = Assert.Single(Hud(content, Active(Infection, "severe")).StatusEffects);

        Assert.Equal(PaletteRole.Warning, roles[Infection]);
        Assert.Equal(PaletteRole.Good, roles[Painkiller]);
        Assert.Equal(PaletteRole.Danger, severe.Role);
    }

    [Fact]
    public void AilmentsListBeforeBuffs_AndTheWorstFirst()
    {
        var content = LoadBase();

        var list = Hud(content, Active(Painkiller), Active(FoodPoisoning), Active(Infection, "severe")).StatusEffects;

        Assert.Equal([Infection, FoodPoisoning, Painkiller], list.Select(e => e.Effect));
    }

    [Fact]
    public void StackedEffects_ShowTheirCount()
    {
        var content = LoadBase();

        var effect = Assert.Single(Hud(content, Active(Painkiller, stacks: 3)).StatusEffects);

        Assert.EndsWith(" x3", effect.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTooltipOfTheBaseMod_FitsTheTooltipAtAGlance_InBothLanguages()
    {
        var content = LoadBase();
        foreach (var language in new[] { "en", "sv" })
        {
            content.Localizer.Language = language;
            foreach (var definition in content.Effects.All)
            {
                foreach (var stage in definition.Stages.Select(s => s.Name).DefaultIfEmpty(null))
                {
                    var effect = Assert.Single(Hud(content, Active(definition.Id, stage)).StatusEffects);

                    Assert.True(effect.Tooltip.Lines.Count <= Tooltip.MaxLines, $"{language} {definition.Id} {stage}");
                    Assert.DoesNotContain(effect.Tooltip.Lines, l => l.EndsWith("...", StringComparison.Ordinal));
                    Assert.NotNull(effect.Tooltip.Caption);
                    Assert.DoesNotContain("...", effect.Tooltip.Caption, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void EveryEffectOfTheBaseMod_HasEnglishAndSwedishNamesTooltipsStagesAndCureNames()
    {
        var content = LoadBase();
        var keys = new List<string>
        {
            "hud.effect.stage", "hud.effect.cure_item", "hud.effect.cure_effect", "hud.effect.cure_time", "hud.effect.cure_none",
        };
        foreach (var definition in content.Effects.All)
        {
            var key = $"status_effect.{definition.Id.Replace(':', '.').Replace('/', '.')}";
            keys.Add(key);
            keys.Add($"{key}.tooltip");
            keys.AddRange(definition.Stages.Select(s => $"{key}.stage.{s.Name}"));
            keys.AddRange(definition.CuredByItems.Select(i => $"item.{i.Value.Replace(':', '.').Replace('/', '.')}"));
        }

        foreach (var item in content.Mods.Registry.OfKind("item").Where(d => d.Json.Contains("onConsume", StringComparison.Ordinal)))
        {
            keys.Add($"item.{item.Id.Value.Replace(':', '.').Replace('/', '.')}");
        }

        foreach (var language in new[] { "en", "sv" })
        {
            content.Localizer.Language = language;
            foreach (var key in keys)
            {
                Assert.NotEqual(key, content.Localizer.Get(key));
            }
        }

        Assert.Empty(content.Localizer.MissingKeys("sv"));
    }

    [Fact]
    public void AnEffectCuredByAnotherEffect_NamesThatEffect()
    {
        var effects = new StatusEffectCatalog(PoisonAndAntidote.Select(StatusEffectJson.Parse));
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "hud.effect.cure_effect": "Cured by: {0}",
              "status_effect.t.status_effect.poison": "Poison",
              "status_effect.t.status_effect.poison.tooltip": "Hurts.",
              "status_effect.t.status_effect.antidote": "Antidote" } }
            """, out var table, out var error), error);
        var hud = new HudModel(new Body(new BodyId(1)), new Needs(), null, null, new Localizer([table]), effects: [Active("t:status_effect/poison")], effectCatalog: effects);

        var effect = Assert.Single(hud.StatusEffects);

        Assert.Equal("Cured by: Antidote", effect.CureHint);
    }
}
