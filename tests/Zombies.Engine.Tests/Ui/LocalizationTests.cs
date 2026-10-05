using System.Globalization;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class LocalizationTests
{
    private const string English = """{ "language": "en", "strings": { "hud.health": "Health", "hud.ammo": "Ammo {0}/{1}", "only.english": "Only here" } }""";
    private const string Swedish = """{ "language": "sv", "strings": { "hud.health": "Hälsa", "hud.ammo": "Ammo {0}/{1}" } }""";

    private static Localizer Localizer(string language = "en")
    {
        Assert.True(StringTable.TryParse(English, out var en, out var enError), enError);
        Assert.True(StringTable.TryParse(Swedish, out var sv, out var svError), svError);
        return new Localizer([en, sv], fallbackLanguage: "en") { Language = language };
    }

    [Fact]
    public void Get_InTheChosenLanguage_ReturnsThatLanguagesText()
    {
        Assert.Equal("Health", Localizer("en").Get("hud.health"));
        Assert.Equal("Hälsa", Localizer("sv").Get("hud.health"));
    }

    [Fact]
    public void Get_AKeyMissingInTheChosenLanguage_FallsBackToEnglish()
    {
        Assert.Equal("Only here", Localizer("sv").Get("only.english"));
    }

    [Fact]
    public void Get_AKeyNoLanguageHas_ShowsTheKeySoTheGapIsVisible()
    {
        Assert.Equal("no.such.key", Localizer().Get("no.such.key"));
    }

    [Fact]
    public void Format_FillsPlaceholdersWithoutDependingOnTheMachinesCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            Assert.Equal("Ammo 7/30", Localizer().Format("hud.ammo", 7, 30));
            Assert.Equal("Ammo 1.5/30", Localizer().Format("hud.ammo", 1.5, 30));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Format_WithTooFewArguments_DoesNotThrow()
    {
        Assert.Equal("Ammo {0}/{1}", Localizer().Format("hud.ammo"));
    }

    [Fact]
    public void Language_AnUnknownLanguage_IsRefusedAndTheCurrentOneStays()
    {
        var localizer = Localizer("sv");

        Assert.False(localizer.TrySetLanguage("xx"));

        Assert.Equal("sv", localizer.Language);
    }

    [Fact]
    public void Languages_ListsEveryLoadedLanguage()
    {
        Assert.Equal(["en", "sv"], Localizer().Languages);
    }

    [Fact]
    public void MissingKeys_ListsWhatALanguageStillNeedsToTranslate()
    {
        Assert.Equal(["only.english"], Localizer().MissingKeys("sv"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "strings": { "a": "b" } }""")]
    [InlineData("""{ "language": "", "strings": {} }""")]
    [InlineData("""{ "language": "en", "strings": { "a": 5 } }""")]
    [InlineData("""{ "language": "en" }""")]
    public void TryParse_ABadTable_ReportsWhy(string json)
    {
        Assert.False(StringTable.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_AKeyListedTwice_IsReportedInsteadOfSilentlyTakingTheLast()
    {
        const string Duplicate = """{ "language": "en", "strings": { "a": "one", "a": "two" } }""";

        Assert.False(StringTable.TryParse(Duplicate, out _, out var error));
        Assert.Contains("'a'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_ALaterTableReplacesAndAddsKeys_AsAModDoes()
    {
        Assert.True(StringTable.TryParse(English, out var baseTable, out _));
        Assert.True(StringTable.TryParse("""{ "language": "en", "strings": { "hud.health": "HP", "mod.new": "New" } }""", out var mod, out _));

        var localizer = new Localizer([baseTable, mod], "en");

        Assert.Equal("HP", localizer.Get("hud.health"));
        Assert.Equal("New", localizer.Get("mod.new"));
        Assert.Equal("Only here", localizer.Get("only.english"));
    }
}
