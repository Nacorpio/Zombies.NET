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
}

/// <summary>A reason the mod set cannot load. <see cref="ModId"/> is the mod's id, or its source when no id could be read.</summary>
public sealed record ModLoadError(ModLoadErrorKind Kind, string ModId, string? File, string Message)
{
    public override string ToString() => File is null ? $"[{Kind}] {ModId}: {Message}" : $"[{Kind}] {ModId} ({File}): {Message}";
}

public sealed record LoadedMod(ModManifest Manifest, int Order);

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
