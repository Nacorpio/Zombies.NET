using Zombies.ContentJudge.Judging;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Tests;

public sealed class EvaluationTests
{
    private static readonly Thresholds Thresholds = Thresholds.Parse("""
        {
          // comments are allowed
          "questions": {
            "noul_low": { "reviewBelow": 0.5 },
            "noul_high": { "reviewAbove": 0.3 },
            "score": { "reviewAbove": 1.5, "minConfidence": 0.6 },
            "choice": { "reviewChoices": ["wrong_area"], "minConfidence": 0.5 }
          }
        }
        """);

    private static ChoiceAnswer Choice(string choice, double confidence) =>
        new(choice, new Dictionary<string, double> { [choice] = 1.0 }, confidence);

    private static ScoreAnswer Score(double score, double confidence) =>
        new(score, new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(), new Dictionary<string, double>(), confidence);

    [Theory]
    [InlineData("noul_low", 0.9, Verdict.Pass)]
    [InlineData("noul_low", 0.2, Verdict.Review)]
    [InlineData("noul_high", 0.2, Verdict.Pass)]
    [InlineData("noul_high", 0.9, Verdict.Review)]
    public void Noul_ComparedWithItsThreshold(string id, double noul, Verdict expected) =>
        Assert.Equal(expected, Thresholds.Evaluate(id, new NoulAnswer(noul)).Verdict);

    [Fact]
    public void Score_AboveThresholdOrUnsureIsReviewed()
    {
        Assert.Equal(Verdict.Pass, Thresholds.Evaluate("score", Score(1.0, 0.9)).Verdict);
        Assert.Equal(Verdict.Review, Thresholds.Evaluate("score", Score(2.0, 0.9)).Verdict);
        Assert.Equal(Verdict.Review, Thresholds.Evaluate("score", Score(1.0, 0.4)).Verdict);
    }

    [Fact]
    public void Choice_FlaggedOptionOrLowConfidenceIsReviewed()
    {
        Assert.Equal(Verdict.Pass, Thresholds.Evaluate("choice", Choice("kitchen", 0.9)).Verdict);
        Assert.Equal(Verdict.Review, Thresholds.Evaluate("choice", Choice("wrong_area", 0.9)).Verdict);
        Assert.Equal(Verdict.Review, Thresholds.Evaluate("choice", Choice("kitchen", 0.2)).Verdict);
    }

    [Fact]
    public void QuestionWithoutThreshold_IsReviewedNeverPassed() =>
        Assert.Equal(Verdict.Review, Thresholds.Evaluate("unknown", new NoulAnswer(1.0)).Verdict);

    [Fact]
    public void ModelFindings_NeverFail()
    {
        foreach (var verdict in Enum.GetValues<ModelVerdict>())
        {
            Assert.NotEqual(Verdict.Fail, Finding.FromModel(verdict, "q", "m").Verdict);
        }

        Assert.Equal(Verdict.Fail, Finding.Deterministic(Verdict.Fail, "m").Verdict);
    }

    [Fact]
    public void Worst_FailOverReviewOverPass()
    {
        Assert.Equal(Verdict.Pass, Finding.Worst([]));
        Assert.Equal(Verdict.Review, Finding.Worst([Finding.FromModel(ModelVerdict.Pass, "a", ""), Finding.FromModel(ModelVerdict.Review, "b", "")]));
        Assert.Equal(Verdict.Fail, Finding.Worst([Finding.FromModel(ModelVerdict.Review, "b", ""), Finding.Deterministic(Verdict.Fail, "")]));
    }

    [Theory]
    [InlineData("""{ "questions": { "q": { "reviewOver": 1 } } }""")]
    [InlineData("""{ "question": {} }""")]
    [InlineData("not json")]
    public void ThresholdsFile_TyposAreRejected(string json) =>
        Assert.Throws<FormatException>(() => Thresholds.Parse(json));

