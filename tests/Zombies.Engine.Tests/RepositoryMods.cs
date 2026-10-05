using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Tests.Rigs;

namespace Zombies.Engine.Tests;

/// <summary>
/// The mods in the repository, the sample Code mod included, loaded the way a game process of a role loads them: packages
/// filtered by side, then data, then code. Each role is loaded once per test run, since an assembly cannot be unloaded.
/// </summary>
internal static class RepositoryMods
{
    private static readonly Dictionary<ProcessRole, Lazy<Loaded>> Cache = new()
    {
        [ProcessRole.Server] = new(() => Load(ProcessRole.Server)),
        [ProcessRole.Client] = new(() => Load(ProcessRole.Client)),
        [ProcessRole.Solo] = new(() => Load(ProcessRole.Solo)),
    };

    public static string Root => Path.Combine(RigTestData.RepoRoot(), "mods");

    public static Loaded For(ProcessRole role) => Cache[role].Value;

    /// <summary>A Trait registry with the base game's Traits and those the Code mods registered, as a Server builds it.</summary>
    public static TraitRegistry Traits(ProcessRole role = ProcessRole.Server)
    {
        var traits = new TraitRegistry();
        BaseTraits.Register(traits);
        CodeModTraits.Register(traits, For(role).Code);
        return traits;
    }

    private static Loaded Load(ProcessRole role)
    {
        var packages = ModSides.ForRole(DirectoryModSource.Read(Root), role);
        var mods = ModLoader.Load(packages);
        Assert.True(mods.IsSuccess, string.Join(Environment.NewLine, mods.Errors));
        var code = CodeModLoader.Load(packages, mods, role);
        Assert.True(code.IsSuccess, string.Join(Environment.NewLine, code.Problems));
        return new Loaded(packages, mods, code);
    }

    internal sealed record Loaded(IReadOnlyList<ModPackage> Packages, ModLoadResult Mods, CodeModLoadResult Code);
}
