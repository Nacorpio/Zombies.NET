using Zombies.Engine.Render;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class UiRendererTests
{
    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": { "t.title": "Hello", "t.tip": "A tooltip" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private static UiLayout Layout(string json)
    {
        Assert.True(UiLayout.TryParse(json, out var layout, out var error), error);
        return layout;
    }

    private static UiContext Context() => new(English(), 1f, 1);

    [Fact]
    public void Draw_APanelAndABar_PutsQuadsOnTheScreen()
    {
        var layout = Layout("""
            { "id": "t", "root": { "type": "panel", "id": "box", "anchor": "topLeft", "width": 40, "height": 20, "children": [
                { "type": "bar", "id": "bar", "anchor": "topLeft", "x": 2, "y": 2, "width": 30, "height": 6 } ] } }
            """);
        layout.Arrange(new UiRect(0, 0, 200, 100), Context());
        var values = new UiValues();
        values.SetFraction("bar", 0.5f);
        var sprites = new SpriteBatch();

        UiRenderer.Draw(sprites, layout, values, Context(), UiPalette.For(UiPaletteKind.Standard));

        // One quad for the panel, one for the bar's track, and one for its fill.
        Assert.Equal(3, sprites.QuadCount);
    }

    [Fact]
    public void Draw_ABarAtZero_HasNoFill()
    {
        var layout = Layout("""{ "id": "t", "root": { "type": "bar", "id": "bar", "anchor": "topLeft", "width": 30, "height": 6 } }""");
        layout.Arrange(new UiRect(0, 0, 200, 100), Context());
        var sprites = new SpriteBatch();

        UiRenderer.Draw(sprites, layout, new UiValues(), Context(), UiPalette.For(UiPaletteKind.Standard));

        Assert.Equal(1, sprites.QuadCount);
    }

    [Fact]
    public void Draw_ABarAtFull_FillsItsWholeWidth()
    {
        var layout = Layout("""{ "id": "t", "root": { "type": "bar", "id": "bar", "anchor": "topLeft", "width": 30, "height": 6 } }""");
        layout.Arrange(new UiRect(0, 0, 200, 100), Context());
        var values = new UiValues();
        values.SetFraction("bar", 1f);
        var sprites = new SpriteBatch();

        UiRenderer.Draw(sprites, layout, values, Context(), UiPalette.For(UiPaletteKind.Standard));

        Assert.Equal(2, sprites.QuadCount);
    }

    [Fact]
    public void Draw_ALabelWithNoValue_UsesItsTextKey()
    {
        var layout = Layout("""{ "id": "t", "root": { "type": "label", "id": "title", "textKey": "t.title", "anchor": "topLeft" } }""");
        layout.Arrange(new UiRect(0, 0, 200, 100), Context());
        var sprites = new SpriteBatch();

        UiRenderer.Draw(sprites, layout, new UiValues(), Context(), UiPalette.For(UiPaletteKind.Standard));

        Assert.True(sprites.QuadCount > 0);
    }

    [Fact]
    public void Draw_ALabelWithAValue_UsesTheValue()
    {
        var layout = Layout("""{ "id": "t", "root": { "type": "label", "id": "title", "textKey": "t.title", "anchor": "topLeft" } }""");
        layout.Arrange(new UiRect(0, 0, 200, 100), Context());
        var values = new UiValues();
        values.SetText("title", "Dynamic");
        var sprites = new SpriteBatch();

        UiRenderer.Draw(sprites, layout, values, Context(), UiPalette.For(UiPaletteKind.Standard));

        Assert.True(sprites.QuadCount > 0);
    }

    [Fact]
    public void Draw_AHiddenWidget_DrawsNothing()
    {
        var layout = Layout("""{ "id": "t", "root": { "type": "panel", "id": "box", "anchor": "topLeft", "width": 40, "height": 20 } }""");
        layout.Arrange(new UiRect(0, 0, 200, 100), Context());
        var values = new UiValues();
        values.SetVisible("box", false);
        var sprites = new SpriteBatch();

        UiRenderer.Draw(sprites, layout, values, Context(), UiPalette.For(UiPaletteKind.Standard));

        Assert.Equal(0, sprites.QuadCount);
    }

    [Fact]
    public void DrawTooltip_OverAWidgetWithAKey_DrawsABox()
    {
        var layout = Layout("""{ "id": "t", "root": { "type": "panel", "id": "box", "anchor": "topLeft", "width": 40, "height": 20, "tooltipKey": "t.tip" } }""");
        layout.Arrange(new UiRect(0, 0, 200, 100), Context());
        var sprites = new SpriteBatch();

        UiRenderer.DrawTooltip(sprites, layout, English(), UiPalette.For(UiPaletteKind.Standard), 5, 5, new UiRect(0, 0, 200, 100), 1);

        Assert.True(sprites.QuadCount > 0);
    }

    [Fact]
    public void DrawTooltip_OverNothing_DrawsNothing()
    {
        var layout = Layout("""{ "id": "t", "root": { "type": "panel", "id": "box", "anchor": "topLeft", "width": 40, "height": 20 } }""");
        layout.Arrange(new UiRect(0, 0, 200, 100), Context());
        var sprites = new SpriteBatch();

        UiRenderer.DrawTooltip(sprites, layout, English(), UiPalette.For(UiPaletteKind.Standard), 150, 90, new UiRect(0, 0, 200, 100), 1);

        Assert.Equal(0, sprites.QuadCount);
    }

    [Fact]
    public void DrawDialog_DrawsTheBoxAndEveryButton()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": { "d.title": "Sure?", "d.message": "This will happen.", "d.yes": "Yes", "d.no": "No" } }
            """, out var table, out var error), error);
        var dialog = Dialog.Create(new Localizer([table]), "d.title", "d.message", null, [new DialogButton("yes", "d.yes"), new DialogButton("no", "d.no", IsCancel: true)]);
        dialog.Arrange(new UiRect(0, 0, 400, 300), 1);
        var sprites = new SpriteBatch();

        UiRenderer.DrawDialog(sprites, dialog, UiPalette.For(UiPaletteKind.Standard), 1);

        // The box, one quad per button, and one quad per drawn character (spaces are skipped).
        var text = dialog.Title.Count(c => c != ' ') + dialog.MessageLines.Sum(l => l.Count(c => c != ' ')) + dialog.Buttons.Sum(b => b.Label.Count(c => c != ' '));
        Assert.Equal(3 + text, sprites.QuadCount);
    }
}
