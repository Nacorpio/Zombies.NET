namespace Zombies.Domain.Mods;

/// <summary>One definition file of a mod: a path relative to the mod root, and its JSON text.</summary>
public sealed record ModFile(string Path, string Json);

/// <summary>A non-JSON file of a mod, such as an icon: a path relative to the mod root, and its bytes. <see cref="ReadError"/> is set when the file could not be read.</summary>
public sealed record ModAsset(string Path, byte[] Bytes, string? ReadError = null);

/// <summary>A mod as read from disk or memory: where it came from, its manifest text, and its definition files.</summary>
public sealed record ModPackage(string Source, string ManifestJson, IReadOnlyList<ModFile> Files)
{
    /// <summary>Files that are not definitions. The mod loader ignores them; each system that owns a kind of asset reads its own.</summary>
    public IReadOnlyList<ModAsset> Assets { get; init; } = [];
}