    [Fact]
    public void CheckedInThresholds_CoverEveryCatalogQuestion()
    {
        var thresholds = Thresholds.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "thresholds.json")));
        var request = new DefinitionJudge().BuildRequest(Fakes.Item(), "jev-latest");

        Assert.All(request.Questions, q => Assert.True(thresholds.TryGet(q.Id, out _), q.Id));
    }

    [Fact]
    public void DefinitionJudge_CheckFailsWhenIdDoesNotMatch()
    {
        var judge = new DefinitionJudge();

        Assert.Empty(judge.Check(Fakes.Item()));
        var finding = Assert.Single(judge.Check(Fakes.Item(jsonId: "base:item/other")));
        Assert.Equal(Verdict.Fail, finding.Verdict);
        Assert.Equal(FindingSource.Deterministic, finding.Source);
    }

    [Fact]
    public void DefinitionJudge_BuildsAValidDeterministicRequest()
    {
        var judge = new DefinitionJudge();
        var first = judge.BuildRequest(Fakes.Item(), "jev-latest");

        Assert.Empty(first.Validate(imagesAllowed: false));
        Assert.Equal(first.ToJsonString(), judge.BuildRequest(Fakes.Item(), "jev-latest").ToJsonString());
        Assert.Equal("jev-latest", first.Model);
        Assert.Equal("base:item/canned_beans", first.State["content_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0.9, Verdict.Pass)]
    [InlineData(0.1, Verdict.Review)]
    public void DefinitionJudge_EvaluatesAgainstThresholds(double noul, Verdict expected)
    {
        var judge = new DefinitionJudge();
        var request = judge.BuildRequest(Fakes.Item(), "jev-latest");
        var response = SystemOneResponse.Parse(Fakes.NoulResponse(DefinitionJudge.PlausibleQuestion, noul));
        var thresholds = Thresholds.Parse("""{ "questions": { "definition.plausible": { "reviewBelow": 0.5 } } }""");

        var finding = Assert.Single(judge.Evaluate(Fakes.Item(), request, response, thresholds));

        Assert.Equal(expected, finding.Verdict);
        Assert.Equal(FindingSource.Model, finding.Source);
    }

    [Fact]
    public void ExitCode_ErrorThenFailThenReviewThenPass()
    {
        JudgeResult Result(Verdict verdict, string? error = null) => new("j", "s", "jev", verdict, [], error);

        Assert.Equal(0, JudgeExitCode.For([]));
        Assert.Equal(0, JudgeExitCode.For([Result(Verdict.Pass)]));
        Assert.Equal(2, JudgeExitCode.For([Result(Verdict.Pass), Result(Verdict.Review)]));
        Assert.Equal(1, JudgeExitCode.For([Result(Verdict.Review), Result(Verdict.Fail)]));
        Assert.Equal(3, JudgeExitCode.For([Result(Verdict.Fail), Result(Verdict.Pass, "timeout")]));
    }

    [Fact]
    public void Report_WritesMarkdownAndJson()
    {
        IReadOnlyList<JudgeResult> results =
        [
            new("definition", "base:item/a", "jev", Verdict.Pass, [Finding.FromModel(ModelVerdict.Pass, "q", "noul 0.9")]),
            new("definition", "base:item/b", "jev", Verdict.Review, [Finding.FromModel(ModelVerdict.Review, "q", "noul 0.1 | low")]),
            new("definition", "base:item/c", "jev", Verdict.Fail, [Finding.Deterministic(Verdict.Fail, "id mismatch")]),
            new("definition", "base:item/d", "jev", Verdict.Pass, [], "HTTP 500"),
        ];
        var directory = Path.Combine(Path.GetTempPath(), "contentjudge-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (markdown, json) = ReportWriter.Write(directory, results);

            var text = File.ReadAllText(markdown);
            Assert.Contains("Exit code 3", text, StringComparison.Ordinal);
            Assert.Contains("| Fail | definition | `base:item/c` | id mismatch |", text, StringComparison.Ordinal);
            Assert.Contains("noul 0.1 \\| low", text, StringComparison.Ordinal);
            Assert.DoesNotContain("base:item/a", text, StringComparison.Ordinal);

            var report = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(json))!;
            Assert.Equal(3, report["exitCode"]!.GetValue<int>());
            Assert.Equal(1, report["summary"]!["error"]!.GetValue<int>());
            Assert.Equal(4, report["results"]!.AsArray().Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
