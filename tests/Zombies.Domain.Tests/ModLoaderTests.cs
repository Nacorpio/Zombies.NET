using Zombies.Domain.Mods;

namespace Zombies.Domain.Tests;

public sealed class ModLoaderTests
{
    private static ModPackage Mod(string id, string version = "1.0.0", string extra = "", params (string Path, string Json)[] files) =>
        new(id, $$"""{ "id": "{{id}}", "version": "{{version}}"{{extra}} }""", [.. files.Select(f => new ModFile(f.Path, f.Json))]);

    private static string Deps(params string[] ids) =>
        $", \"dependencies\": [{string.Join(", ", ids.Select(i => $$"""{ "id": "{{i}}" }"""))}]";

    private static (string Path, string Json) Item(string id, int maxStack = 4) =>
        ($"data/{id.Replace(':', '_').Replace('/', '_')}.json", $$"""{ "id": "{{id}}", "mass": "1 kg", "volume": "1 l", "maxStack": {{maxStack}} }""");

    private static (string Path, string Json) Patch(string target, string body, string name = "patch") =>
        ($"data/{name}.json", $$"""{ "patch": "{{target}}", {{body}} }""");

    private static ContentId Id(string value) => ContentId.TryParse(value, out var id) ? id : throw new ArgumentException(value);

    private static string JsonOf(ModLoadResult result, string id) =>
        result.Registry.TryGet(Id(id), out var d) ? d.Json : throw new InvalidOperationException($"{id} missing");

    private static void AssertFails(ModLoadResult result, ModLoadErrorKind kind)
    {
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Kind == kind);
        Assert.Equal(0, result.Registry.Count);
        Assert.Empty(result.Mods);
    }

    [Fact]
    public void Load_ReturnsModsInLoadOrderWithBaseFirst()
    {
        var result = ModLoader.Load([Mod("zeta"), Mod("alpha"), Mod("base")]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["base", "alpha", "zeta"], result.Mods.Select(m => m.Manifest.Id));
        Assert.Equal([0, 1, 2], result.Mods.Select(m => m.Order));
    }

    [Fact]
    public void Load_OrdersByDependencyRegardlessOfInputOrder()
    {
        var result = ModLoader.Load([Mod("c", extra: Deps("b")), Mod("b", extra: Deps("a")), Mod("a")]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["a", "b", "c"], result.Mods.Select(m => m.Manifest.Id));
    }

    [Fact]
    public void Load_PreferredOrderBreaksTies()
    {
        var result = ModLoader.Load([Mod("a"), Mod("b"), Mod("c")], preferredOrder: ["c", "a"]);

        Assert.Equal(["c", "a", "b"], result.Mods.Select(m => m.Manifest.Id));
    }

    [Fact]
    public void Load_LoadAfterOrdersWhenPresentAndIsIgnoredWhenAbsent()
    {
        var present = ModLoader.Load([Mod("a", extra: ", \"loadAfter\": [\"b\"]"), Mod("b")]);
        var absent = ModLoader.Load([Mod("a", extra: ", \"loadAfter\": [\"ghost\"]")]);

        Assert.Equal(["b", "a"], present.Mods.Select(m => m.Manifest.Id));
        Assert.True(absent.IsSuccess);
    }

    [Fact]
    public void Load_MissingDependencyFails()
    {
        AssertFails(ModLoader.Load([Mod("a", extra: Deps("ghost"))]), ModLoadErrorKind.MissingDependency);
    }

    [Fact]
    public void Load_DependencyVersionTooLowFails()
    {
        var dependent = Mod("a", extra: ", \"dependencies\": [{ \"id\": \"base\", \"minVersion\": \"2.0.0\" }]");

        AssertFails(ModLoader.Load([Mod("base", "1.9.0"), dependent]), ModLoadErrorKind.DependencyVersionTooLow);
        Assert.True(ModLoader.Load([Mod("base", "2.0.0"), dependent]).IsSuccess);
    }

    [Fact]
    public void Load_DependencyCycleFails()
    {
        AssertFails(ModLoader.Load([Mod("a", extra: Deps("b")), Mod("b", extra: Deps("a"))]), ModLoadErrorKind.DependencyCycle);
    }

    [Fact]
    public void Load_DuplicateModIdFails()
    {
        AssertFails(ModLoader.Load([Mod("a"), Mod("a", "2.0.0")]), ModLoadErrorKind.DuplicateModId);
    }

    [Theory]
    [InlineData("""{ "version": "1.0.0" }""")]
    [InlineData("""{ "id": "Bad Id", "version": "1.0.0" }""")]
    [InlineData("""{ "id": "a" }""")]
    [InlineData("""{ "id": "a", "version": "1.0" }""")]
    [InlineData("""{ "id": "a", "version": "1.0.0", "side": "everywhere" }""")]
    [InlineData("""{ "id": "a", "version": "1.0.0", "dependencies": "base" }""")]
    [InlineData("""{ "id": "a", "version": "1.0.0", "dependencies": [{ "minVersion": "1.0.0" }] }""")]
    [InlineData("""{ "id": "a", "version": "1.0.0", "loadAfter": [1] }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Load_InvalidManifestFails(string manifest)
    {
        AssertFails(ModLoader.Load([new ModPackage("folder", manifest, [])]), ModLoadErrorKind.InvalidManifest);
    }

