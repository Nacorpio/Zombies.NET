using System.Text.Json.Nodes;
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

    private static (string Path, string Json) Table(string id, string body) =>
        ($"data/{id.Replace(':', '_').Replace('/', '_')}.json", $$"""{ "id": "{{id}}", {{body}} }""");

    private const string KitchenBody = """
        "rolls": { "min": 1, "max": 3 },
        "stack": 10,
        "ratio": 1.5,
        "entries": [ { "item": "base:item/beans", "weight": 10 }, { "item": "base:item/water", "weight": 5 } ]
        """;

    private static JsonNode Parsed(ModLoadResult result, string id) => JsonNode.Parse(JsonOf(result, id))!;

    private static ModLoadResult LoadWithPatch(string body) =>
        ModLoader.Load([Mod("base", files: Table("base:loot/kitchen", KitchenBody)), Mod("a", extra: Deps("base"), files: Patch("base:loot/kitchen", body))]);

    [Fact]
    public void Load_CopyFromInheritsEverythingAndOverridesOnlyTheFieldsGiven()
    {
        var result = ModLoader.Load([Mod("base", files: Table("base:loot/kitchen", KitchenBody)), Mod("a", extra: Deps("base"),
            files: Table("a:loot/pantry", """ "copy-from": "base:loot/kitchen", "rolls": { "max": 5 } """))]);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var pantry = Parsed(result, "a:loot/pantry");
        Assert.Equal("a:loot/pantry", (string?)pantry["id"]);
        Assert.Equal(1, (int?)pantry["rolls"]!["min"]);
        Assert.Equal(5, (int?)pantry["rolls"]!["max"]);
        Assert.Equal(2, pantry["entries"]!.AsArray().Count);
        Assert.Null(pantry["copy-from"]);
        Assert.Equal(3, (int?)Parsed(result, "base:loot/kitchen")["rolls"]!["max"]);
    }

    [Fact]
    public void Load_CopyFromWorksBetweenAModsOwnDefinitionsInAnyFileOrder()
    {
        var result = ModLoader.Load([Mod("m", files:
        [
            ("data/a.json", """{ "id": "m:loot/a", "copy-from": "m:loot/b", "stack": 2 }"""),
            ("data/b.json", """{ "id": "m:loot/b", "copy-from": "m:loot/c", "ratio": 2.5 }"""),
            ("data/c.json", """{ "id": "m:loot/c", "stack": 1, "ratio": 1.0 }"""),
        ])]);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var a = Parsed(result, "m:loot/a");
        Assert.Equal(2, (int?)a["stack"]);
        Assert.Equal(2.5, (double?)a["ratio"]);
    }

    [Fact]
    public void Load_OverrideMayCopyFromAndEditAnotherDefinition()
    {
        var result = ModLoader.Load([Mod("base", files: [Table("base:loot/kitchen", KitchenBody), Table("base:loot/garage", """ "stack": 99 """)]), Mod("a", extra: Deps("base"),
            files: ("data/o.json", """{ "id": "base:loot/garage", "override": true, "copy-from": "base:loot/kitchen", "relative": { "stack": 1 } }"""))]);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(11, (int?)Parsed(result, "base:loot/garage")["stack"]);
        Assert.Equal(["a"], result.Registry.Definitions.Single(d => d.Id.Value == "base:loot/garage").ModifiedBy);
    }

    [Fact]
    public void Load_CopyFromCycleFailsNamingTheModAndContentIds()
    {
        var result = ModLoader.Load([Mod("m", files:
        [
            ("data/a.json", """{ "id": "m:loot/a", "copy-from": "m:loot/b" }"""),
            ("data/b.json", """{ "id": "m:loot/b", "copy-from": "m:loot/a" }"""),
        ])]);

        AssertFails(result, ModLoadErrorKind.CopyFromCycle);
        var error = Assert.Single(result.Errors);
        Assert.Equal("m", error.ModId);
        Assert.Contains("m:loot/a", error.Message);
        Assert.Contains("m:loot/b", error.Message);
    }

    [Fact]
    public void Load_CopyFromItselfIsACycle()
    {
        AssertFails(ModLoader.Load([Mod("m", files: ("data/a.json", """{ "id": "m:loot/a", "copy-from": "m:loot/a" }"""))]), ModLoadErrorKind.CopyFromCycle);
    }

    [Fact]
    public void Load_CopyFromAMissingParentFailsNamingTheModAndContentId()
    {
        var result = ModLoader.Load([Mod("m", files: ("data/a.json", """{ "id": "m:loot/a", "copy-from": "m:loot/ghost" }"""))]);

        AssertFails(result, ModLoadErrorKind.CopyFromMissing);
        var error = Assert.Single(result.Errors);
        Assert.Equal("m", error.ModId);
        Assert.Contains("m:loot/a", error.Message);
        Assert.Contains("m:loot/ghost", error.Message);
    }

    [Fact]
    public void Load_CopyFromRejectsAModTheCallerDoesNotDependOnAndAMismatchedKind()
    {
        var parent = Mod("base", files: [Table("base:loot/kitchen", KitchenBody), Item("base:item/beans")]);

        AssertFails(ModLoader.Load([parent, Mod("a", files: Table("a:loot/x", """ "copy-from": "base:loot/kitchen" """))]), ModLoadErrorKind.UndeclaredDependency);
        AssertFails(ModLoader.Load([parent, Mod("a", extra: Deps("base"), files: Table("a:loot/x", """ "copy-from": "base:item/beans" """))]), ModLoadErrorKind.InvalidDefinition);
        AssertFails(ModLoader.Load([Mod("a", files: Table("a:loot/x", """ "copy-from": 5 """))]), ModLoadErrorKind.InvalidDefinition);
        AssertFails(LoadWithPatch(""" "copy-from": "base:loot/kitchen" """), ModLoadErrorKind.InvalidDefinition);
    }

    [Fact]
    public void Load_ExtendAppendsToAnArrayAndCreatesOneThatIsMissing()
    {
        var result = LoadWithPatch(""" "extend": { "entries": [ { "item": "base:item/rope", "weight": 1 } ], "tags": [ "kitchen" ] } """);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var kitchen = Parsed(result, "base:loot/kitchen");
        Assert.Equal(["base:item/beans", "base:item/water", "base:item/rope"], kitchen["entries"]!.AsArray().Select(e => (string?)e!["item"]));
        Assert.Equal("kitchen", (string?)kitchen["tags"]![0]);
        Assert.Equal(["a"], result.Registry.Definitions.Single(d => d.Id.Value == "base:loot/kitchen").ModifiedBy);
    }

    [Fact]
    public void Load_DeleteRemovesEntriesMatchingAWholeValueOrASubsetOfMembers()
    {
        var result = LoadWithPatch(""" "extend": { "tags": [ "a", "b", "c" ] }, "delete": { "tags": [ "b" ], "entries": [ { "item": "base:item/water" } ] } """);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var kitchen = Parsed(result, "base:loot/kitchen");
        Assert.Equal(["a", "c"], kitchen["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal("base:item/beans", (string?)Assert.Single(kitchen["entries"]!.AsArray())!["item"]);
    }

    [Fact]
    public void Load_RelativeAddsToANumberKeepingWholeNumbersWhole()
    {
        var result = LoadWithPatch(""" "relative": { "stack": -4, "ratio": 0.25, "rolls": { "max": 2 } } """);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var kitchen = Parsed(result, "base:loot/kitchen");
        Assert.Equal("6", kitchen["stack"]!.ToJsonString());
        Assert.Equal(1.75, (double?)kitchen["ratio"]);
        Assert.Equal(5, (int?)kitchen["rolls"]!["max"]);
    }

    [Fact]
    public void Load_ProportionalScalesANumberRoundingWholeNumbers()
    {
        var result = LoadWithPatch(""" "proportional": { "stack": 1.25, "ratio": 2, "rolls": { "max": 0.5 } } """);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var kitchen = Parsed(result, "base:loot/kitchen");
        Assert.Equal("13", kitchen["stack"]!.ToJsonString());
        Assert.Equal(3.0, (double?)kitchen["ratio"]);
        Assert.Equal("2", kitchen["rolls"]!["max"]!.ToJsonString());
    }

    [Fact]
    public void Load_OperatorsRunAfterTheMergedFieldsOfTheSamePatch()
    {
        var result = LoadWithPatch(""" "stack": 100, "relative": { "stack": 5 } """);

        Assert.Equal(105, (int?)Parsed(result, "base:loot/kitchen")["stack"]);
    }

    [Fact]
    public void Load_OperatorsApplyToACopiedDefinition()
    {
        var result = ModLoader.Load([Mod("base", files: Table("base:loot/kitchen", KitchenBody)), Mod("a", extra: Deps("base"),
            files: Table("a:loot/pantry", """ "copy-from": "base:loot/kitchen", "proportional": { "stack": 2 }, "extend": { "entries": [ { "item": "a:item/jam", "weight": 3 } ] } """))]);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var pantry = Parsed(result, "a:loot/pantry");
        Assert.Equal(20, (int?)pantry["stack"]);
        Assert.Equal(3, pantry["entries"]!.AsArray().Count);
        Assert.Equal(2, Parsed(result, "base:loot/kitchen")["entries"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(""" "extend": 5 """)]
    [InlineData(""" "extend": { "stack": [ 1 ] } """)]
    [InlineData(""" "extend": { "entries": "x" } """)]
    [InlineData(""" "delete": { "entries": 1 } """)]
    [InlineData(""" "delete": { "tags": [ "x" ] } """)]
    [InlineData(""" "relative": { "ghost": 1 } """)]
    [InlineData(""" "relative": { "stack": "1" } """)]
    [InlineData(""" "relative": { "entries": 1 } """)]
    [InlineData(""" "proportional": { "stack": [ 2 ] } """)]
    [InlineData(""" "proportional": { "stack": { "x": 2 } } """)]
    public void Load_AnOperatorThatDoesNotFitTheDefinitionFailsNamingTheModAndContentId(string body)
    {
        var result = LoadWithPatch(body);

        AssertFails(result, ModLoadErrorKind.InvalidOperator);
        var error = Assert.Single(result.Errors);
        Assert.Equal("a", error.ModId);
        Assert.Contains("base:loot/kitchen", error.Message);
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
