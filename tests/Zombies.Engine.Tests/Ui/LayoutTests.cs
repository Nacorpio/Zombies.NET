using Zombies.Engine.Render;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class LayoutTests
{
    private const string Simple = """
        {
          "id": "test",
          "root": {
            "type": "panel", "id": "box", "anchor": "bottomLeft", "x": 10, "y": 6, "width": 100, "height": 40,
            "children": [
              { "type": "label", "id": "title", "textKey": "t.title", "anchor": "topLeft", "x": 4, "y": 4, "role": "good" },
              { "type": "bar", "id": "bar", "anchor": "bottomRight", "x": 4, "y": 4, "width": 50, "height": 8, "tooltipKey": "t.tip" }
            ]
          }
        }
        """;

    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""{ "language": "en", "strings": { "t.title": "Hello", "t.tip": "A tooltip" } }""", out var table, out var error), error);
        return new Localizer([table]);
    }

    private static UiLayout Parse(string json = Simple)
    {
        Assert.True(UiLayout.TryParse(json, out var layout, out var error), error);
        return layout;
    }

    private static UiContext Context(float scale = 1f, int textScale = 1) => new(English(), scale, textScale);

    [Fact]
    public void TryParse_BuildsTheTreeWithIdsKindsAndRoles()
    {
        var layout = Parse();

        Assert.Equal("test", layout.Id);
        Assert.Equal(WidgetKind.Panel, layout.Root.Kind);
        Assert.Equal(["title", "bar"], layout.Root.Children.Select(c => c.Id));
        Assert.Equal(PaletteRole.Good, layout.Find("title")!.Role);
        Assert.Equal("t.tip", layout.Find("bar")!.TooltipKey);
        Assert.Null(layout.Find("nope"));
    }

    [Fact]
    public void Arrange_AnchorsAWidgetToACornerWithAnInwardOffset()
    {
        var layout = Parse();

        layout.Arrange(new UiRect(0, 0, 800, 600), Context());

        Assert.Equal(new UiRect(10, 554, 100, 40), layout.Find("box")!.Bounds);
        Assert.Equal(new UiRect(14, 558, 0, 0).X, layout.Find("title")!.Bounds.X);
        var bar = layout.Find("bar")!.Bounds;
        Assert.Equal(110 - 4 - 50, bar.X);
        Assert.Equal(594 - 4 - 8, bar.Y);
    }

    [Fact]
    public void Arrange_ScalesSizesAndOffsetsWithTheUiScale()
    {
        var layout = Parse();

        layout.Arrange(new UiRect(0, 0, 800, 600), Context(scale: 2f));

        Assert.Equal(new UiRect(20, 600 - 12 - 80, 200, 80), layout.Find("box")!.Bounds);
    }

    [Fact]
    public void Arrange_ALabelWithNoSizeMeasuresItsLocalizedText()
    {
        var layout = Parse();

        layout.Arrange(new UiRect(0, 0, 800, 600), Context(textScale: 2));

        var title = layout.Find("title")!.Bounds;
        Assert.Equal(DebugFont.MeasureWidth("Hello", 2), title.Width);
        Assert.Equal(DebugFont.LineHeight * 2, title.Height);
    }

    [Fact]
    public void Arrange_CenterAndFill_PlaceAgainstTheParent()
    {
        const string Json = """
            { "id": "t", "root": { "type": "panel", "anchor": "fill", "x": 5, "y": 5, "children": [
                { "type": "panel", "id": "mid", "anchor": "center", "width": 20, "height": 10 } ] } }
            """;
        var layout = Parse(Json);

        layout.Arrange(new UiRect(0, 0, 200, 100), Context());

        Assert.Equal(new UiRect(5, 5, 190, 90), layout.Root.Bounds);
        Assert.Equal(new UiRect(90, 45, 20, 10), layout.Find("mid")!.Bounds);
    }

    [Fact]
    public void Arrange_ARowStacksChildrenLeftToRightWithTheGap_AndAColumnTopToBottom()
    {
        const string Json = """
            { "id": "t", "root": { "type": "column", "anchor": "topLeft", "gap": 3, "children": [
                { "type": "row", "id": "r", "gap": 2, "children": [
                    { "type": "panel", "id": "a", "width": 10, "height": 8 },
                    { "type": "panel", "id": "b", "width": 6, "height": 12 } ] },
                { "type": "panel", "id": "c", "width": 4, "height": 5 } ] } }
            """;
        var layout = Parse(Json);

        layout.Arrange(new UiRect(0, 0, 200, 100), Context());

        Assert.Equal(new UiRect(0, 0, 10, 8), layout.Find("a")!.Bounds);
        Assert.Equal(new UiRect(12, 0, 6, 12), layout.Find("b")!.Bounds);
        Assert.Equal(new UiRect(0, 15, 4, 5), layout.Find("c")!.Bounds);
        Assert.Equal(new UiRect(0, 0, 18, 12), layout.Find("r")!.Bounds);
    }

    [Fact]
    public void HitTest_ReturnsTheDeepestWidgetUnderThePoint_AndNullOutsideEverything()
    {
        var layout = Parse();
        layout.Arrange(new UiRect(0, 0, 800, 600), Context());

        Assert.Equal("bar", layout.HitTest(layout.Find("bar")!.Bounds.X + 1, layout.Find("bar")!.Bounds.Y + 1)!.Id);
        Assert.Equal("box", layout.HitTest(12, 590)!.Id);
        Assert.Null(layout.HitTest(500, 20));
    }

    [Fact]
    public void HitTest_OfOverlappingSiblings_PrefersTheOneDrawnLast()
    {
        const string Json = """
            { "id": "t", "root": { "type": "panel", "anchor": "topLeft", "width": 50, "height": 50, "children": [
                { "type": "panel", "id": "under", "anchor": "topLeft", "width": 30, "height": 30 },
                { "type": "panel", "id": "over", "anchor": "topLeft", "width": 30, "height": 30 } ] } }
            """;
        var layout = Parse(Json);
        layout.Arrange(new UiRect(0, 0, 100, 100), Context());

        Assert.Equal("over", layout.HitTest(5, 5)!.Id);
    }

    [Fact]
    public void TooltipAt_UsesTheWidgetsKey_OrTheNearestParentsWhenItHasNone()
    {
        var layout = Parse();
        layout.Arrange(new UiRect(0, 0, 800, 600), Context());
        var bar = layout.Find("bar")!.Bounds;

        var tooltip = layout.TooltipAt(bar.X + 1, bar.Y + 1, English());

        Assert.Equal("A tooltip", Assert.Single(tooltip!.Lines));
        Assert.Null(layout.TooltipAt(12, 590, English()));
    }

    [Fact]
    public void Values_OverrideTheTextFractionAndVisibilityOfAWidgetById()
    {
        var values = new UiValues();

        values.SetText("title", "Dynamic");
        values.SetFraction("bar", 0.4f);
        values.SetRole("bar", PaletteRole.Danger);
        values.SetVisible("title", false);

        Assert.Equal("Dynamic", values.TextOf("title"));
        Assert.Equal(0.4f, values.FractionOf("bar"));
        Assert.Equal(PaletteRole.Danger, values.RoleOf("bar"));
        Assert.False(values.IsVisible("title"));
        Assert.True(values.IsVisible("never-set"));
        Assert.Null(values.TextOf("bar"));
        Assert.Equal(0f, values.FractionOf("never-set"));
    }

    [Fact]
    public void Values_FractionsAreClampedToZeroToOne_AndNaNIsZero()
    {
        var values = new UiValues();

        values.SetFraction("a", 5f);
        values.SetFraction("b", -1f);
        values.SetFraction("c", float.NaN);

        Assert.Equal(1f, values.FractionOf("a"));
        Assert.Equal(0f, values.FractionOf("b"));
        Assert.Equal(0f, values.FractionOf("c"));
    }

    [Fact]
    public void Keys_ListsEveryTextAndTooltipKeyALayoutNeeds()
    {
        Assert.Equal(["t.tip", "t.title"], Parse().Keys().Order());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("""{ "id": "t" }""")]
    [InlineData("""{ "root": { "type": "panel" } }""")]
    [InlineData("""{ "id": "t", "root": { "type": "hologram" } }""")]
    [InlineData("""{ "id": "t", "root": { "type": "panel", "anchor": "sideways" } }""")]
    [InlineData("""{ "id": "t", "root": { "type": "label", "role": "magenta" } }""")]
    [InlineData("""{ "id": "t", "root": { "type": "panel", "width": -4 } }""")]
    [InlineData("""{ "id": "t", "root": { "type": "panel", "children": [ { "type": "panel", "id": "x" }, { "type": "panel", "id": "x" } ] } }""")]
    public void TryParse_ABadLayout_ReportsWhy(string json)
    {
        Assert.False(UiLayout.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_AnErrorNamesWhereInTheTreeItIs()
    {
        const string Json = """{ "id": "t", "root": { "type": "panel", "children": [ { "type": "panel", "children": [ { "type": "hologram" } ] } ] } }""";

        Assert.False(UiLayout.TryParse(Json, out _, out var error));

        Assert.Contains("root.children[0].children[0]", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ATreeNestedAbsurdlyDeep_IsRefusedInsteadOfOverflowingTheStack()
    {
        var json = """{ "id": "t", "root": """ + string.Concat(Enumerable.Repeat("""{ "type": "panel", "children": [""", 100)) + """{ "type": "panel" }""" + string.Concat(Enumerable.Repeat("]}", 100)) + "}";

        Assert.False(UiLayout.TryParse(json, out _, out var error));
        Assert.Contains("deep", error, StringComparison.Ordinal);
    }
}

public sealed class TooltipTests
{
    [Fact]
    public void Create_WrapsTheBodyToAShortBlock()
    {
        var tooltip = Tooltip.Create("Bandage", "Stops bleeding on one body part. Takes a few seconds to apply, cannot be reused afterwards, and works best on clean skin that has been washed with water first.");

        Assert.Equal("Bandage", tooltip.Title);
        Assert.All(tooltip.Lines, l => Assert.True(l.Length <= Tooltip.MaxCharsPerLine));
        Assert.True(tooltip.Lines.Count <= Tooltip.MaxLines);
        Assert.EndsWith("...", tooltip.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Create_AShortBodyIsKeptWhole()
    {
        var tooltip = Tooltip.Create("Beans", "Canned. Fills you up.");

        Assert.Equal(["Canned. Fills you up."], tooltip.Lines);
    }

    [Fact]
    public void Create_WithNoTitle_HasOnlyLines()
    {
        Assert.Null(Tooltip.Create(null, "Hi").Title);
    }

    [Fact]
    public void Place_PutsTheTooltipBesideThePointer()
    {
        var tooltip = Tooltip.Create("Hi", "There");

        var rect = tooltip.Place(100, 100, new UiRect(0, 0, 800, 600), textScale: 1);

        Assert.True(rect.X >= 100 && rect.Y >= 100);
    }

    [Fact]
    public void Place_NearTheBottomRightEdge_FlipsToStayOnScreen()
    {
        var tooltip = Tooltip.Create("Title", "A line of text here");
        var screen = new UiRect(0, 0, 800, 600);

        var rect = tooltip.Place(795, 595, screen, textScale: 2);

        Assert.True(rect.X >= 0 && rect.Y >= 0);
        Assert.True(rect.Right <= screen.Right && rect.Bottom <= screen.Bottom);
    }

    [Fact]
    public void Size_GrowsWithTheTextScale()
    {
        var tooltip = Tooltip.Create("Title", "Body");

        var one = tooltip.Place(0, 0, new UiRect(0, 0, 800, 600), 1);
        var two = tooltip.Place(0, 0, new UiRect(0, 0, 800, 600), 2);

        Assert.True(two.Width > one.Width && two.Height > one.Height);
    }
}

public sealed class DialogTests
{
    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "d.title": "Drop everything?",
              "d.message": "You will drop every item you carry on the ground here, and anyone nearby can take it. This cannot be undone once you walk away.",
              "d.caption": "Press Enter to confirm",
              "d.yes": "Drop", "d.no": "Cancel" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private static Dialog Build() => Dialog.Create(English(), "d.title", "d.message", "d.caption", [new DialogButton("yes", "d.yes"), new DialogButton("no", "d.no", IsCancel: true)]);

    [Fact]
    public void Create_ShowsTitleCaptionAndConciseMessageLines()
    {
        var dialog = Build();

        Assert.Equal("Drop everything?", dialog.Title);
        Assert.Equal("Press Enter to confirm", dialog.Caption);
        Assert.All(dialog.MessageLines, l => Assert.True(l.Length <= Dialog.MaxCharsPerLine));
        Assert.True(dialog.MessageLines.Count <= Dialog.MaxMessageLines);
        Assert.EndsWith("...", dialog.MessageLines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Create_TheCaptionIsOptional()
    {
        var dialog = Dialog.Create(English(), "d.title", "d.message", null, [new DialogButton("yes", "d.yes")]);

        Assert.Null(dialog.Caption);
    }

    [Fact]
    public void Buttons_AreLabelledInTheChosenLanguage()
    {
        Assert.Equal(["Drop", "Cancel"], Build().Buttons.Select(b => b.Label));
    }

    [Fact]
    public void Layout_CentersTheDialogAndPlacesButtonsInsideIt()
    {
        var dialog = Build();
        var screen = new UiRect(0, 0, 800, 600);

        dialog.Arrange(screen, textScale: 2);

        Assert.Equal(screen.X + (screen.Width / 2), dialog.Bounds.X + (dialog.Bounds.Width / 2), 1);
        Assert.Equal(screen.Y + (screen.Height / 2), dialog.Bounds.Y + (dialog.Bounds.Height / 2), 1);
        foreach (var button in dialog.Buttons)
        {
            Assert.True(button.Bounds.X >= dialog.Bounds.X && button.Bounds.Right <= dialog.Bounds.Right);
            Assert.True(button.Bounds.Y >= dialog.Bounds.Y && button.Bounds.Bottom <= dialog.Bounds.Bottom);
        }
    }

    [Fact]
    public void Clicking_AButton_ClosesTheDialogWithThatButtonsId()
    {
        var dialog = Build();
        dialog.Arrange(new UiRect(0, 0, 800, 600), 2);
        var yes = dialog.Buttons[0].Bounds;

        var handled = dialog.Click(yes.X + 1, yes.Y + 1);

        Assert.True(handled);
        Assert.Equal("yes", dialog.Result);
    }

    [Fact]
    public void Clicking_OutsideEveryButton_DoesNotCloseTheDialog_ButStillCountsAsHandled()
    {
        var dialog = Build();
        dialog.Arrange(new UiRect(0, 0, 800, 600), 2);

        var handled = dialog.Click(1, 1);

        Assert.True(handled);
        Assert.Null(dialog.Result);
    }

    [Fact]
    public void Keyboard_MovesFocusAndActivates()
    {
        var dialog = Build();

        dialog.MoveFocus(+1);
        dialog.Activate();

        Assert.Equal("no", dialog.Result);
    }

    [Fact]
    public void Focus_WrapsAround()
    {
        var dialog = Build();

        dialog.MoveFocus(-1);

        Assert.Equal(1, dialog.FocusedIndex);
        dialog.MoveFocus(+1);
        Assert.Equal(0, dialog.FocusedIndex);
    }

    [Fact]
    public void Cancel_ChoosesTheCancelButton_OrNothingWhenThereIsNone()
    {
        var withCancel = Build();
        withCancel.Cancel();
        Assert.Equal("no", withCancel.Result);

        var without = Dialog.Create(English(), "d.title", "d.message", null, [new DialogButton("ok", "d.yes")]);
        without.Cancel();
        Assert.Null(without.Result);
    }

    [Fact]
    public void ADialogThatIsClosed_IgnoresFurtherInput()
    {
        var dialog = Build();
        dialog.Activate();

        dialog.MoveFocus(+1);
        dialog.Activate();

        Assert.Equal("yes", dialog.Result);
    }

    [Fact]
    public void Create_WithNoButtons_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => Dialog.Create(English(), "d.title", "d.message", null, []));
    }
}

public sealed class PaletteTests
{
    private static (double X, double Y, double Z) Lab(Rgba color)
    {
        // Simulate deuteranopia (Machado 2009, severity 1.0) in linear RGB, then go to CIE Lab so distances track what is seen.
        static double Lin(byte v) => v / 255.0 <= 0.04045 ? v / 255.0 / 12.92 : Math.Pow(((v / 255.0) + 0.055) / 1.055, 2.4);
        double r = Lin(color.R), g = Lin(color.G), b = Lin(color.B);
        var sr = (0.367322 * r) + (0.860646 * g) + (-0.227968 * b);
        var sg = (0.280085 * r) + (0.672501 * g) + (0.047413 * b);
        var sb = (-0.011820 * r) + (0.042940 * g) + (0.968881 * b);
        var x = ((0.4124 * sr) + (0.3576 * sg) + (0.1805 * sb)) / 0.95047;
        var y = (0.2126 * sr) + (0.7152 * sg) + (0.0722 * sb);
        var z = ((0.0193 * sr) + (0.1192 * sg) + (0.9505 * sb)) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);
        return ((116 * F(y)) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    private static double Distance(Rgba a, Rgba b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt(Math.Pow(l1 - l2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));
    }

    [Fact]
    public void EveryRole_HasAColorInBothPalettes()
    {
        foreach (var kind in Enum.GetValues<UiPaletteKind>())
        {
            var palette = UiPalette.For(kind);
            foreach (var role in Enum.GetValues<PaletteRole>())
            {
                // 'None' is the role that draws nothing, so it is the one role with no color.
                Assert.True(role == PaletteRole.None || palette.Color(role).A > 0, $"{kind} {role}");
            }
        }
    }

    [Fact]
    public void Colorblind_KeepsGoodWarningAndDangerApartForRedGreenColorBlindness()
    {
        var palette = UiPalette.For(UiPaletteKind.Colorblind);

        Assert.True(Distance(palette.Color(PaletteRole.Good), palette.Color(PaletteRole.Danger)) > 30);
        Assert.True(Distance(palette.Color(PaletteRole.Good), palette.Color(PaletteRole.Warning)) > 30);
        Assert.True(Distance(palette.Color(PaletteRole.Warning), palette.Color(PaletteRole.Danger)) > 30);
    }

    [Fact]
    public void Standard_ReallyDoesNeedTheColorblindPalette()
    {
        var palette = UiPalette.For(UiPaletteKind.Standard);

        var goodVersusDanger = Distance(palette.Color(PaletteRole.Good), palette.Color(PaletteRole.Danger));
        var colorblind = UiPalette.For(UiPaletteKind.Colorblind);

        Assert.True(goodVersusDanger < Distance(colorblind.Color(PaletteRole.Good), colorblind.Color(PaletteRole.Danger)));
    }

    [Fact]
    public void Bars_ReadAgainstThePanelInBothPalettes()
    {
        foreach (var kind in Enum.GetValues<UiPaletteKind>())
        {
            var palette = UiPalette.For(kind);
            Assert.True(Distance(palette.Color(PaletteRole.Text), palette.Color(PaletteRole.Panel).WithAlpha(255)) > 40, kind.ToString());
        }
    }
}
