using Zombies.Engine.Core.Modding;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class ContextMenuTests
{
    private static readonly UiRect Screen = new(0, 0, 1280, 720);

    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "m.use": "Use", "m.equip": "Equip", "m.drop": "Drop", "m.inspect": "Inspect",
              "m.reason": "That cannot be worn" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private static List<ContextMenuEntry> Entries() =>
    [
        new("use", "m.use", IconNames.Use, "F"),
        new("equip", "m.equip", IconNames.Equip, null, DisabledReasonKey: "m.reason"),
        new("drop", "m.drop", IconNames.Drop, "G", StartsGroup: true),
        new("inspect", "m.inspect", IconNames.Inspect),
    ];

    private static ContextMenu Build() => ContextMenu.Create(English(), Entries());

    private static UiRect Row(ContextMenu menu, int index) => menu.Items[index].Bounds;

    private static (float X, float Y) Center(UiRect r) => (r.X + (r.Width / 2), r.Y + (r.Height / 2));

    [Fact]
    public void Create_GivesEachEntryItsIconLabelShortcutAndState()
    {
        var menu = Build();

        Assert.Equal(["Use", "Equip", "Drop", "Inspect"], menu.Items.Select(i => i.Label));
        Assert.Equal(IconNames.Use, menu.Items[0].Icon);
        Assert.Equal("F", menu.Items[0].Shortcut);
        Assert.Null(menu.Items[1].Shortcut);
        Assert.True(menu.Items[0].IsEnabled);
        Assert.False(menu.Items[1].IsEnabled);
        Assert.Equal("That cannot be worn", menu.Items[1].DisabledReason);
    }

    [Fact]
    public void Create_WithoutEntries_Throws() =>
        Assert.Throws<ArgumentException>(() => ContextMenu.Create(English(), []));

    [Fact]
    public void Groups_AreSeparatedByAThinDividerAndNothingElse()
    {
        var menu = Build();
        menu.Arrange(100, 100, Screen, 1);

        Assert.Null(menu.Items[0].Divider);
        Assert.Null(menu.Items[1].Divider);
        Assert.NotNull(menu.Items[2].Divider);
        var divider = menu.Items[2].Divider!.Value;
        Assert.Equal(1, divider.Height);
        Assert.Null(menu.Items[3].Divider);
        Assert.True(divider.Bottom <= Row(menu, 2).Y && divider.Y >= Row(menu, 1).Bottom);
    }

    [Fact]
    public void Groups_ADividerAboveTheFirstEntryIsIgnored()
    {
        var menu = ContextMenu.Create(English(), [new("use", "m.use", IconNames.Use, StartsGroup: true), new("drop", "m.drop", IconNames.Drop)]);
        menu.Arrange(0, 0, Screen, 1);

        Assert.Null(menu.Items[0].Divider);
    }

    [Fact]
    public void Arrange_OpensWithItsTopLeftAtThePointerWhenThereIsRoom()
    {
        var menu = Build();
        menu.Arrange(100, 200, Screen, 1);

        Assert.Equal(100, menu.Bounds.X);
        Assert.Equal(200, menu.Bounds.Y);
        Assert.Equal(1, menu.EffectiveTextScale);
    }

    [Fact]
    public void Arrange_RowsStackInsideTheMenuWithoutOverlap()
    {
        var menu = Build();
        menu.Arrange(100, 100, Screen, 2);

        for (var i = 0; i < menu.Items.Count; i++)
        {
            var row = Row(menu, i);
            Assert.True(row.X >= menu.Bounds.X && row.Right <= menu.Bounds.Right);
            Assert.True(row.Y >= menu.Bounds.Y && row.Bottom <= menu.Bounds.Bottom);
            if (i > 0)
            {
                Assert.True(row.Y >= Row(menu, i - 1).Bottom);
            }
        }
    }

    public static TheoryData<int, int, int> Windows() => new()
    {
        { 1280, 720, 1 }, { 1920, 1080, 2 }, { 3840, 2160, 4 }, { 800, 600, 1 }, { 640, 480, 2 }, { 640, 480, 4 }, { 480, 360, 3 }, { 320, 240, 1 }, { 320, 240, 4 },
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public void Arrange_StaysFullyOnScreen_WhereverThePointerIs(int width, int height, int scale)
    {
        var screen = new UiRect(0, 0, width, height);
        float[] fractions = [0f, 0.25f, 0.5f, 0.9f, 1f];
        foreach (var fx in fractions)
        {
            foreach (var fy in fractions)
            {
                var menu = Build();
                menu.Arrange(fx * width, fy * height, screen, scale);

                var b = menu.Bounds;
                Assert.True(b.X >= 0 && b.Y >= 0 && b.Right <= width && b.Bottom <= height, $"{width}x{height} scale {scale} pointer {fx},{fy} gave {b}");
                Assert.All(menu.Items, i => Assert.True(i.Bounds.Right <= b.Right && i.Bounds.Bottom <= b.Bottom));
            }
        }
    }

    [Fact]
    public void Arrange_NearTheRightAndBottomEdges_OpensLeftOfAndAboveThePointer()
    {
        var menu = Build();
        menu.Arrange(1270, 710, Screen, 1);

        Assert.Equal(1270, menu.Bounds.Right);
        Assert.Equal(710, menu.Bounds.Bottom);
    }

    [Fact]
    public void Arrange_APointerOutsideTheScreen_IsClampedBackIn()
    {
        var menu = Build();
        menu.Arrange(-50, 9999, Screen, 1);

        Assert.True(menu.Bounds.X >= 0 && menu.Bounds.Bottom <= Screen.Height);
    }

    [Fact]
    public void Arrange_ALargeScaleOnASmallWindow_ShrinksTheTextScaleToFit()
    {
        var menu = Build();
        menu.Arrange(0, 0, new UiRect(0, 0, 320, 240), 4);

        Assert.True(menu.EffectiveTextScale < 4);
        Assert.True(menu.Bounds.Width <= 320 && menu.Bounds.Height <= 240);
    }

    [Fact]
    public void Arrange_AWindowSmallerThanTheMenu_ShowsItFromTheTopLeftCorner()
    {
        var menu = Build();
        menu.Arrange(50, 50, new UiRect(0, 0, 100, 100), 1);

        Assert.Equal(0, menu.Bounds.X);
        Assert.Equal(0, menu.Bounds.Y);
    }

    [Fact]
    public void Arrange_AScreenNotAtTheOrigin_KeepsTheMenuInsideIt()
    {
        var menu = Build();
        var screen = new UiRect(100, 50, 400, 300);
        menu.Arrange(490, 340, screen, 1);

        Assert.True(menu.Bounds.X >= screen.X && menu.Bounds.Right <= screen.Right && menu.Bounds.Y >= screen.Y && menu.Bounds.Bottom <= screen.Bottom);
    }

    [Fact]
    public void Move_DownAndUp_WrapAroundTheEnds()
    {
        var menu = Build();

        menu.Move(1);
        Assert.Equal(0, menu.HighlightedIndex);
        menu.Move(-1);
        Assert.Equal(3, menu.HighlightedIndex);
        menu.Move(1);
        Assert.Equal(0, menu.HighlightedIndex);
    }

    [Fact]
    public void Move_UpFromNothing_GoesToTheLastEntry()
    {
        var menu = Build();

        menu.Move(-1);

        Assert.Equal(3, menu.HighlightedIndex);
    }

    [Fact]
    public void Choose_AnEnabledEntry_ReturnsItsId_AndADisabledOneNothing()
    {
        var menu = Build();

        menu.Move(1);
        Assert.Equal("use", menu.Choose());
        menu.Move(1);
        Assert.Equal("equip", menu.Highlighted?.Id);
        Assert.Null(menu.Choose());
    }

    [Fact]
    public void Hover_HighlightsTheEntryUnderThePointer_AndKeepsItOverADivider()
    {
        var menu = Build();
        menu.Arrange(100, 100, Screen, 1);

        var (x, y) = Center(Row(menu, 2));
        menu.Hover(x, y);
        Assert.Equal(2, menu.HighlightedIndex);

        var divider = menu.Items[2].Divider!.Value;
        menu.Hover(divider.X + 1, divider.Y);
        Assert.Equal(2, menu.HighlightedIndex);
    }

    [Fact]
    public void LongLabelsAndReasons_AreCutToFit()
    {
        Assert.True(StringTable.TryParse("""{ "language": "en", "strings": { "long": "A very very long label that cannot possibly fit", "why": "A reason that goes on\nand on and on and on and on" } }""", out var table, out var error), error);
        var menu = ContextMenu.Create(new Localizer([table]), [new("a", "long", IconNames.Use, DisabledReasonKey: "why")]);

        Assert.Equal(ContextMenu.MaxLabelChars, menu.Items[0].Label.Length);
        Assert.EndsWith("...", menu.Items[0].Label, StringComparison.Ordinal);
        Assert.True(menu.Items[0].DisabledReason!.Length <= ContextMenu.MaxReasonChars);
        Assert.DoesNotContain('\n', menu.Items[0].DisabledReason!);
    }

    [Fact]
    public void ALongerLabel_MakesTheMenuWider()
    {
        Assert.True(StringTable.TryParse("""{ "language": "en", "strings": { "s": "Use", "l": "Use the thing now" } }""", out var table, out var error), error);
        var localizer = new Localizer([table]);
        var narrow = ContextMenu.Create(localizer, [new("a", "s", IconNames.Use)]);
        var wide = ContextMenu.Create(localizer, [new("a", "l", IconNames.Use)]);
        narrow.Arrange(0, 0, Screen, 1);
        wide.Arrange(0, 0, Screen, 1);

        Assert.True(wide.Bounds.Width > narrow.Bounds.Width);
    }

    [Fact]
    public void Swedish_BaseModEntries_FitOnScreenInTheCornerWithoutBeingCut()
    {
        var localizer = RepositoryLocalizer();
        ContextMenu Menu(string language)
        {
            localizer.Language = language;
            var menu = ContextMenu.Create(localizer, [
                new("use", "menu.use", IconNames.Use, "F"),
                new("equip", "menu.equip", IconNames.Equip, DisabledReasonKey: "menu.reason.nothing_to_equip"),
                new("drop", "menu.drop", IconNames.Drop, "G", StartsGroup: true),
                new("split", "menu.split", IconNames.Split, DisabledReasonKey: "menu.reason.single_item"),
                new("inspect", "menu.inspect", IconNames.Inspect)]);
            menu.Arrange(Screen.Width - 5, Screen.Height - 5, Screen, 2);
            return menu;
        }

        var en = Menu("en");
        var sv = Menu("sv");

        Assert.True(en.Bounds.Right <= Screen.Right && en.Bounds.Bottom <= Screen.Bottom);
        Assert.True(sv.Bounds.Right <= Screen.Right && sv.Bounds.Bottom <= Screen.Bottom);
        Assert.All(sv.Items, i => Assert.DoesNotContain("...", i.Label, StringComparison.Ordinal));
        Assert.All(sv.Items.Where(i => i.DisabledReason is not null), i => Assert.DoesNotContain("...", i.DisabledReason!, StringComparison.Ordinal));
    }

    [Fact]
    public void MenuKeys_HaveEnglishAndSwedishText_ThatTheFontCanDraw()
    {
        var localizer = RepositoryLocalizer();
        var keys = localizer.MissingKeys("none-such").Where(k => k.StartsWith("menu.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(keys);
        foreach (var language in new[] { "en", "sv" })
        {
            localizer.Language = language;
            foreach (var key in keys)
            {
                var text = localizer.Get(key);
                Assert.NotEqual(key, text);
                Assert.All(text, c => Assert.True(DebugFont.HasGlyph(c), $"'{c}' in {language} {key}"));
                var limit = key.StartsWith("menu.reason.", StringComparison.Ordinal) ? ContextMenu.MaxReasonChars : ContextMenu.MaxLabelChars;
                Assert.True(text.Length <= limit, $"{language} {key} would be cut");
            }
        }
    }

    private static Localizer RepositoryLocalizer()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        var packages = DirectoryModSource.Read(Path.Combine(directory!.FullName, "mods"));
        return LocalizationLoader.Load(packages, Zombies.Domain.Mods.ModLoader.Load(packages)).Localizer;
    }
}

public sealed class ContextMenuHostTests
{
    private static readonly UiRect Screen = new(0, 0, 1280, 720);

    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": { "m.use": "Use", "m.equip": "Equip", "m.drop": "Drop", "m.why": "Nothing to equip" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private static ContextMenu Menu() => ContextMenu.Create(English(),
    [
        new("use", "m.use", IconNames.Use),
        new("equip", "m.equip", IconNames.Equip, DisabledReasonKey: "m.why"),
        new("drop", "m.drop", IconNames.Drop, StartsGroup: true),
    ]);

    private static (float X, float Y) Center(ContextMenuItem item) => (item.Bounds.X + (item.Bounds.Width / 2), item.Bounds.Y + (item.Bounds.Height / 2));

    private static (ContextMenuHost Host, List<string> Chosen) Opened()
    {
        var host = new ContextMenuHost();
        var chosen = new List<string>();
        host.Chosen += chosen.Add;
        host.Open(Menu(), 100, 100, Screen, 1);
        return (host, chosen);
    }

    [Fact]
    public void Open_PutsTheMenuAtThePointer()
    {
        var (host, _) = Opened();

        Assert.True(host.IsOpen);
        Assert.Equal(100, host.Current!.Bounds.X);
        Assert.Equal(100, host.Current.Bounds.Y);
    }

    [Fact]
    public void Open_AnotherMenu_ReplacesTheFirst_SoOnlyOneIsOpen()
    {
        var (host, _) = Opened();
        var first = host.Current!;
        var closed = new List<ContextMenu>();
        host.Closed += closed.Add;
        var second = Menu();

        host.Open(second, 300, 300, Screen, 1);

        Assert.Same(second, host.Current);
        Assert.Equal([first], closed);
        Assert.Equal(300, second.Bounds.X);
    }

    [Fact]
    public void Click_OnAnEnabledEntry_ChoosesItAndCloses()
    {
        var (host, chosen) = Opened();
        var (x, y) = Center(host.Current!.Items[0]);

        Assert.True(host.Click(x, y));

        Assert.Equal(["use"], chosen);
        Assert.False(host.IsOpen);
    }

    [Fact]
    public void Click_OnADisabledEntry_DoesNothingAndStaysOpen()
    {
        var (host, chosen) = Opened();
        var (x, y) = Center(host.Current!.Items[1]);

        Assert.True(host.Click(x, y));

        Assert.Empty(chosen);
        Assert.True(host.IsOpen);
    }

    [Fact]
    public void Click_OnADividerOrThePadding_DoesNothingAndStaysOpen()
    {
        var (host, chosen) = Opened();
        var menu = host.Current!;
        var divider = menu.Items[2].Divider!.Value;

        host.Click(divider.X + 1, divider.Y);
        host.Click(menu.Bounds.X + 1, menu.Bounds.Y + 1);

        Assert.Empty(chosen);
        Assert.True(host.IsOpen);
    }

    [Fact]
    public void Click_Outside_ClosesWithoutChoosingAndTakesTheClick()
    {
        var (host, chosen) = Opened();

        Assert.True(host.Click(900, 600));

        Assert.False(host.IsOpen);
        Assert.Empty(chosen);
    }

    [Fact]
    public void Click_WithNoMenuOpen_IsLeftToTheGame()
    {
        Assert.False(new ContextMenuHost().Click(5, 5));
    }

    [Fact]
    public void Escape_Closes_AndOtherKeysAreLeftToTheGame()
    {
        var (host, _) = Opened();

        Assert.False(host.KeyPressed(Key.W));
        Assert.True(host.IsOpen);
        Assert.True(host.KeyPressed(Key.Escape));
        Assert.False(host.IsOpen);
        Assert.False(host.KeyPressed(Key.Escape));
    }

    [Fact]
    public void DownAndEnter_ChooseTheFirstEntry()
    {
        var (host, chosen) = Opened();

        Assert.True(host.KeyPressed(Key.Down));
        Assert.True(host.KeyPressed(Key.Enter));

        Assert.Equal(["use"], chosen);
        Assert.False(host.IsOpen);
    }

    [Fact]
    public void Up_FromNothing_GoesToTheLastEntry_AndEnterChoosesIt()
    {
        var (host, chosen) = Opened();

        host.KeyPressed(Key.Up);
        host.KeyPressed(Key.Enter);

        Assert.Equal(["drop"], chosen);
    }

    [Fact]
    public void Enter_WithNothingHighlighted_ChoosesNothing()
    {
        var (host, chosen) = Opened();

        Assert.True(host.KeyPressed(Key.Enter));

        Assert.Empty(chosen);
        Assert.True(host.IsOpen);
    }

    [Fact]
    public void Enter_OnADisabledEntry_DoesNothingAndStaysOpen()
    {
        var (host, chosen) = Opened();

        host.KeyPressed(Key.Down);
        host.KeyPressed(Key.Down);
        host.KeyPressed(Key.Enter);

        Assert.Empty(chosen);
        Assert.True(host.IsOpen);
    }

    [Fact]
    public void HoveringADisabledEntry_ShowsItsReasonInATooltip_AtThePointer()
    {
        var (host, _) = Opened();
        var (x, y) = Center(host.Current!.Items[1]);

        Assert.Null(host.ReasonTooltip);
        host.PointerMoved(x, y);

        Assert.NotNull(host.ReasonTooltip);
        Assert.Equal(["Nothing to equip"], host.ReasonTooltip.Lines);
        Assert.Equal((x, y), host.TooltipAnchor);
    }

    [Fact]
    public void HoveringAnEnabledEntry_ShowsNoTooltip()
    {
        var (host, _) = Opened();
        var (x, y) = Center(host.Current!.Items[0]);

        host.PointerMoved(x, y);

        Assert.Null(host.ReasonTooltip);
    }

    [Fact]
    public void ReachingADisabledEntryByKeyboard_ShowsItsReasonBesideTheEntry()
    {
        var (host, _) = Opened();

        host.KeyPressed(Key.Down);
        host.KeyPressed(Key.Down);

        Assert.NotNull(host.ReasonTooltip);
        Assert.Equal(Center(host.Current!.Items[1]), host.TooltipAnchor);
    }

    [Fact]
    public void Rearrange_AfterTheWindowShrinks_KeepsTheMenuOnScreen()
    {
        var host = new ContextMenuHost();
        host.Open(Menu(), 1200, 700, Screen, 1);
        var small = new UiRect(0, 0, 400, 300);

        host.Rearrange(small, 1);

        var b = host.Current!.Bounds;
        Assert.True(b.Right <= small.Right && b.Bottom <= small.Bottom && b.X >= 0 && b.Y >= 0);
    }

    [Fact]
    public void Draw_PutsThePanelDividerRowsAndTooltipOnTheScreen_AndNothingWhenClosed()
    {
        var palette = UiPalette.For(UiPaletteKind.Standard);
        var sprites = new SpriteBatch();
        UiRenderer.DrawContextMenu(sprites, new ContextMenuHost(), palette, Screen, 1);
        Assert.Equal(0, sprites.QuadCount);

        var (host, _) = Opened();
        UiRenderer.DrawContextMenu(sprites, host, palette, Screen, 1);
        var plain = sprites.QuadCount;
        Assert.True(plain > 0);

        var (x, y) = Center(host.Current!.Items[1]);
        host.PointerMoved(x, y);
        var withTooltip = new SpriteBatch();
        UiRenderer.DrawContextMenu(withTooltip, host, palette, Screen, 1);
        Assert.True(withTooltip.QuadCount > plain);
    }
}