    [Fact]
    public void Manifest_ReadsSideDependenciesAndDefaults()
    {
        Assert.True(ModManifest.TryParse(
            """{ "id": "m", "name": "My Mod", "version": "1.2.3", "side": "Server", "dependencies": [{ "id": "base", "minVersion": "0.1.0" }] }""",
            out var full,
            out _));
        Assert.True(ModManifest.TryParse("""{ "id": "m", "version": "1.0.0" }""", out var minimal, out _));

        Assert.Equal("My Mod", full.Name);
        Assert.Equal(new Version(1, 2, 3), full.Version);
        Assert.Equal(ModSide.Server, full.Side);
        Assert.Equal(new ModDependency("base", new Version(0, 1, 0)), Assert.Single(full.Dependencies));
        Assert.Equal("m", minimal.Name);
        Assert.Equal(ModSide.Both, minimal.Side);
        Assert.Empty(minimal.Dependencies);
    }

    [Fact]
    public void Load_RegistersDefinitionsByKind()
    {
        var result = ModLoader.Load([Mod("base", files: [Item("base:item/beans"), Item("base:item/water"), ("data/z.json", """{ "id": "base:zombie/walker" }""")])]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Registry.Count);
        Assert.Equal(["base:item/beans", "base:item/water"], result.Registry.OfKind("item").Select(d => d.Id.Value));
        Assert.Equal("base", result.Registry.OfKind("zombie").Single().DefinedBy);
    }

    [Fact]
    public void Load_AddingToAnotherModsNamespaceFails()
    {
        AssertFails(ModLoader.Load([Mod("a", files: Item("b:item/x"))]), ModLoadErrorKind.ForeignNamespace);
    }

    [Fact]
    public void Load_DuplicateContentIdWithoutOverrideFails()
    {
        var result = ModLoader.Load([Mod("base", files: Item("base:item/beans")), Mod("a", extra: Deps("base"), files: Item("base:item/beans"))]);

        AssertFails(result, ModLoadErrorKind.ForeignNamespace);
        var duplicateInOwnMod = ModLoader.Load([Mod("base", files: [Item("base:item/beans"), ("data/other.json", Item("base:item/beans").Json)])]);
        AssertFails(duplicateInOwnMod, ModLoadErrorKind.DuplicateContentId);
    }

    [Fact]
    public void Load_ExplicitOverrideReplacesTheWholeDefinition()
    {
        var replacement = ("data/o.json", """{ "id": "base:item/beans", "override": true, "mass": "2 kg", "volume": "2 l" }""");

        var result = ModLoader.Load([Mod("base", files: Item("base:item/beans")), Mod("a", extra: Deps("base"), files: replacement)]);

        Assert.True(result.IsSuccess);
        var definition = result.Registry.OfKind("item").Single();
        Assert.Equal("""{"id":"base:item/beans","mass":"2 kg","volume":"2 l"}""", definition.Json);
        Assert.Equal("base", definition.DefinedBy);
        Assert.Equal(["a"], definition.ModifiedBy);
    }

    [Fact]
    public void Load_OverrideOfAMissingDefinitionFails()
    {
        var missing = ("data/o.json", """{ "id": "base:item/ghost", "override": true, "mass": "1 kg", "volume": "1 l" }""");

        AssertFails(ModLoader.Load([Mod("base"), Mod("a", extra: Deps("base"), files: missing)]), ModLoadErrorKind.OverrideTargetMissing);
    }

    [Fact]
    public void Load_PatchMergesFieldsAndRecordsWhoChangedIt()
    {
        var result = ModLoader.Load([Mod("base", files: Item("base:item/beans")), Mod("a", extra: Deps("base"), files: Patch("base:item/beans", "\"maxStack\": 12"))]);

        Assert.True(result.IsSuccess);
        Assert.Equal("""{"id":"base:item/beans","mass":"1 kg","volume":"1 l","maxStack":12}""", JsonOf(result, "base:item/beans"));
        Assert.Equal(["a"], result.Registry.OfKind("item").Single().ModifiedBy);
    }

