using System.Text;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class LayoutLoaderTests
{
    private static ModPackage Mod(string id, params (string Path, string Json)[] layouts)
    {
        var manifest = $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","side":"both","dependencies":[]}""";
        return new ModPackage(id, manifest, []) { Assets = [.. layouts.Select(l => new ModAsset(l.Path, Encoding.UTF8.GetBytes(l.Json)))] };
    }

    private static LayoutLoadResult Load(params ModPackage[] packages) => LayoutLoader.Load(packages, ModLoader.Load(packages));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static IReadOnlyList<ModPackage> Repository() => DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods"));

    [Fact]
    public void BaseMod_ShipsTheScreensAndTheyAllLoadWithoutProblems()
    {
        var result = Load([.. Repository()]);

        Assert.Empty(result.Problems);
        Assert.Equal(["body", "hud", "inventory", "options"], result.Layouts.Keys.Order());
    }

    [Fact]
    public void EveryKeyTheScreensNeed_IsTranslatedInEveryLanguage()
    {
        var packages = Repository();
        var layouts = Load([.. packages]);
        var localizer = LocalizationLoader.Load(packages, ModLoader.Load(packages)).Localizer;

        foreach (var language in localizer.Languages)
        {
            foreach (var layout in layouts.Layouts.Values)
            {
                foreach (var key in layout.Keys())
                {
                    Assert.NotEqual(key, localizer.Get(key));
                }
            }
        }
    }

    [Fact]
    public void EveryScreen_ArrangesInsideTheScreenAtEveryScale()
    {
        var packages = Repository();
        var layouts = Load([.. packages]);
        var localizer = LocalizationLoader.Load(packages, ModLoader.Load(packages)).Localizer;
        var screen = new UiRect(0, 0, 1280, 720);

        foreach (var layout in layouts.Layouts.Values)
        {
            foreach (var scale in new[] { 1f, 2f, 3f })
            {
                layout.Arrange(screen, new UiContext(localizer, scale, (int)scale));
                foreach (var widget in Walk(layout.Root))
                {
                    Assert.True(widget.Bounds.X >= screen.X && widget.Bounds.Right <= screen.Right, $"{layout.Id} {widget.Id} {widget.Bounds}");
                    Assert.True(widget.Bounds.Y >= screen.Y && widget.Bounds.Bottom <= screen.Bottom, $"{layout.Id} {widget.Id} {widget.Bounds}");
                }
            }
        }
    }

    [Fact]
    public void LaterMods_ReplaceAScreenOfTheSameId()
    {
        var first = Mod("a", ("ui/hud.json", """{ "id": "hud", "root": { "type": "panel", "id": "one" } }"""));
        var second = Mod("b", ("ui/hud.json", """{ "id": "hud", "root": { "type": "panel", "id": "two" } }"""));

        var layouts = Load(first, second).Layouts;

        Assert.Equal("two", Assert.Single(layouts).Value.Root.Id);
    }

    [Fact]
    public void ABadLayout_IsReportedWithItsModAndFile_AndTheOthersStillLoad()
    {
        var mod = Mod("a", ("ui/hud.json", """{ "id": "hud", "root": { "type": "panel" } }"""), ("ui/body.json", "{ broken"));

        var result = Load(mod);

        var problem = Assert.Single(result.Problems);
        Assert.Equal("a", problem.ModId);
        Assert.Equal("ui/body.json", problem.File);
        Assert.NotNull(result.Get("hud"));
    }

    [Fact]
    public void ALayoutWhoseNameDisagreesWithItsId_IsReported()
    {
        var result = Load(Mod("a", ("ui/body.json", """{ "id": "hud", "root": { "type": "panel" } }""")));

        Assert.Contains("body", Assert.Single(result.Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModsWithoutLayouts_LoadAnEmptySet()
    {
        var result = Load(Mod("a"));

        Assert.Empty(result.Problems);
        Assert.Empty(result.Layouts);
        Assert.Null(result.Get("hud"));
    }

    private static IEnumerable<Widget> Walk(Widget widget)
    {
        yield return widget;
        foreach (var child in widget.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}
