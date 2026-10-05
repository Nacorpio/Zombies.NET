namespace Zombies.Domain.Mods;

/// <summary>What a game process runs, which decides which mods it loads. Solo play runs both, with the Server embedded.</summary>
[Flags]
public enum ProcessRole
{
    /// <summary>A dedicated Server, headless.</summary>
    Server = 1,

    /// <summary>A client playing on a Server in another process.</summary>
    Client = 2,

    /// <summary>A client with the Server embedded in the same process.</summary>
    Solo = Server | Client,
}

/// <summary>
/// Applies each mod's declared <see cref="ModSide"/>. A process without a client, the dedicated Server, never loads a
/// <see cref="ModSide.Client"/> mod: not its data and not its code. A mod's code runs only where its side runs, so a
/// <see cref="ModSide.Server"/> mod's code never runs on a client of another process, though its data loads there.
/// </summary>
public static class ModSides
{
    /// <summary>Whether a mod of this side loads any of its content, data or code, in a process of this role.</summary>
    public static bool LoadsIn(this ModSide side, ProcessRole role) =>
        side != ModSide.Client || role.HasFlag(ProcessRole.Client);

    /// <summary>Whether a Code mod of this side runs its code in a process of this role.</summary>
    public static bool RunsCodeIn(this ModSide side, ProcessRole role) => side switch
    {
        ModSide.Client => role.HasFlag(ProcessRole.Client),
        ModSide.Server => role.HasFlag(ProcessRole.Server),
        _ => true,
    };

    /// <summary>
    /// The packages a process of this role loads, in the order given. A package whose manifest cannot be read is kept, so
    /// the mod loader reports it instead of it vanishing.
    /// </summary>
    public static IReadOnlyList<ModPackage> ForRole(IEnumerable<ModPackage> packages, ProcessRole role)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return [.. packages.Where(p => !ModManifest.TryParse(p.ManifestJson, out var manifest, out _) || manifest.Side.LoadsIn(role))];
    }
}
