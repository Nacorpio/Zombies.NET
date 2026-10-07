using Zombies.ContentJudge.Judging;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Tests;

public sealed class CacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "contentjudge-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SystemOneRequest Request(string model = "jev-latest", string state = "state", string instructions = "Is it?", byte[]? image = null) =>
        new(model, state, [new NamedQuestion("q", new NoulQuestion(instructions))]) { Images = image is null ? [] : [JudgeImage.FromBytes(image)] };

    [Fact]
    public void Key_IsStableForTheSameRequest() =>
        Assert.Equal(ResponseCache.Key(Request()), ResponseCache.Key(Request()));

    [Fact]
    public void Key_ChangesWithModelStateQuestionsAndImageBytes()
    {
        var baseline = ResponseCache.Key(Request());
        var png = Fakes.Png();
        var otherPng = Fakes.Png();
        otherPng[^1] = 1;

        string[] keys =
        [
            baseline,
            ResponseCache.Key(Request(model: "clef-flash")),
            ResponseCache.Key(Request(state: "other")),
            ResponseCache.Key(Request(instructions: "Is it not?")),
            ResponseCache.Key(Request(image: png)),
            ResponseCache.Key(Request(image: otherPng)),
        ];

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Matches("^[0-9a-f]{64}$", baseline);
    }

    [Fact]
    public void PutThenGet_ReturnsTheResponse()
    {
        var cache = new ResponseCache(_directory);
        var response = SystemOneResponse.Parse(Samples.ScoreResponse);

        Assert.Null(cache.TryGet("missing"));
        cache.Put("k", response);

        Assert.Equal(response.ToJsonString(), cache.TryGet("k")!.ToJsonString());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void CorruptFile_IsAMiss()
    {
        var cache = new ResponseCache(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(cache.PathFor("k"), "{ not json");

        Assert.Null(cache.TryGet("k"));
    }

    [Fact]
    public async Task Runner_SecondRunIsServedFromTheCache()
    {
        var client = new FakeClient(_ => SystemOneResponse.Parse(Fakes.NoulResponse(DefinitionJudge.PlausibleQuestion, 0.9)));
        var thresholds = Thresholds.Parse("""{ "questions": { "definition.plausible": { "reviewBelow": 0.5 } } }""");
        var runner = new JudgeRunner(client, Fakes.Models, thresholds, new ResponseCache(_directory));

        var first = await runner.RunAsync([new DefinitionJudge()], [Fakes.Item()], TestContext.Current.CancellationToken);
        var second = await runner.RunAsync([new DefinitionJudge()], [Fakes.Item()], TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);
        Assert.False(Assert.Single(first).Cached);
        Assert.True(Assert.Single(second).Cached);
        Assert.Equal(Verdict.Pass, second[0].Verdict);
    }

    [Fact]
    public async Task Runner_ChangedDefinitionIsAskedAgain()
    {
        var client = new FakeClient(_ => SystemOneResponse.Parse(Fakes.NoulResponse(DefinitionJudge.PlausibleQuestion, 0.9)));
        var runner = new JudgeRunner(client, Fakes.Models, Thresholds.Empty, new ResponseCache(_directory));

        await runner.RunAsync([new DefinitionJudge()], [Fakes.Item()], TestContext.Current.CancellationToken);
        await runner.RunAsync([new DefinitionJudge()], [Fakes.Item("base:item/other_beans")], TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Calls);
    }
}
