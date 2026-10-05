using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests;

/// <summary>
/// Installing a mod: a Code mod is trusted code, so the player sees the trusted-code warning and must agree before anything is
/// copied. The warning's text comes from the base mod's string tables in English and Swedish. How the dialog looks on screen is
/// not checked here; its text, buttons, and the decision are.
/// </summary>
public sealed class CodeModInstallTests : IDisposable
{
    private readonly string _modsRoot = Path.Combine(Path.GetTempPath(), "zombies-install-" + Guid.NewGuid().ToString("N"));

    private static string SampleCode => Path.Combine(RepositoryMods.Root, "sample_code");

    private static string SampleData => Path.Combine(RepositoryMods.Root, "sample_data");

    public void Dispose()
    {
        if (Directory.Exists(_modsRoot))
        {
            Directory.Delete(_modsRoot, recursive: true);
        }
    }

    private static Localizer RepositoryLocalizer()
    {
        var loaded = RepositoryMods.For(ProcessRole.Solo);
        var localization = LocalizationLoader.Load(loaded.Packages, loaded.Mods);
        Assert.Empty(localization.Problems);
        return localization.Localizer;
    }

    private static ModInstallReview Review(string directory)
    {
        var review = ModInstaller.Review(directory, out var error);
        Assert.True(review is not null, error);
        return review;
    }

    [Fact]
    public void InstallingACodeMod_WithoutTrust_CopiesNothing_AndAsksForTrust()
    {
        var result = ModInstaller.Install(SampleCode, _modsRoot, trustCode: false);

        Assert.Equal(ModInstallStatus.NeedsTrust, result.Status);
        Assert.True(result.Review!.IsCodeMod);
        Assert.False(Directory.Exists(Path.Combine(_modsRoot, "sample_code")));
    }

    [Fact]
    public void InstallingACodeMod_OnceTrusted_CopiesWhatTheGameLoads_UnchangedForJoin()
    {
        var result = ModInstaller.Install(SampleCode, _modsRoot, trustCode: true);

        Assert.Equal(ModInstallStatus.Installed, result.Status);
        var installed = DirectoryModSource.ReadOne(result.InstalledTo!);
        Assert.Equal(DirectoryModSource.ReadOne(SampleCode).ComputeContentHash(), installed.ComputeContentHash());
        Assert.Equal(["assemblies/SampleCodeMod.dll"], installed.Assemblies.Select(a => a.Path));
        Assert.False(Directory.Exists(Path.Combine(result.InstalledTo!, "source")));
        Assert.Equal(ModInstallStatus.AlreadyInstalled, ModInstaller.Install(SampleCode, _modsRoot, trustCode: true).Status);
    }

    [Fact]
    public void InstallingADataMod_NeedsNoTrust_AndShowsNoWarning()
    {
        var review = Review(SampleData);

        var result = ModInstaller.Install(SampleData, _modsRoot, trustCode: false);

        Assert.False(review.IsCodeMod);
        Assert.False(CodeModWarning.IsNeeded(review));
        Assert.Equal(ModInstallStatus.Installed, result.Status);
        Assert.Throws<ArgumentException>(() => CodeModWarning.Create(RepositoryLocalizer(), review));
    }

    [Fact]
    public void AModThatCarriesADll_IsACodeMod_EvenWhenItsManifestListsNone()
    {
        var mod = Path.Combine(_modsRoot, "incoming", "sneaky");
        Directory.CreateDirectory(Path.Combine(mod, "extras"));
        File.WriteAllText(Path.Combine(mod, "mod.json"), """{ "id": "sneaky", "name": "Sneaky", "version": "1.0.0" }""");
        File.WriteAllBytes(Path.Combine(mod, "extras", "Payload.dll"), [0x4D, 0x5A]);

        var review = Review(mod);

        Assert.False(review.Manifest.IsCodeMod);
        Assert.True(review.IsCodeMod);
        Assert.Equal(["extras/Payload.dll"], review.CodeFiles);
        Assert.Equal(ModInstallStatus.NeedsTrust, ModInstaller.Install(mod, Path.Combine(_modsRoot, "installed"), trustCode: false).Status);
    }

    [Fact]
    public void TheWarning_SaysInEnglishThatTheModsCodeIsTrusted_AndOffersCancelFirst()
    {
        var dialog = CodeModWarning.Create(RepositoryLocalizer(), Review(SampleCode));

        Assert.Equal("Trust this mod's code?", dialog.Title);
        Assert.Equal(
            "Sample Code Mod runs its own code with full access to your computer. Install it only if you trust its author.",
            string.Join(' ', dialog.MessageLines));
        Assert.Equal("Code mods are not sandboxed", dialog.Caption);
        Assert.Equal(["Cancel", "Install"], dialog.Buttons.Select(b => b.Label));

        // Pressing Enter at once, or backing out, never installs.
        dialog.Activate();
        Assert.False(CodeModWarning.Trusted(dialog));
    }

    [Fact]
    public void TheWarning_SaysTheSameInSwedish()
    {
        var localizer = RepositoryLocalizer();
        Assert.True(localizer.TrySetLanguage("sv"));

        var dialog = CodeModWarning.Create(localizer, Review(SampleCode));

        Assert.Equal("Lita på moddens kod?", dialog.Title);
        Assert.Equal(
            "Sample Code Mod kör egen kod med full åtkomst till din dator. Installera den bara om du litar på skaparen.",
            string.Join(' ', dialog.MessageLines));
        Assert.Equal("Kodmoddar körs utan sandlåda", dialog.Caption);
        Assert.Equal(["Avbryt", "Installera"], dialog.Buttons.Select(b => b.Label));
    }

    [Fact]
    public void ChoosingInstall_IsTheOnlyWayToTrustTheMod()
    {
        var dialog = CodeModWarning.Create(RepositoryLocalizer(), Review(SampleCode));

        dialog.MoveFocus(1);
        dialog.Activate();

        Assert.Equal(CodeModWarning.InstallButton, dialog.Result);
        Assert.True(CodeModWarning.Trusted(dialog));
    }

    [Fact]
    public void EveryWarningKey_HasEnglishAndSwedishText()
    {
        var localizer = RepositoryLocalizer();

        foreach (var language in new[] { "en", "sv" })
        {
            Assert.True(localizer.TrySetLanguage(language));
            Assert.All(CodeModWarning.Keys, key => Assert.NotEqual(key, localizer.Get(key)));
            Assert.DoesNotContain(localizer.MissingKeys(language), CodeModWarning.Keys.Contains);
        }
    }
}