    [Fact]
    public void Load_PatchNullRemovesAField()
    {
        var result = ModLoader.Load([Mod("base", files: Item("base:item/beans")), Mod("a", extra: Deps("base"), files: Patch("base:item/beans", "\"maxStack\": null"))]);

        Assert.DoesNotContain("maxStack", JsonOf(result, "base:item/beans"), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_PatchesApplyInLoadOrder()
    {
        var result = ModLoader.Load(
        [
            Mod("c", extra: Deps("b"), files: Patch("base:item/beans", "\"maxStack\": 20")),
            Mod("b", extra: Deps("base"), files: Patch("base:item/beans", "\"maxStack\": 12")),
            Mod("base", files: Item("base:item/beans")),
        ]);

        Assert.True(result.IsSuccess);
        Assert.Contains("\"maxStack\":20", JsonOf(result, "base:item/beans"), StringComparison.Ordinal);
        Assert.Equal(["b", "c"], result.Registry.OfKind("item").Single().ModifiedBy);
    }

    [Fact]
    public void Load_ModCanPatchItsOwnDefinitionEvenWhenThePatchFileSortsFirst()
    {
        var result = ModLoader.Load([Mod("m", files: [Patch("m:item/x", "\"maxStack\": 9", name: "a_patch"), Item("m:item/x")])]);

        Assert.True(result.IsSuccess);
        Assert.Contains("\"maxStack\":9", JsonOf(result, "m:item/x"), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_PatchOfAMissingDefinitionFails()
    {
        AssertFails(ModLoader.Load([Mod("base"), Mod("a", extra: Deps("base"), files: Patch("base:item/ghost", "\"maxStack\": 1"))]), ModLoadErrorKind.PatchTargetMissing);
    }

    [Fact]
    public void Load_PatchOfAModTheCallerDoesNotDependOnFails()
    {
        var result = ModLoader.Load([Mod("base", files: Item("base:item/beans")), Mod("a", files: Patch("base:item/beans", "\"maxStack\": 1"))]);

        AssertFails(result, ModLoadErrorKind.UndeclaredDependency);
    }

    [Fact]
    public void Load_PatchMayNotChangeTheId()
    {
        var changed = Patch("base:item/beans", "\"id\": \"base:item/other\"");
        var removed = Patch("base:item/beans", "\"id\": null");

        AssertFails(ModLoader.Load([Mod("base", files: Item("base:item/beans")), Mod("a", extra: Deps("base"), files: changed)]), ModLoadErrorKind.PatchChangesId);
        AssertFails(ModLoader.Load([Mod("base", files: Item("base:item/beans")), Mod("a", extra: Deps("base"), files: removed)]), ModLoadErrorKind.PatchChangesId);
    }

    [Fact]
    public void Load_BadDefinitionFilesFailWithPreciseErrors()
    {
        AssertFails(ModLoader.Load([Mod("a", files: ("data/x.json", "{ not json"))]), ModLoadErrorKind.InvalidJson);
        AssertFails(ModLoader.Load([Mod("a", files: ("data/x.json", "[1, 2]"))]), ModLoadErrorKind.InvalidDefinition);
        AssertFails(ModLoader.Load([Mod("a", files: ("data/x.json", """{ "mass": "1 kg" }"""))]), ModLoadErrorKind.InvalidDefinition);
        AssertFails(ModLoader.Load([Mod("a", files: ("data/x.json", """{ "id": "Not An Id" }"""))]), ModLoadErrorKind.InvalidDefinition);
        AssertFails(ModLoader.Load([Mod("a", files: ("data/x.json", """{ "patch": 5 }"""))]), ModLoadErrorKind.InvalidDefinition);
    }

    [Fact]
    public void Load_ReportsEveryProblemAndLoadsNothing()
    {
        var result = ModLoader.Load(
        [
            Mod("a", files: [("data/1.json", "{ bad"), Item("b:item/x")]),
            Mod("b", extra: Deps("a"), files: Patch("a:item/ghost", "\"maxStack\": 1")),
        ]);

        Assert.False(result.IsSuccess);
        Assert.Equal(3, result.Errors.Count);
        Assert.Equal(0, result.Registry.Count);
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.ToString())));
    }

    [Fact]
    public void ContentId_SplitsNamespacePathAndKind()
    {
        Assert.True(ContentId.TryParse("my_mod:item/food/beans", out var id));

        Assert.Equal("my_mod", id.Namespace);
        Assert.Equal("item/food/beans", id.Path);
        Assert.Equal("item", id.Kind);
        Assert.False(ContentId.TryParse("nonamespace", out _));
        Assert.False(ContentId.TryParse("a:Upper", out _));
        Assert.False(ContentId.TryParse(null, out _));
    }
}
