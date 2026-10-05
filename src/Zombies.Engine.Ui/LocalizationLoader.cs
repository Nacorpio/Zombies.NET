using System.Text;
using Zombies.Domain.Mods;

namespace Zombies.Engine.Ui;

/// <summary>A string table that could not be used. Reported to the player and the log; it never stops the game or the other tables.</summary>
public sealed record LocalizationProblem(string ModId, string File, string Message)
{
    public override string ToString() => $"[Language] {ModId} ({File}): {Message}";
}

public sealed record LocalizationLoadResult(Localizer Localizer, IReadOnlyList<LocalizationProblem> Problems);

/// <summary>
/// Builds the <see cref="Localizer"/> from the <c>lang/</c> folders of the loaded mods. A table is <c>lang/&lt;code&gt;.json</c>
/// and its file name must match the language it declares. Mods apply in load order, so a mod can add keys and replace
/// the text of the mods before it. A bad table is reported and skipped.
/// </summary>
public static class LocalizationLoader
{
    public const string FolderPrefix = "lang/";
    public const string Extension = ".json";

    public static LocalizationLoadResult Load(IReadOnlyList<ModPackage> packages, ModLoadResult mods, string fallbackLanguage = "en")
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(mods);
        var problems = new List<LocalizationProblem>();
        var tables = new List<StringTable>();
        if (!mods.IsSuccess)
        {
            return new LocalizationLoadResult(new Localizer([], fallbackLanguage), problems);
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
                    problems.Add(new LocalizationProblem(loaded.Manifest.Id, asset.Path, asset.ReadError));
                    continue;
                }

                if (!StringTable.TryParse(Encoding.UTF8.GetString(asset.Bytes).TrimStart('\uFEFF'), out var table, out var error))
                {
                    problems.Add(new LocalizationProblem(loaded.Manifest.Id, asset.Path, error));
                    continue;
                }

                var expected = asset.Path[FolderPrefix.Length..^Extension.Length];
                if (!string.Equals(expected, table.Language, StringComparison.Ordinal))
                {
                    problems.Add(new LocalizationProblem(loaded.Manifest.Id, asset.Path, $"The file is named for '{expected}' but the table says its language is '{table.Language}'."));
                    continue;
                }

                tables.Add(table);
            }
        }

        return new LocalizationLoadResult(new Localizer(tables, fallbackLanguage), problems);
    }
}
