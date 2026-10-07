using Zombies.ContentJudge.Judging;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Tests;

[Collection("Console")]
public sealed class FailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "contentjudge-fail-" + Guid.NewGuid().ToString("N"));

    public FailureTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<(int Code, string Error)> Capture(Func<Task<int>> action)
    {
        var original = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            return (await action(), error.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public async Task DryRun_MissingModsFolder_ExitsWithUsageErrorAndOneLine()
    {
        var missing = Path.Combine(_root, "no_such_mods");
        var (code, error) = await Capture(() => Task.FromResult(Cli.DryRun([missing])));

        Assert.Equal(JudgeExitCode.UsageError, code);
        Assert.Equal(64, code);
        var line = Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(missing, line);
        Assert.DoesNotContain("   at ", line);
    }

    [Fact]
    public async Task Run_MissingModsFolder_ExitsWithUsageError()
    {
        var missing = Path.Combine(_root, "no_such_mods");
        var (code, error) = await Capture(() => Cli.RunAsync([missing, "--out", Path.Combine(_root, "out")]));

        Assert.Equal(JudgeExitCode.UsageError, code);
        Assert.Contains(missing, error);
    }

    [Fact]
    public async Task Runner_UnwritableCache_StillCompletesWarnsOnceAndKeepsResults()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file, not a directory");
        var cache = new ResponseCache(Path.Combine(blocker, "cache"));
        var client = new FakeClient(_ => SystemOneResponse.Parse(Fakes.NoulResponse(DefinitionJudge.PlausibleQuestion, 0.9)));
        var runner = new JudgeRunner(client, Fakes.Models, Thresholds.Parse("""{ "questions": { "definition.plausible": { "reviewBelow": 0.5 } } }"""), cache);

        var (_, error) = await Capture(async () =>
        {
            var results = await runner.RunAsync([new DefinitionJudge()], [Fakes.Item(), Fakes.Item("base:item/other_beans")], TestContext.Current.CancellationToken);
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.False(r.IsError));
            Assert.Equal(JudgeExitCode.Pass, JudgeExitCode.For(results));
            var (markdown, json) = ReportWriter.Write(Path.Combine(_root, "reports"), results);
            Assert.True(File.Exists(markdown));
            Assert.True(File.Exists(json));
            return 0;
        });

        Assert.Equal(2, client.Calls);
        var warning = Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("cache", warning, StringComparison.OrdinalIgnoreCase);
    }
}
