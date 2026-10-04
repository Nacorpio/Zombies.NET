using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

public sealed class IconLoaderTests
{
    private static readonly (byte R, byte G, byte B, byte A) Clear = (0, 0, 0, 0);

    private static readonly string[] BadNames = ["garbage", "empty", "blank"];

    /// <summary>A PNG with one lit block, placed by <paramref name="column"/>, so two icons can be told apart.</summary>
    private static byte[] Png(int column = 4) =>
        TestPng.Rgba(IconSet.Size, IconSet.Size, (x, y) => x >= column && x < column + 6 && y is >= 4 and < 10 ? TestPng.C(255, 255, 255, 255) : Clear);

    private static ModPackage Mod(string id, string[]? dependsOn = null, params (string Path, byte[] Bytes)[] assets)
    {
        var dependencies = string.Join(",", (dependsOn ?? []).Select(d => $$"""{"id":"{{d}}","minVersion":"0.1.0"}"""));
        var manifest = $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","side":"both","dependencies":[{{dependencies}}]}""";
        return new ModPackage(id, manifest, [])
        {
            Assets = [.. assets.Select(a => new ModAsset(a.Path, a.Bytes))],
        };
    }

    private static IconLoadResult Load(params ModPackage[] packages) => IconLoader.Load(packages, ModLoader.Load(packages));

    private static (string, byte[])[] BuiltInIcons() => [.. IconNames.BuiltIn.Select((n, i) => ($"icons/{n}.png", Png(i)))];

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static IconLoadResult LoadRepositoryIcons()
    {
        var packages = DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods"));
        return IconLoader.Load(packages, ModLoader.Load(packages));
    }

    [Fact]
    public void BaseMod_ShipsTheTwelveBuiltInIconsAsPngsAndTheyAllLoad()
    {
        var result = LoadRepositoryIcons();

        Assert.Empty(result.Problems);
        foreach (var name in IconNames.BuiltIn)
        {
            Assert.True(result.Icons.Exists(name), $"missing icon '{name}'");
            var lit = result.Icons.Mask(name).ToArray().Count(b => b != 0);
            Assert.InRange(lit, 40, 900);
        }

        Assert.Equal(12, IconNames.BuiltIn.Count);
        Assert.Equal(IconNames.BuiltIn.Count, result.Icons.Count);
    }

    [Fact]
    public void BuiltInRobot_KeepsItsSoftEyesAsAWeakerTone()
    {
        var mask = LoadRepositoryIcons().Icons.Mask(IconNames.Robot).ToArray();

        // Two 2 by 2 eyes of 16 by 16 art, each pixel now a 2 by 2 block.
        Assert.Equal(8 * 4, mask.Count(b => b is > 0 and < 255));
        Assert.Contains((byte)255, mask);
    }

    [Fact]
    public void ModIcons_LoadByFileName_AndAreIgnoredWhenNotPngsInTheIconsFolder()
    {
        var result = Load(
            Mod("base", null, BuiltInIcons()),
            Mod("extra", ["base"], ("icons/axe.png", Png()), ("icons/notes.txt", [1, 2, 3]), ("data/hidden.png", Png())));

        Assert.Empty(result.Problems);
        Assert.True(result.Icons.Exists("axe"));
        Assert.False(result.Icons.Exists("notes"));
        Assert.False(result.Icons.Exists("hidden"));
    }

    [Fact]
    public void ModCanOverrideABaseIcon_WhenItDependsOnBase()
    {
        var plain = Load(Mod("base", null, BuiltInIcons()));
        var result = Load(Mod("base", null, BuiltInIcons()), Mod("reskin", ["base"], ("icons/robot.png", Png(20))));

        Assert.Empty(result.Problems);
        Assert.False(result.Icons.Mask(IconNames.Robot).SequenceEqual(plain.Icons.Mask(IconNames.Robot)));
        Assert.True(result.Icons.Mask(IconNames.Robot)[(5 * IconSet.Size) + 22] != 0);
        Assert.Equal(plain.Icons.Count, result.Icons.Count);
        Assert.True(result.Icons.Mask(IconNames.Use).SequenceEqual(plain.Icons.Mask(IconNames.Use)));
    }

    [Fact]
    public void LaterModInLoadOrder_WinsAnOverride()
    {
        var result = Load(
            Mod("base", null, BuiltInIcons()),
            Mod("first", ["base"], ("icons/robot.png", Png(2))),
            Mod("second", ["base", "first"], ("icons/robot.png", Png(24))));

        Assert.Empty(result.Problems);
        Assert.NotEqual(0, result.Icons.Mask(IconNames.Robot)[(5 * IconSet.Size) + 26]);
        Assert.Equal(0, result.Icons.Mask(IconNames.Robot)[(5 * IconSet.Size) + 3]);
    }

