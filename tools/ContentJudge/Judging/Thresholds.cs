using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Judging;

/// <summary>
/// When one question's answer needs a person to look. The value compared is the noul probability or the score;
/// a choice is flagged when it is one of <see cref="ReviewChoices"/>. Any answer whose confidence is below
/// <see cref="MinConfidence"/> is flagged too.
/// </summary>
public sealed record QuestionThreshold
{
    public double? ReviewAbove { get; init; }

    public double? ReviewBelow { get; init; }

    public IReadOnlyList<string> ReviewChoices { get; init; } = [];

    public double? MinConfidence { get; init; }
}

/// <summary>Per-question thresholds, read from <c>tools/ContentJudge/thresholds.json</c> and keyed by question id.</summary>
public sealed class Thresholds
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed record ThresholdFile(Dictionary<string, QuestionThreshold> Questions);

    private readonly IReadOnlyDictionary<string, QuestionThreshold> _questions;

    public Thresholds(IReadOnlyDictionary<string, QuestionThreshold> questions) => _questions = questions;

    public static Thresholds Empty { get; } = new(new Dictionary<string, QuestionThreshold>());

    public IReadOnlyCollection<string> QuestionIds => [.. _questions.Keys];

    /// <summary>Reads <c>{ "questions": { "&lt;id&gt;": { "reviewAbove", "reviewBelow", "reviewChoices", "minConfidence" } } }</c>.</summary>
    /// <exception cref="FormatException">The JSON is not a thresholds file.</exception>
    public static Thresholds Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<ThresholdFile>(json, Options) ?? throw new FormatException("thresholds.json is JSON null.");
            return new Thresholds(file.Questions ?? throw new FormatException("thresholds.json needs a 'questions' object."));
        }
        catch (JsonException ex)
        {
            throw new FormatException($"thresholds.json is not valid: {ex.Message}", ex);
        }
    }

    public bool TryGet(string questionId, out QuestionThreshold threshold) => _questions.TryGetValue(questionId, out threshold!);

    /// <summary>
    /// Compares one answer with its threshold. Pure. A question with no threshold is sent to review, so an
    /// uncalibrated question never passes silently.
    /// </summary>
    public Finding Evaluate(string questionId, Answer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (!TryGet(questionId, out var threshold))
        {
            return Finding.FromModel(ModelVerdict.Review, questionId, $"no threshold configured; answer {Describe(answer)}");
        }

        var reasons = new List<string>();
        if (threshold.MinConfidence is { } min && answer.Confidence is { } confidence && confidence < min)
        {
            reasons.Add($"confidence {F(confidence)} < {F(min)}");
        }

        double? value = answer switch
        {
            NoulAnswer noul => noul.Noul,
            ScoreAnswer score => score.Score,
            _ => null,
        };
        if (value is { } v && threshold.ReviewAbove is { } above && v > above)
        {
            reasons.Add($"{F(v)} > {F(above)}");
        }

        if (value is { } w && threshold.ReviewBelow is { } below && w < below)
        {
            reasons.Add($"{F(w)} < {F(below)}");
        }

        if (answer is ChoiceAnswer choice && threshold.ReviewChoices.Contains(choice.Choice, StringComparer.Ordinal))
        {
            reasons.Add($"choice '{choice.Choice}' is flagged for review");
        }

        return reasons.Count == 0
            ? Finding.FromModel(ModelVerdict.Pass, questionId, Describe(answer))
            : Finding.FromModel(ModelVerdict.Review, questionId, $"{Describe(answer)}: {string.Join("; ", reasons)}");
    }

    /// <summary>Evaluates every question of <paramref name="request"/> against its answer in <paramref name="response"/>.</summary>
    public IReadOnlyList<Finding> EvaluateAll(SystemOneRequest request, SystemOneResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        return
        [
            .. request.Questions.Select(q => response.Answers.TryGetValue(q.Id, out var answer)
                ? Evaluate(q.Id, answer)
                : Finding.FromModel(ModelVerdict.Review, q.Id, "no answer returned")),
        ];
    }

    private static string Describe(Answer answer) => answer switch
    {
        NoulAnswer noul => $"noul {F(noul.Noul)}",
        ChoiceAnswer choice => $"choice '{choice.Choice}' (confidence {F(choice.ConfidenceValue)})",
        ScoreAnswer score => $"score {F(score.Score)} (confidence {F(score.ConfidenceValue)})",
        _ => answer.Type,
    };

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
