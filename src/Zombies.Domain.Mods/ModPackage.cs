namespace Zombies.Domain.Mods;

/// <summary>One definition file of a mod: a path relative to the mod root, and its JSON text.</summary>
public sealed record ModFile(string Path, string Json);

/// <summary>A mod as read from disk or memory: where it came from, its manifest text, and its definition files.</summary>
public sealed record ModPackage(string Source, string ManifestJson, IReadOnlyList<ModFile> Files);
