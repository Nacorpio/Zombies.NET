using System.Text.Json.Nodes;
using Zombies.Domain.Mods;

namespace Zombies.Domain.Tests;

public sealed class JsonMergePatchTests
{
    private static string Merge(string target, string patch) =>
        JsonMergePatch.Apply(JsonNode.Parse(target), JsonNode.Parse(patch))!.ToJsonString();

    [Theory]
    [InlineData("""{"a":"b"}""", """{"a":"c"}""", """{"a":"c"}""")]
    [InlineData("""{"a":"b"}""", """{"b":"c"}""", """{"a":"b","b":"c"}""")]
    [InlineData("""{"a":"b"}""", """{"a":null}""", "{}")]
    [InlineData("""{"a":"b","b":"c"}""", """{"a":null}""", """{"b":"c"}""")]
    [InlineData("""{"a":["b"]}""", """{"a":"c"}""", """{"a":"c"}""")]
    [InlineData("""{"a":"c"}""", """{"a":["b"]}""", """{"a":["b"]}""")]
    [InlineData("""{"a":{"b":"c"}}""", """{"a":{"b":"d","c":null}}""", """{"a":{"b":"d"}}""")]
    [InlineData("""{"a":[{"b":"c"}]}""", """{"a":[1]}""", """{"a":[1]}""")]
    [InlineData("""["a","b"]""", """["c","d"]""", """["c","d"]""")]
    [InlineData("""{"a":"b"}""", "[\"c\"]", "[\"c\"]")]
    [InlineData("""{"e":null}""", """{"a":1}""", """{"e":null,"a":1}""")]
    [InlineData("[1,2]", """{"a":"b","c":null}""", """{"a":"b"}""")]
    [InlineData("{}", """{"a":{"bb":{"ccc":null}}}""", """{"a":{"bb":{}}}""")]
    public void Apply_FollowsRfc7386(string target, string patch, string expected)
    {
        Assert.Equal(expected, Merge(target, patch));
    }

    [Fact]
    public void Apply_DoesNotModifyItsArguments()
    {
        var target = JsonNode.Parse("""{"a":{"b":1}}""")!;
        var patch = JsonNode.Parse("""{"a":{"b":null,"c":2}}""")!;

        JsonMergePatch.Apply(target, patch);

        Assert.Equal("""{"a":{"b":1}}""", target.ToJsonString());
        Assert.Equal("""{"a":{"b":null,"c":2}}""", patch.ToJsonString());
    }
}
