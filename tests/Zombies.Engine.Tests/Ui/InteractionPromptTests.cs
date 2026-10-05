using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class InteractionPromptTests
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

    private static (Localizer Localizer, DefinitionRegistry Registry) LoadRepository()
    {
        var packages = DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods"));
        var mods = ModLoader.Load(packages);
        return (LocalizationLoader.Load(packages, mods).Localizer, mods.Registry);
    }

    [Fact]
    public void ForContainer_IsTitledWithTheKindNamesTheKeyAndIsCaptionedWithTheRoom()
    {
        var (localizer, _) = LoadRepository();

        var tooltip = InteractionPrompt.ForContainer(localizer, "cabinet", "base:area_type/kitchen", Key.F);

        Assert.Equal("Cabinet", tooltip.Title);
        Assert.Equal(["Press F to open"], tooltip.Lines);
        Assert.Equal("Kitchen", tooltip.Caption);
    }

    [Fact]
    public void ForContainer_FollowsTheChosenLanguage()
    {
        var (localizer, _) = LoadRepository();
        Assert.True(localizer.TrySetLanguage("sv"));

        var tooltip = InteractionPrompt.ForContainer(localizer, "wardrobe", "base:area_type/bedroom", Key.E);

        Assert.Equal("Garderob", tooltip.Title);
        Assert.Equal(["Tryck E för att öppna"], tooltip.Lines);
        Assert.Equal("Sovrum", tooltip.Caption);
    }

    [Fact]
    public void Keys_FollowTheSameNamingAsItems()
    {
        Assert.Equal("container.kind.fridge", InteractionPrompt.ContainerKindKey("fridge"));
        Assert.Equal("area_type.base.area_type.kitchen", InteractionPrompt.AreaTypeKey("base:area_type/kitchen"));
    }

    [Fact]
    public void EveryContainerKindAndAreaTypeInTheBaseGame_HasANameInEveryLanguage()
    {
        var (localizer, registry) = LoadRepository();
        var content = SettlementContentLoader.Load(registry);
        var kinds = content.Structures.SelectMany(s => s.Containers.Select(c => c.ContainerKind)).Distinct().ToList();
        var areaTypes = registry.OfKind("area_type").Select(d => d.Id.Value).ToList();

        Assert.NotEmpty(kinds);
        Assert.NotEmpty(areaTypes);
        foreach (var language in localizer.Languages)
        {
            Assert.True(localizer.TrySetLanguage(language));
            foreach (var kind in kinds)
            {
                var key = InteractionPrompt.ContainerKindKey(kind);
                Assert.NotEqual(key, localizer.Get(key));
            }

            foreach (var areaType in areaTypes.Where(a => a.StartsWith("base:", StringComparison.Ordinal)))
            {
                var key = InteractionPrompt.AreaTypeKey(areaType);
                Assert.NotEqual(key, localizer.Get(key));
            }

            Assert.NotEqual("action.interact", localizer.Get("action.interact"));
        }
    }

    [Fact]
    public void Interact_IsBoundToFByDefault_AndNoOtherActionUsesIt()
    {
        var keys = KeyBindings.Defaults();

        Assert.Equal(Key.F, keys.KeyFor(GameAction.Interact));
        Assert.Equal(GameAction.Interact, keys.ActionFor(Key.F));
    }

    [Fact]
    public void Caption_IsKeptShortAndAddsARowToTheTooltipBox()
    {
        var plain = Tooltip.Create("Cabinet", "Press F to open");
        var captioned = Tooltip.Create("Cabinet", "Press F to open", "Kitchen");
        var screen = new UiRect(0, 0, 800, 600);

        Assert.Null(plain.Caption);
        Assert.Equal("Kitchen", captioned.Caption);
        Assert.True(captioned.Place(100, 100, screen, 1).Height > plain.Place(100, 100, screen, 1).Height);
        Assert.Equal(Tooltip.MaxCharsPerLine, Tooltip.Create(null, "x", new string('k', 100)).Caption!.Length);
    }

    [Fact]
    public void Caption_WidensTheBoxWhenItIsTheLongestLine()
    {
        var plain = Tooltip.Create("Hi", "Ok");
        var captioned = Tooltip.Create("Hi", "Ok", "A long caption");
        var screen = new UiRect(0, 0, 800, 600);

        Assert.True(captioned.Place(0, 0, screen, 1).Width > plain.Place(0, 0, screen, 1).Width);
    }

    [Fact]
    public void DrawTooltip_DrawsTheCaptionToo()
    {
        var palette = UiPalette.For(UiPaletteKind.Standard);
        var screen = new UiRect(0, 0, 400, 300);
        var without = new SpriteBatch();
        var with = new SpriteBatch();

        UiRenderer.DrawTooltip(without, Tooltip.Create("Cabinet", "Press F to open"), palette, 50, 50, screen, 1);
        UiRenderer.DrawTooltip(with, Tooltip.Create("Cabinet", "Press F to open", "Kitchen"), palette, 50, 50, screen, 1);

        Assert.True(without.QuadCount > 0);
        Assert.True(with.QuadCount > without.QuadCount);
    }

    [Fact]
    public void LookingAtAContainer_GivesItsPrompt_AndLookingAwayGivesNone()
    {
        var (localizer, _) = LoadRepository();
        var cabinet = new WorldContainer(10, 60, 20, "cabinet", "main", "base:area_type/bathroom", 3, 5);
        var eye = new Vector3(10.5f, 60.5f, 18.5f);

        var target = ContainerTargeting.Find([cabinet], eye, Vector3.UnitZ);
        var away = ContainerTargeting.Find([cabinet], eye, -Vector3.UnitZ);

        Assert.NotNull(target);
        var tooltip = InteractionPrompt.ForContainer(localizer, target.ContainerKind, target.AreaType, KeyBindings.Defaults().KeyFor(GameAction.Interact));
        Assert.Equal(("Cabinet", "Bathroom"), (tooltip.Title, tooltip.Caption));
        Assert.Null(away);
    }
}
