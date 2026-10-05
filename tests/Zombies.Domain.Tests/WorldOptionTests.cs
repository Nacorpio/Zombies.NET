using Zombies.Domain.Mods;

namespace Zombies.Domain.Tests;

public sealed class WorldOptionTests
{
    private const string Density = "test:world_option/density";
    private const string Hardcore = "test:world_option/hardcore";
    private const string Waves = "test:world_option/waves";
    private const string Tint = "test:world_option/tint";

    private static string Json(string id, string type, string? range, string defaultValue, string extra = "") =>
        $$"""{ "id": "{{id}}", "type": "{{type}}", {{(range is null ? "" : range + ",")}} "default": {{defaultValue}}, "description": "A test option."{{extra}} }""";

    private static readonly WorldOptionCatalog Catalog = new(
    [
        WorldOptionJson.Parse(Json(Density, "number", "\"min\": 0, \"max\": 4", "1")),
        WorldOptionJson.Parse(Json(Hardcore, "boolean", null, "0")),
        WorldOptionJson.Parse(Json(Waves, "integer", "\"min\": 1, \"max\": 10", "3")),
        WorldOptionJson.Parse(Json(Tint, "number", "\"min\": 0, \"max\": 1", "0.5", ", \"affectsSimulation\": false")),
    ]);

    [Fact]
    public void Parse_ReadsIdTypeRangeDefaultAndDescription()
    {
        var option = WorldOptionJson.Parse(Json(Density, "number", "\"min\": 0, \"max\": 4", "1"));

        Assert.Equal(Density, option.Id.Value);
        Assert.Equal(WorldOptionType.Number, option.Type);
        Assert.Equal((0, 4, 1), (option.Min, option.Max, option.Default));
        Assert.Equal("A test option.", option.Description);
        Assert.True(option.AffectsSimulation);
    }

    [Theory]
    [InlineData("test:item/not_an_option", "number", "\"min\": 0, \"max\": 4", "1")]
    [InlineData("test:world_option/a", "number", null, "1")]
    [InlineData("test:world_option/a", "number", "\"min\": 4, \"max\": 0", "1")]
    [InlineData("test:world_option/a", "number", "\"min\": 0, \"max\": 4", "9")]
    [InlineData("test:world_option/a", "integer", "\"min\": 0, \"max\": 4", "1.5")]
    [InlineData("test:world_option/a", "boolean", "\"min\": 0, \"max\": 1", "0")]
    [InlineData("test:world_option/a", "boolean", null, "2")]
    [InlineData("test:world_option/a", "text", null, "1")]
    public void Parse_RejectsAnInvalidDeclaration(string id, string type, string? range, string defaultValue) =>
        Assert.Throws<WorldOptionException>(() => WorldOptionJson.Parse(Json(id, type, range, defaultValue)));

    [Fact]
    public void Get_ReturnsTheDeclaredDefault_WhenNothingWasChosen()
    {
        var options = new WorldOptions(Catalog);

        Assert.Equal(1, options.Get(Density));
        Assert.Equal(0, options.Get(Hardcore));
        Assert.Equal(3, options.Get(Waves));
        Assert.Empty(options.Chosen);
    }

    [Fact]
    public void Get_ReturnsTheChosenValue_AndDefaultsForTheRest()
    {
        var options = new WorldOptions(Catalog, [KeyValuePair.Create(Density, 2.5), KeyValuePair.Create(Hardcore, 1.0)]);

        Assert.Equal(2.5, options.Get(Density));
        Assert.Equal(1, options.Get(Hardcore));
        Assert.Equal(3, options.Get(Waves));
        Assert.Equal([KeyValuePair.Create(Density, 2.5), KeyValuePair.Create(Hardcore, 1.0)], options.Chosen);
    }

    [Fact]
    public void Get_OfAnUndeclaredOption_Throws() =>
        Assert.Throws<WorldOptionException>(() => new WorldOptions(Catalog).Get("test:world_option/missing"));

    [Theory]
    [InlineData(Density, 5.0)]
    [InlineData(Density, -1.0)]
    [InlineData(Density, double.NaN)]
    [InlineData(Hardcore, 0.5)]
    [InlineData(Waves, 2.5)]
    [InlineData("test:world_option/missing", 1.0)]
    public void Choosing_AValueTheDeclarationForbids_IsRefused(string id, double value) =>
        Assert.Throws<WorldOptionException>(() => new WorldOptions(Catalog, [KeyValuePair.Create(id, value)]));

    [Fact]
    public void SimulationValues_ListOnlyOptionsThatChangeTheSimulation_WithDefaultsFilledIn()
    {
        var options = new WorldOptions(Catalog, [KeyValuePair.Create(Density, 2.0), KeyValuePair.Create(Tint, 0.9)]);

        Assert.Equal([KeyValuePair.Create(Density, 2.0), KeyValuePair.Create(Hardcore, 0.0), KeyValuePair.Create(Waves, 3.0)], options.SimulationValues);
    }
}
