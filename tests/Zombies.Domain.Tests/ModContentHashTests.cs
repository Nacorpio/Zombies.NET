using Zombies.Domain.Mods;

namespace Zombies.Domain.Tests;

public sealed class ModContentHashTests
{
    private static ModPackage Mod(string manifest, params (string Path, string Json)[] files) =>
        new("test", manifest, [.. files.Select(f => new ModFile(f.Path, f.Json))]);

    private const string Manifest = """{ "id": "test", "version": "1.0.0" }""";

    [Fact]
    public void ContentHash_IgnoresFileOrderAndLineEndings()
    {
        var a = Mod(Manifest, ("data/a.json", "{\n \"x\": 1\n}"), ("data/b.json", "{}"));
        var b = Mod(Manifest.Replace(" ", " ", StringComparison.Ordinal), ("data/b.json", "{}"), ("data/a.json", "{\r\n \"x\": 1\r\n}"));

        Assert.Equal(a.ComputeContentHash(), b.ComputeContentHash());
    }

    [Fact]
    public void ContentHash_ChangesWhenAnyFileOrPathChanges()
    {
        var original = Mod(Manifest, ("data/a.json", "{ \"x\": 1 }")).ComputeContentHash();

        Assert.NotEqual(original, Mod(Manifest, ("data/a.json", "{ \"x\": 2 }")).ComputeContentHash());
        Assert.NotEqual(original, Mod(Manifest, ("data/renamed.json", "{ \"x\": 1 }")).ComputeContentHash());
        Assert.NotEqual(original, Mod("""{ "id": "test", "version": "1.0.1" }""", ("data/a.json", "{ \"x\": 1 }")).ComputeContentHash());
    }

    [Fact]
    public void ContentHash_CoversACodeModsAssembliesByteForByte()
    {
        var data = Mod(Manifest, ("data/a.json", "{}"));
        var code = data with { Assemblies = [new ModAsset("assemblies/A.dll", [1, 2, 3])] };

        Assert.NotEqual(data.ComputeContentHash(), code.ComputeContentHash());
        Assert.NotEqual(code.ComputeContentHash(), (data with { Assemblies = [new ModAsset("assemblies/A.dll", [1, 2, 4])] }).ComputeContentHash());
    }

    [Fact]
    public void Load_RecordsEachModsContentHash()
    {
        var package = Mod(Manifest);

        var result = ModLoader.Load([package]);

        Assert.Equal(package.ComputeContentHash(), Assert.Single(result.Mods).ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", result.Mods[0].ContentHash);
    }
}
