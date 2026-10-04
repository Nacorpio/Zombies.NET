using Zombies.Domain.Mods;

namespace Zombies.Engine.Core.Modding;

/// <summary>
/// Reads mods from disk. Each sub-folder of the mods root is one mod: a <c>mod.json</c> manifest and
/// definition files as <c>*.json</c> anywhere under <c>data/</c>, and icons as <c>*.png</c> in <c>icons/</c>. The same loader handles every mod, the Base mod included.
/// </summary>
public static class DirectoryModSource
{
    public const string ManifestFileName = "mod.json";
    public const string DataFolderName = "data";
    public const string IconsFolderName = "icons";

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

            packages.Add(new ModPackage(Path.GetFileName(directory), File.ReadAllText(manifestPath), files) { Assets = ReadIcons(directory) });
        }

        return packages;
    }

    private static List<ModAsset> ReadIcons(string modDirectory)
    {
        var assets = new List<ModAsset>();
        var iconsRoot = Path.Combine(modDirectory, IconsFolderName);
        if (!Directory.Exists(iconsRoot))
        {
            return assets;
        }

        foreach (var path in Directory.EnumerateFiles(iconsRoot, "*.png").Order(StringComparer.Ordinal))
        {
            var relative = IconsFolderName + "/" + Path.GetFileName(path);
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
}
