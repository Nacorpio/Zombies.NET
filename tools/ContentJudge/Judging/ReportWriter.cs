using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Judging;

/// <summary>Writes a run's results as <c>content-judge.md</c> for people and <c>content-judge.json</c> for tools.</summary>
public static class ReportWriter
{
    public const string MarkdownFileName = "content-judge.md";
    public const string JsonFileName = "content-judge.json";

    /// <returns>The paths written: Markdown first, then JSON.</returns>
    public static (string Markdown, string Json) Write(string directory, IReadOnlyList<JudgeResult> results)
    {
        Directory.CreateDirectory(directory);
        var markdown = Path.Combine(directory, MarkdownFileName);
        var json = Path.Combine(directory, JsonFileName);
        File.WriteAllText(markdown, ToMarkdown(results));
        File.WriteAllText(json, ToJson(results));
        return (markdown, json);
    }

    public static string ToJson(IReadOnlyList<JudgeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var report = new JsonObject
        {
            ["exitCode"] = JudgeExitCode.For(results),
            ["summary"] = new JsonObject
            {
                ["pass"] = results.Count(r => !r.IsError && r.Verdict == Verdict.Pass),
                ["review"] = results.Count(r => !r.IsError && r.Verdict == Verdict.Review),
                ["fail"] = results.Count(r => !r.IsError && r.Verdict == Verdict.Fail),
                ["error"] = results.Count(r => r.IsError),
            },
            ["results"] = new JsonArray([.. results.Select(ResultToJson)]),
        };
        return report.ToJsonString(Wire.Indented) + "\n";
    }

    public static string ToMarkdown(IReadOnlyList<JudgeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var text = new StringBuilder();
        text.Append("# Content judge report\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Exit code {JudgeExitCode.For(results)}. ");
        text.Append(CultureInfo.InvariantCulture, $"{results.Count(r => r.IsError)} error, ");
        text.Append(CultureInfo.InvariantCulture, $"{results.Count(r => !r.IsError && r.Verdict == Verdict.Fail)} fail, ");
        text.Append(CultureInfo.InvariantCulture, $"{results.Count(r => !r.IsError && r.Verdict == Verdict.Review)} review, ");
        text.Append(CultureInfo.InvariantCulture, $"{results.Count(r => !r.IsError && r.Verdict == Verdict.Pass)} pass.\n");

        var attention = results.Where(r => r.IsError || r.Verdict != Verdict.Pass)
            .OrderByDescending(r => r.IsError)
            .ThenByDescending(r => r.Verdict)
            .ThenBy(r => r.Subject, StringComparer.Ordinal)
            .ToList();
        if (attention.Count == 0)
        {
            text.Append("\nNothing needs attention.\n");
            return text.ToString();
        }

        text.Append("\n| Status | Judge | Subject | Finding |\n|---|---|---|---|\n");
        foreach (var result in attention)
        {
            var status = result.IsError ? "Error" : result.Verdict.ToString();
            var details = result.IsError
                ? [result.Error!]
                : result.Findings.Where(f => f.Verdict != Verdict.Pass).Select(f => f.QuestionId is null ? f.Message : $"`{f.QuestionId}`: {f.Message}").ToList();
            text.Append(CultureInfo.InvariantCulture, $"| {status} | {result.Judge} | `{result.Subject}` | {Cell(string.Join("<br>", details))} |\n");
        }

        return text.ToString();
    }

    private static JsonObject ResultToJson(JudgeResult result) => new()
    {
        ["judge"] = result.Judge,
        ["subject"] = result.Subject,
        ["model"] = result.Model,
        ["verdict"] = result.IsError ? "Error" : result.Verdict.ToString(),
        ["cached"] = result.Cached,
        ["error"] = result.Error,
        ["findings"] = new JsonArray([.. result.Findings.Select(f => (JsonNode)new JsonObject
        {
            ["verdict"] = f.Verdict.ToString(),
            ["source"] = f.Source.ToString(),
            ["question"] = f.QuestionId,
            ["message"] = f.Message,
        })]),
    };

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
