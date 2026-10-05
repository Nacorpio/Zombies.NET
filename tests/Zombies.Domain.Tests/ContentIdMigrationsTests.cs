using Zombies.Domain.Mods;

namespace Zombies.Domain.Tests;

public sealed class ContentIdMigrationsTests
{
    private static (string Path, string Json) Migration(string name, string body) =>
        ($"data/{name}.json", $$"""{ "id": "a:migration/{{name}}", {{body}} }""");

    private static (string Path, string Json) Rename(string name, string from, string to) =>
        Migration(name, $"\"from\": \"{from}\", \"to\": \"{to}\"");

    private static ModLoadResult Load(params (string Path, string Json)[] files) =>
        ModLoader.Load([new ModPackage("a", """{ "id": "a", "version": "1.0.0" }""", [.. files.Select(f => new ModFile(f.Path, f.Json))])]);

    private static void AssertFails(ModLoadResult result, ModLoadErrorKind kind)
    {
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Kind == kind);
        Assert.Equal(0, result.Migrations.Count);
    }

    [Fact]
    public void Resolve_RenamedId_ReturnsTheNewId()
    {
        var result = Load(Rename("knife", "a:item/old_knife", "a:item/knife"));

        Assert.True(result.IsSuccess);
        Assert.Equal("a:item/knife", result.Migrations.Resolve("a:item/old_knife"));
    }

    [Fact]
    public void Resolve_RemovedId_ReturnsNull()
    {
        var result = Load(Migration("gone", "\"from\": \"a:item/gone\", \"removed\": true"));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Migrations.Resolve("a:item/gone"));
    }

    [Fact]
    public void Resolve_IdWithNoMigration_ReturnsItUnchanged()
    {
        var result = Load(Rename("knife", "a:item/old_knife", "a:item/knife"));

        Assert.Equal("a:item/axe", result.Migrations.Resolve("a:item/axe"));
    }

    [Fact]
    public void Resolve_ChainOfRenames_ReturnsTheLastId()
    {
        var result = Load(Rename("one", "a:item/x", "a:item/y"), Rename("two", "a:item/y", "a:item/z"));

        Assert.True(result.IsSuccess);
        Assert.Equal("a:item/z", result.Migrations.Resolve("a:item/x"));
        Assert.Equal("a:item/z", result.Migrations.Resolve("a:item/y"));
    }

    [Fact]
    public void Resolve_ChainThatEndsInARemoval_ReturnsNull()
    {
        var result = Load(Rename("one", "a:item/x", "a:item/y"), Migration("two", "\"from\": \"a:item/y\", \"removed\": true"));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Migrations.Resolve("a:item/x"));
    }

    [Fact]
    public void Load_MigrationsThatFormACycle_AreRejectedOnce()
    {
        var result = Load(Rename("one", "a:item/x", "a:item/y"), Rename("two", "a:item/y", "a:item/z"), Rename("three", "a:item/z", "a:item/x"));

        AssertFails(result, ModLoadErrorKind.MigrationCycle);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Load_MigrationToItself_IsRejected()
    {
        AssertFails(Load(Rename("self", "a:item/x", "a:item/x")), ModLoadErrorKind.MigrationCycle);
    }

    [Fact]
    public void Load_ChainThatLeadsIntoACycle_IsRejected()
    {
        var result = Load(Rename("in", "a:item/start", "a:item/x"), Rename("one", "a:item/x", "a:item/y"), Rename("two", "a:item/y", "a:item/x"));

        AssertFails(result, ModLoadErrorKind.MigrationCycle);
    }

    [Fact]
    public void Load_TwoMigrationsFromTheSameId_AreRejected()
    {
        AssertFails(Load(Rename("one", "a:item/x", "a:item/y"), Rename("two", "a:item/x", "a:item/z")), ModLoadErrorKind.DuplicateMigration);
    }

    [Theory]
    [InlineData("\"from\": \"a:item/x\"")]
    [InlineData("\"from\": \"a:item/x\", \"to\": \"a:item/y\", \"removed\": true")]
    [InlineData("\"from\": \"not an id\", \"removed\": true")]
    [InlineData("\"from\": \"a:item/x\", \"to\": \"not an id\"")]
    [InlineData("\"to\": \"a:item/y\"")]
    public void Load_InvalidMigration_IsRejected(string body)
    {
        AssertFails(Load(Migration("bad", body)), ModLoadErrorKind.InvalidDefinition);
    }
}
