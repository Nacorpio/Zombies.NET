using System.Text.Json.Nodes;

namespace Zombies.ContentJudge.SystemOne;

/// <summary>A typed answer, returned under the id of the question it answers.</summary>
public abstract record Answer
{
    public abstract string Type { get; }

    /// <summary>How certain the model is, for choice and score answers; noul answers carry none.</summary>
    public virtual double? Confidence => null;

    public abstract JsonObject ToJson();

    /// <exception cref="FormatException">The JSON is not a noul, choice, or score answer.</exception>
    public static Answer FromJson(JsonNode? node)
    {
        if (node is not JsonObject json)
        {
            throw new FormatException("An answer must be a JSON object.");
        }

        return Wire.RequiredString(json, "type") switch
        {
            Question.NoulType => new NoulAnswer(Wire.RequiredNumber(json, "noul")),
            Question.ChoiceType => new ChoiceAnswer(Wire.RequiredString(json, "choice"), Wire.Probabilities(json), Wire.RequiredNumber(json, "confidence")),
            Question.ScoreType => new ScoreAnswer(
                Wire.RequiredNumber(json, "score"),
                json["legend"] is JsonObject legend ? legend.ToDictionary(l => l.Key, l => l.Value?.DeepClone(), StringComparer.Ordinal) : [],
                Wire.Probabilities(json),
                Wire.RequiredNumber(json, "confidence")),
            var type => throw new FormatException($"Unknown answer type '{type}'."),
        };
    }

    protected static JsonObject ProbabilitiesToJson(IReadOnlyDictionary<string, double> probabilities)
    {
        var json = new JsonObject();
        foreach (var (key, value) in probabilities)
        {
            json[key] = value;
        }

        return json;
    }
}

/// <summary>The probability, from 0 to 1, that the answer to a noul question is yes.</summary>
public sealed record NoulAnswer(double Noul) : Answer
{
    public override string Type => Question.NoulType;

    public override JsonObject ToJson() => new() { ["type"] = Type, ["noul"] = Noul };
}

/// <summary>The highest-probability option, the probability of every option, and confidence.</summary>
public sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double ConfidenceValue) : Answer
{
    public override string Type => Question.ChoiceType;

    public override double? Confidence => ConfidenceValue;

    public override JsonObject ToJson() => new()
    {
        ["type"] = Type,
        ["choice"] = Choice,
        ["probabilities"] = ProbabilitiesToJson(Probabilities),
        ["confidence"] = ConfidenceValue,
    };
}

/// <summary>A probability-weighted level (it can land between levels), each level's description and probability, and confidence.</summary>
public sealed record ScoreAnswer(double Score, IReadOnlyDictionary<string, JsonNode?> Legend, IReadOnlyDictionary<string, double> Probabilities, double ConfidenceValue) : Answer
{
    public override string Type => Question.ScoreType;

    public override double? Confidence => ConfidenceValue;

    public override JsonObject ToJson()
    {
        var legend = new JsonObject();
        foreach (var (level, description) in Legend)
        {
            legend[level] = description?.DeepClone();
        }

        return new JsonObject
        {
            ["type"] = Type,
            ["score"] = Score,
            ["legend"] = legend,
            ["probabilities"] = ProbabilitiesToJson(Probabilities),
            ["confidence"] = ConfidenceValue,
        };
    }
}

public sealed record Usage(int InputTokens, int OutputTokens);

/// <summary>A System One response: the model that answered, one answer per question id, and token usage.</summary>
public sealed record SystemOneResponse(string Model, IReadOnlyDictionary<string, Answer> Answers, Usage? Usage)
{
    public JsonObject ToJson()
    {
        var answers = new JsonObject();
        foreach (var (id, answer) in Answers)
        {
            answers[id] = answer.ToJson();
        }

        var json = new JsonObject { ["model"] = Model, ["answers"] = answers };
        if (Usage is not null)
        {
            json["usage"] = new JsonObject { ["input_tokens"] = Usage.InputTokens, ["output_tokens"] = Usage.OutputTokens };
        }

        return json;
    }

    public string ToJsonString(bool indented = false) => indented ? ToJson().ToJsonString(Wire.Indented) : ToJson().ToJsonString(Wire.Compact);

    /// <summary>
    /// Parses a response body. A Cloudflare Workers AI envelope (<c>{ "result": …, "success": …, "errors": … }</c>) is
    /// unwrapped first, so the same parser serves Jev and Clef Flash.
    /// </summary>
    /// <exception cref="FormatException">The body is not a System One response, or the envelope reports failure.</exception>
    public static SystemOneResponse Parse(string body)
    {
        var root = Wire.ParseNode(body, "response") as JsonObject ?? throw new FormatException("The response must be a JSON object.");
        if (root["answers"] is null && root.ContainsKey("success"))
        {
            if (root["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok)
            {
                throw new FormatException($"The Workers AI envelope reports failure: {root["errors"]?.ToJsonString() ?? "no errors given"}.");
            }

            root = root["result"] as JsonObject ?? throw new FormatException("The Workers AI envelope has no 'result' object.");
        }

        if (root["answers"] is not JsonObject answers)
        {
            throw new FormatException("The response has no 'answers' object.");
        }

        var parsed = new Dictionary<string, Answer>(StringComparer.Ordinal);
        foreach (var (id, answer) in answers)
        {
            try
            {
                parsed[id] = Answer.FromJson(answer);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Answer '{id}': {ex.Message}", ex);
            }
        }

        Usage? usage = null;
        if (root["usage"] is JsonObject u)
        {
            usage = new Usage((int)Wire.RequiredNumber(u, "input_tokens"), (int)Wire.RequiredNumber(u, "output_tokens"));
        }

        var model = root["model"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : string.Empty;
        return new SystemOneResponse(model, parsed, usage);
    }

    /// <summary>Problems that make this response unusable for <paramref name="request"/>: a missing answer, or one whose type does not match its question.</summary>
    public IReadOnlyList<string> Mismatches(SystemOneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        foreach (var question in request.Questions)
        {
            if (!Answers.TryGetValue(question.Id, out var answer))
            {
                problems.Add($"No answer for question '{question.Id}'.");
            }
            else if (!string.Equals(answer.Type, question.Question.Type, StringComparison.Ordinal))
            {
                problems.Add($"Question '{question.Id}' is a {question.Question.Type} but the answer is a {answer.Type}.");
            }
        }

        return problems;
    }
}
