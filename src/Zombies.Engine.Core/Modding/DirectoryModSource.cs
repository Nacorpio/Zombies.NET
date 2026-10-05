using Zombies.Domain.Mods;

namespace Zombies.Engine.Core.Modding;

/// <summary>
/// Reads mods from disk. Each sub-folder of the mods root is one mod: a <c>mod.json</c> manifest and
/// definition files as <c>*.json</c> anywhere under <c>data/</c>, icons as <c>*.png</c> in <c>icons/</c>, string tables as <c>*.json</c> in <c>lang/</c>, and screen layouts as <c>*.json</c> in <c>ui/</c>. The same loader handles every mod, the Base mod included.
/// </summary>
public static class DirectoryModSource
{
    public const string ManifestFileName = "mod.json";
    public const string DataFolderName = "data";
    public const string IconsFolderName = "icons";
    public const string LanguageFolderName = "lang";
    public const string LayoutFolderName = "ui";

    public static IReadOnlyList<ModPackage> Read(string modsRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(modsRoot);
        if (!Directory.Exists(modsRoot))
        {
            throw new DirectoryNotFoundException($"Mods folder '{modsRoot}' does not exist.");
        }

        var packages = new List<ModPackage>();
        foreach (var directory in Directory.EnumerateDirectories(modsRoot).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var dataRoot = Path.Combine(directory, DataFolderName);
            var files = new List<ModFile>();
            if (Directory.Exists(dataRoot))
            {
                foreach (var path in Directory.EnumerateFiles(dataRoot, "*.json", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                    files.Add(new ModFile(relative, File.ReadAllText(path)));
                }
            }

            packages.Add(new ModPackage(Path.GetFileName(directory), File.ReadAllText(manifestPath), files)
            {
                Assets =
                [
                    .. ReadAssets(directory, IconsFolderName, "*.png"),
                    .. ReadAssets(directory, LanguageFolderName, "*.json"),
                    .. ReadAssets(directory, LayoutFolderName, "*.json"),
                ],
            });
        }

        return packages;
    }

    private static List<ModAsset> ReadAssets(string modDirectory, string folder, string pattern)
    {
        var assets = new List<ModAsset>();
        var root = Path.Combine(modDirectory, folder);
        if (!Directory.Exists(root))
        {
            return assets;
        }

        foreach (var path in Directory.EnumerateFiles(root, pattern).Order(StringComparer.Ordinal))
        {
            var relative = folder + "/" + Path.GetFileName(path);
            try
            {
                assets.Add(new ModAsset(relative, File.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                assets.Add(new ModAsset(relative, [], ex.Message));
            }
        }

        return assets;
    }

    /// <summary>Finds the <c>mods</c> folder by walking up from <paramref name="start"/>, so it works from the repository and from a published build.</summary>
    public static string Find(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "mods");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find a 'mods' folder. Pass one with --mods.");
    }
}
