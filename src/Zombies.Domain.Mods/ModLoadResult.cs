namespace Zombies.Domain.Mods;

public enum ModLoadErrorKind
{
    InvalidManifest,
    DuplicateModId,
    MissingDependency,
    DependencyVersionTooLow,
    DependencyCycle,
    InvalidJson,
    InvalidDefinition,
    ForeignNamespace,
    DuplicateContentId,
    OverrideTargetMissing,
    PatchTargetMissing,
    UndeclaredDependency,
    PatchChangesId,
    CopyFromMissing,
    CopyFromCycle,
    InvalidOperator,
}

/// <summary>A reason the mod set cannot load. <see cref="ModId"/> is the mod's id, or its source when no id could be read.</summary>
public sealed record ModLoadError(ModLoadErrorKind Kind, string ModId, string? File, string Message)
{
    public override string ToString() => File is null ? $"[{Kind}] {ModId}: {Message}" : $"[{Kind}] {ModId} ({File}): {Message}";
}

/// <summary>A mod that loaded, its place in load order, and the <see cref="ModPackage.ComputeContentHash"/> of what it shipped.</summary>
public sealed record LoadedMod(ModManifest Manifest, int Order, string ContentHash);

/// <summary>
/// All-or-nothing outcome of loading a mod set. On any error nothing loads: <see cref="Registry"/> is empty
/// and <see cref="Errors"/> lists every problem found.
/// </summary>
public sealed class ModLoadResult
{
    internal ModLoadResult(IReadOnlyList<LoadedMod> mods, DefinitionRegistry registry, IReadOnlyList<ModLoadError> errors)
    {
        Mods = mods;
        Registry = registry;
        Errors = errors;
    }

    public bool IsSuccess => Errors.Count == 0;

    /// <summary>Loaded mods in load order.</summary>
    public IReadOnlyList<LoadedMod> Mods { get; }

    public DefinitionRegistry Registry { get; }

    public IReadOnlyList<ModLoadError> Errors { get; }
}
