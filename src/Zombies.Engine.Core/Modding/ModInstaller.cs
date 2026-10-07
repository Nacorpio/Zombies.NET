using Zombies.Domain.Mods;

namespace Zombies.Engine.Core.Modding;

/// <summary>What installing a mod would mean, shown to the player before anything is copied.</summary>
/// <param name="CodeFiles">Every <c>.dll</c> the mod carries, relative to its folder, whether its manifest lists it or not.</param>
public sealed record ModInstallReview(ModManifest Manifest, IReadOnlyList<string> CodeFiles)
{
    /// <summary>A Code mod: it lists assemblies or carries any. Its code would run with the game's full rights.</summary>
    public bool IsCodeMod => Manifest.IsCodeMod || CodeFiles.Count > 0;

    /// <summary>Whether the player must be warned and agree to trust the mod's code before it is installed (ADR 0004).</summary>
    public bool NeedsTrust => IsCodeMod;
}

public enum ModInstallStatus
{
    Installed,

    /// <summary>The mod is a Code mod and the player has not agreed to trust it. Nothing was copied.</summary>
    NeedsTrust,

    /// <summary>A mod with the same id is already in the mods folder. Nothing was copied.</summary>
    AlreadyInstalled,

    /// <summary>The folder is not a mod, or its manifest is invalid. Nothing was copied.</summary>
    NotAMod,
}

/// <param name="Review">What the mod is, or null when it is not a mod.</param>
/// <param name="InstalledTo">The folder the mod was copied to, when it was.</param>
public sealed record ModInstallResult(ModInstallStatus Status, ModInstallReview? Review, string Detail, string? InstalledTo = null);

/// <summary>
/// Installs a mod by copying it into the mods folder. A Data mod installs at once. A Code mod is trusted code with no sandbox,
/// so it installs only when the player has seen the trusted-code warning and agreed; until then <see cref="Install"/>
/// answers <see cref="ModInstallStatus.NeedsTrust"/> and copies nothing. Only what the game reads is copied: the manifest and
/// the <c>data</c>, <c>icons</c>, <c>lang</c>, <c>ui</c>, <c>rigs</c>, and <c>assemblies</c> folders.
/// </summary>
public static class ModInstaller
{
    private static readonly string[] CopiedFolders =
    [
        DirectoryModSource.DataFolderName,
        DirectoryModSource.IconsFolderName,
        DirectoryModSource.LanguageFolderName,
        DirectoryModSource.LayoutFolderName,
        DirectoryModSource.RigFolderName,
        DirectoryModSource.AssemblyFolderName,
    ];

    /// <summary>Reads what the mod in <paramref name="modDirectory"/> is, without installing it.</summary>
    /// <param name="error">Why the folder is not a mod, when it returns null.</param>
    public static ModInstallReview? Review(string modDirectory, out string error)
    {
        ArgumentException.ThrowIfNullOrEmpty(modDirectory);
        var manifestPath = Path.Combine(modDirectory, DirectoryModSource.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            error = $"'{modDirectory}' is not a mod: it has no {DirectoryModSource.ManifestFileName}.";
            return null;
        }

        if (!ModManifest.TryParse(File.ReadAllText(manifestPath), out var manifest, out var manifestError))
        {
            error = $"'{modDirectory}' has an invalid {DirectoryModSource.ManifestFileName}: {manifestError}";
            return null;
        }

        var code = Directory.EnumerateFiles(modDirectory, "*.dll", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(modDirectory, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        error = string.Empty;
        return new ModInstallReview(manifest, code);
    }

    /// <param name="modDirectory">The folder of the mod to install, holding its <c>mod.json</c>.</param>
    /// <param name="modsRoot">The game's mods folder. The mod is copied into a folder named after its id.</param>
    /// <param name="trustCode">Whether the player agreed to the trusted-code warning. Ignored for a Data mod.</param>
    public static ModInstallResult Install(string modDirectory, string modsRoot, bool trustCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(modsRoot);
        if (Review(modDirectory, out var error) is not { } review)
        {
            return new ModInstallResult(ModInstallStatus.NotAMod, null, error);
        }

        if (review.NeedsTrust && !trustCode)
        {
            return new ModInstallResult(ModInstallStatus.NeedsTrust, review, $"'{review.Manifest.Id}' is a Code mod. Its code runs with full access to this computer, so it installs only once the player trusts it.");
        }

        var target = Path.Combine(modsRoot, review.Manifest.Id);
        if (Directory.Exists(target))
        {
            return new ModInstallResult(ModInstallStatus.AlreadyInstalled, review, $"A mod is already installed at '{target}'.");
        }

        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(modDirectory, DirectoryModSource.ManifestFileName), Path.Combine(target, DirectoryModSource.ManifestFileName));
        foreach (var folder in CopiedFolders)
        {
            var from = Path.Combine(modDirectory, folder);
            if (!Directory.Exists(from))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(target, Path.GetRelativePath(modDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to);
            }
        }

        return new ModInstallResult(ModInstallStatus.Installed, review, $"Installed '{review.Manifest.Id}' {review.Manifest.Version}.", target);
    }
}
