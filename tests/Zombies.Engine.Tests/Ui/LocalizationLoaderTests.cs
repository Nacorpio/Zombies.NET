using System.Text;
using System.Text.RegularExpressions;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed partial class LocalizationLoaderTests
{
    private static ModPackage Mod(string id, params (string Path, string Json)[] languages)
    {
        var manifest = $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","side":"both","dependencies":[]}""";
        return new ModPackage(id, manifest, []) { Assets = [.. languages.Select(l => new ModAsset(l.Path, Encoding.UTF8.GetBytes(l.Json)))] };
    }

    private static string Table(string language, string strings) => $$"""{ "language": "{{language}}", "strings": { {{strings}} } }""";

    private static LocalizationLoadResult Load(params ModPackage[] packages) => LocalizationLoader.Load(packages, ModLoader.Load(packages));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static LocalizationLoadResult LoadRepository()
    {
        var packages = DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods"));
        return LocalizationLoader.Load(packages, ModLoader.Load(packages));
    }

    [Fact]
    public void BaseMod_ShipsEnglishAndSwedishAndTheyLoadWithoutProblems()
    {
        var result = LoadRepository();

        Assert.Empty(result.Problems);
        Assert.Equal(["en", "sv"], result.Localizer.Languages);
    }

    [Fact]
    public void Swedish_TranslatesEveryKeyEnglishHas()
    {
        Assert.Empty(LoadRepository().Localizer.MissingKeys("sv"));
    }

    [Fact]
    public void Swedish_UsesTheSamePlaceholdersAsEnglishForEveryKey()
    {
        var packages = DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods"));
        var tables = packages.Single(p => p.Source == "base").Assets
            .Where(a => a.Path.StartsWith(LocalizationLoader.FolderPrefix, StringComparison.Ordinal))
            .Select(a => { Assert.True(StringTable.TryParse(Encoding.UTF8.GetString(a.Bytes).TrimStart('\uFEFF'), out var t, out var e), e); return t; })
            .ToDictionary(t => t.Language);

        foreach (var (key, english) in tables["en"].Strings)
        {
            Assert.Equal(Placeholders(english), Placeholders(tables["sv"].Strings[key]));
        }
    }

    [Fact]
    public void Swedish_ReallyIsSwedish_NotACopyOfEnglish()
    {
        var localizer = LoadRepository().Localizer;
        localizer.Language = "sv";

        Assert.NotEqual("Health", localizer.Get("hud.health"));
        Assert.Contains(localizer.Languages, l => l == "sv");
    }

    [Fact]
    public void Swedish_OnlyUsesCharactersTheUiFontCanDraw()
    {
        var localizer = LoadRepository().Localizer;
        foreach (var language in localizer.Languages)
        {
            localizer.Language = language;
            foreach (var key in localizer.MissingKeys("none-such"))
            {
                foreach (var c in localizer.Get(key).Where(c => c != '{' && c != '}'))
                {
                    Assert.True(Zombies.Engine.Render.DebugFont.HasGlyph(c), $"'{c}' in {language} {key} has no glyph");
                }
            }
        }
    }

    [Fact]
    public void LaterMods_AddKeysAndReplaceTheTextOfEarlierOnes()
    {
        var first = Mod("a", ("lang/en.json", Table("en", """ "x": "from a", "y": "only a" """)));
        var second = Mod("b", ("lang/en.json", Table("en", """ "x": "from b" """)));

        var localizer = Load(first, second).Localizer;

        Assert.Equal("from b", localizer.Get("x"));
        Assert.Equal("only a", localizer.Get("y"));
    }

    [Fact]
    public void ABadTable_IsReportedWithItsModAndFile_AndTheOthersStillLoad()
    {
        var mod = Mod("a", ("lang/en.json", Table("en", """ "x": "fine" """)), ("lang/sv.json", "{ broken"));

        var result = Load(mod);

        var problem = Assert.Single(result.Problems);
        Assert.Equal("a", problem.ModId);
        Assert.Equal("lang/sv.json", problem.File);
        Assert.Equal("fine", result.Localizer.Get("x"));
    }

    [Fact]
    public void ATableWhoseNameDisagreesWithItsLanguage_IsReported()
    {
        var result = Load(Mod("a", ("lang/sv.json", Table("en", """ "x": "y" """))));

        Assert.Contains("sv", Assert.Single(result.Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATableSavedWithAByteOrderMark_StillLoads()
    {
        var withMark = Mod("a", ("lang/en.json", "\uFEFF" + Table("en", """ "x": "fine" """)));

        var result = Load(withMark);

        Assert.Empty(result.Problems);
        Assert.Equal("fine", result.Localizer.Get("x"));
    }

    [Fact]
    public void ModsWithoutLanguageFiles_LoadAnEmptyLocalizerThatShowsKeys()
    {
        var result = Load(Mod("a"));

        Assert.Empty(result.Problems);
        Assert.Equal("some.key", result.Localizer.Get("some.key"));
    }

    private static string Placeholders(string text) =>
        string.Join(",", PlaceholderPattern().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal));

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