    [Fact]
    public void ModThatDoesNotDependOnTheOwner_CannotReplaceItsIcon()
    {
        var baseOnly = Load(Mod("base", null, BuiltInIcons()));
        var result = Load(Mod("base", null, BuiltInIcons()), Mod("stranger", null, ("icons/robot.png", Png(20))));

        var problem = Assert.Single(result.Problems);
        Assert.Equal(("stranger", "robot"), (problem.ModId, problem.Icon));
        Assert.Contains("does not depend on", problem.Message);
        Assert.True(result.Icons.Mask(IconNames.Robot).SequenceEqual(baseOnly.Icons.Mask(IconNames.Robot)));
    }

    [Fact]
    public void BadImages_AreReportedAndNeverStopTheOthers()
    {
        var result = Load(
            Mod("base", null, BuiltInIcons()),
            Mod("messy", ["base"],
                ("icons/garbage.png", [1, 2, 3, 4]),
                ("icons/empty.png", []),
                ("icons/blank.png", TestPng.Rgba(40, 40, (_, _) => Clear)),
                ("icons/good.png", Png())));

        Assert.Equal(["blank", "empty", "garbage"], result.Problems.Select(p => p.Icon).Order(StringComparer.Ordinal));
        Assert.All(result.Problems, p => Assert.Contains("placeholder", p.Message));
        Assert.True(result.Icons.Exists("good"));
        Assert.All(BadNames, name =>
        {
            Assert.False(result.Icons.Exists(name));
            Assert.Equal(result.Icons.Uv(IconNames.Unknown), result.Icons.Uv(name));
        });
    }

    [Fact]
    public void BadOverride_KeepsTheIconItWouldHaveReplaced()
    {
        var plain = Load(Mod("base", null, BuiltInIcons()));
        var result = Load(Mod("base", null, BuiltInIcons()), Mod("broken", ["base"], ("icons/robot.png", [9, 9, 9])));

        var problem = Assert.Single(result.Problems);
        Assert.Contains("previous icon is kept", problem.Message);
        Assert.True(result.Icons.Mask(IconNames.Robot).SequenceEqual(plain.Icons.Mask(IconNames.Robot)));
    }

    [Fact]
    public void UnreadableFile_IsReported()
    {
        var package = Mod("base", null, BuiltInIcons()) with { };
        var unreadable = new ModPackage("extra", Mod("extra", ["base"]).ManifestJson, [])
        {
            Assets = [new ModAsset("icons/locked.png", [], "Access denied.")],
        };

        var result = Load(package, unreadable);

        var problem = Assert.Single(result.Problems);
        Assert.Contains("could not be read: Access denied.", problem.Message);
        Assert.False(result.Icons.Exists("locked"));
    }

    [Fact]
    public void BadIconName_IsReported()
    {
        var result = Load(Mod("base", null, BuiltInIcons()), Mod("extra", ["base"], ("icons/Bad-Name.png", Png())));

        var problem = Assert.Single(result.Problems);
        Assert.Contains("a-z, 0-9 and _", problem.Message);
    }

    [Fact]
    public void MissingBuiltInIcon_IsReportedAndDrawsAsThePlaceholder()
    {
        var result = Load(Mod("base", null, BuiltInIcons().Where(i => !i.Item1.EndsWith("/check.png", StringComparison.Ordinal)).ToArray()));

        var problem = Assert.Single(result.Problems);
        Assert.Equal((ModLoader.BaseModId, IconNames.Check), (problem.ModId, problem.Icon));
        Assert.Equal(result.Icons.Uv(IconNames.Unknown), result.Icons.Uv(IconNames.Check));
    }

    [Fact]
    public void WhenModsFailToLoad_IconsFallBackToThePlaceholderAndOnlyThatFailureIsReported()
    {
        var broken = new ModPackage("broken", "{ not json", []);

        var result = Load(Mod("base", null, BuiltInIcons()), broken);

        Assert.Empty(result.Problems);
        Assert.Equal(1, result.Icons.Count);
    }

    [Fact]
    public void LoadingIsDeterministic_NamesFollowLoadOrderThenFileName()
    {
        var result = Load(
            Mod("base", null, BuiltInIcons()),
            Mod("extra", ["base"], ("icons/zed.png", Png()), ("icons/ant.png", Png())));

        Assert.Equal(IconNames.Unknown, result.Icons.Names[0]);
        Assert.Equal(["ant", "zed"], result.Icons.Names.TakeLast(2));
    }
}
