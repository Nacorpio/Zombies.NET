using System.Text;
using Zombies.Domain.Mods;

namespace Zombies.Engine.Ui;

/// <summary>A layout that could not be used. Reported to the player and the log; it never stops the game or the other layouts.</summary>
public sealed record LayoutProblem(string ModId, string File, string Message)
{
    public override string ToString() => $"[Layout] {ModId} ({File}): {Message}";
}

public sealed record LayoutLoadResult(IReadOnlyDictionary<string, UiLayout> Layouts, IReadOnlyList<LayoutProblem> Problems)
{
    public UiLayout? Get(string id) => Layouts.GetValueOrDefault(id);
}

/// <summary>
/// Builds the screens from the <c>ui/</c> folders of the loaded mods. A layout is <c>ui/&lt;id&gt;.json</c> and its file name
/// must match the id it declares. Mods apply in load order, so a later mod can replace a screen. A bad layout is
/// reported and skipped.
/// </summary>
public static class LayoutLoader
{
    public const string FolderPrefix = "ui/";
    public const string Extension = ".json";

    public static LayoutLoadResult Load(IReadOnlyList<ModPackage> packages, ModLoadResult mods)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(mods);
        var problems = new List<LayoutProblem>();
        var layouts = new Dictionary<string, UiLayout>(StringComparer.Ordinal);
        if (!mods.IsSuccess)
        {
            return new LayoutLoadResult(layouts, problems);
        }

        var byId = packages
            .Select(p => ModManifest.TryParse(p.ManifestJson, out var manifest, out _) ? (Manifest: manifest, Package: p) : default)
            .Where(x => x.Manifest is not null)
            .ToDictionary(x => x.Manifest.Id, StringComparer.Ordinal);

        foreach (var loaded in mods.Mods)
        {
            var assets = byId[loaded.Manifest.Id].Package.Assets
                .Where(a => a.Path.StartsWith(FolderPrefix, StringComparison.Ordinal) && a.Path.EndsWith(Extension, StringComparison.Ordinal))
                .OrderBy(a => a.Path, StringComparer.Ordinal);
            foreach (var asset in assets)
            {
                if (asset.ReadError is not null)
                {
                    problems.Add(new LayoutProblem(loaded.Manifest.Id, asset.Path, asset.ReadError));
                    continue;
                }

                if (!UiLayout.TryParse(Encoding.UTF8.GetString(asset.Bytes).TrimStart('\uFEFF'), out var layout, out var error))
                {
                    problems.Add(new LayoutProblem(loaded.Manifest.Id, asset.Path, error));
                    continue;
                }

                var expected = asset.Path[FolderPrefix.Length..^Extension.Length];
                if (!string.Equals(expected, layout.Id, StringComparison.Ordinal))
                {
                    problems.Add(new LayoutProblem(loaded.Manifest.Id, asset.Path, $"The file is named for '{expected}' but the layout says its id is '{layout.Id}'."));
                    continue;
                }

                layouts[layout.Id] = layout;
            }
        }

        return new LayoutLoadResult(layouts, problems);
    }
}
