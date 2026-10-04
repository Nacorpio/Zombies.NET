using System.Text.RegularExpressions;
using Zombies.Domain.Mods;

namespace Zombies.Engine.Render;

/// <summary>An icon that could not be used. Reported to the player and the log; it never stops the game or the other icons.</summary>
public sealed record IconProblem(string ModId, string Icon, string Message)
{
    public override string ToString() => $"[Icon] {ModId} ({Icon}): {Message}";
}

public sealed record IconLoadResult(IconSet Icons, IReadOnlyList<IconProblem> Problems);

/// <summary>
/// Builds the icon set from the icons folders of the loaded mods. An icon is a PNG named after the icon, such as
/// <c>icons/robot.png</c>. Mods apply in load order, so a mod can add icons and replace the icon of a mod it depends on.
/// Unlike manifests, a bad icon does not fail the load: it is reported and the placeholder, or the icon it would have replaced, stays.
/// </summary>
public static partial class IconLoader
{
    public const string FolderPrefix = "icons/";
    public const string Extension = ".png";

    public static IconLoadResult Load(IReadOnlyList<ModPackage> packages, ModLoadResult mods, IconStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(mods);
        style ??= IconStyle.Default;

        // If the mods did not load there is no load order to apply, and the mod errors are reported on their own.
        if (!mods.IsSuccess)
        {
            return new IconLoadResult(IconSet.Placeholder, []);
        }

        var problems = new List<IconProblem>();
        var byId = packages
            .Select(p => ModManifest.TryParse(p.ManifestJson, out var manifest, out _) ? (Manifest: manifest, Package: p) : default)
            .Where(x => x.Manifest is not null)
            .ToDictionary(x => x.Manifest.Id, StringComparer.Ordinal);

        // Icon name to its mask and the mod that supplied it. The placeholder is not in here, so anyone may replace it.
        var icons = new Dictionary<string, (byte[] Mask, string Owner)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var loaded in mods.Mods)
        {
            var manifest = loaded.Manifest;
            var allowed = DependencyClosure(manifest, byId);
            foreach (var asset in byId[manifest.Id].Package.Assets.OrderBy(a => a.Path, StringComparer.Ordinal))
            {
                if (!asset.Path.StartsWith(FolderPrefix, StringComparison.Ordinal) || !asset.Path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = asset.Path[FolderPrefix.Length..^Extension.Length];
                if (!NamePattern().IsMatch(name))
                {
                    problems.Add(new IconProblem(manifest.Id, name, "An icon name may use only a-z, 0-9 and _."));
                    continue;
                }

                if (icons.TryGetValue(name, out var existing) && existing.Mask is not null && !allowed.Contains(existing.Owner))
                {
                    problems.Add(new IconProblem(manifest.Id, name, $"'{name}' belongs to '{existing.Owner}', which this mod does not depend on, so it was not replaced."));
                    continue;
                }

                if (!TryMake(asset, style, out var mask, out var error))
                {
                    problems.Add(new IconProblem(manifest.Id, name, error + (existing.Mask is null ? " The placeholder is drawn." : " The previous icon is kept.")));
                    continue;
                }

                if (existing.Mask is null)
                {
                    order.Add(name);
                }

                icons[name] = (mask, manifest.Id);
            }
        }

        if (byId.ContainsKey(ModLoader.BaseModId))
        {
            foreach (var builtIn in IconNames.BuiltIn)
            {
                if (!icons.ContainsKey(builtIn) && !problems.Any(p => p.Icon == builtIn))
                {
                    problems.Add(new IconProblem(ModLoader.BaseModId, builtIn, "The built-in icon is missing. The placeholder is drawn."));
                }
            }
        }

        return new IconLoadResult(IconSet.Create(order.Select(n => (n, icons[n].Mask))), problems);
    }

    private static bool TryMake(ModAsset asset, IconStyle style, out byte[] mask, out string error)
    {
        mask = [];
        if (asset.ReadError is not null)
        {
            error = $"The file could not be read: {asset.ReadError}";
            return false;
        }

        if (!PngReader.TryDecode(asset.Bytes, out var image, out var decodeError))
        {
            error = decodeError;
            return false;
        }

        mask = IconNormalizer.Normalize(image, style);
        if (!mask.Any(b => b != 0))
        {
            error = "The image is empty: nothing in it is visible.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>The mod itself plus everything it depends on, directly or not.</summary>
    private static HashSet<string> DependencyClosure(ModManifest manifest, Dictionary<string, (ModManifest Manifest, ModPackage Package)> byId)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { manifest.Id };
        var pending = new Stack<ModManifest>([manifest]);
        while (pending.Count > 0)
        {
            foreach (var dependency in pending.Pop().Dependencies)
            {
                if (allowed.Add(dependency.Id) && byId.TryGetValue(dependency.Id, out var found))
                {
                    pending.Push(found.Manifest);
                }
            }
        }

        return allowed;
    }

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex NamePattern();
}
