using System.Text.Json.Nodes;

namespace Zombies.ContentJudge.SystemOne;

/// <summary>
/// A typed System One question. <see cref="Instructions"/> is a string, or an object or array that holds the question
/// in one field and data it refers to in others, as the System One API allows.
/// </summary>
public abstract record Question(JsonNode Instructions)
{
    public const string NoulType = "noul";
    public const string ChoiceType = "choice";
    public const string ScoreType = "score";

    /// <summary>The wire <c>type</c>: <c>noul</c>, <c>choice</c>, or <c>score</c>.</summary>
    public abstract string Type { get; }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["type"] = Type,
            ["instructions"] = Instructions.DeepClone(),
        };
        var criteria = CriteriaToJson();
        if (criteria is not null)
        {
            json["criteria"] = criteria;
        }

        return json;
    }

    protected abstract JsonNode? CriteriaToJson();

    /// <exception cref="FormatException">The JSON is not a noul, choice, or score question.</exception>
    public static Question FromJson(JsonNode? node)
    {
        if (node is not JsonObject json)
        {
            throw new FormatException("A question must be a JSON object.");
        }

        var type = Wire.RequiredString(json, "type");
        var instructions = json["instructions"]?.DeepClone() ?? throw new FormatException("A question needs 'instructions'.");
        var criteria = json["criteria"];
        return type switch
        {
            NoulType => NoulQuestion.FromCriteria(instructions, criteria),
            ChoiceType => criteria is JsonObject options
                ? new ChoiceQuestion(instructions, [.. options.Select(o => new KeyValuePair<string, JsonNode?>(o.Key, o.Value?.DeepClone()))])
                : throw new FormatException("A choice question needs a 'criteria' object of options."),
            ScoreType => criteria is JsonArray levels
                ? new ScoreQuestion(instructions, [.. levels.Select(l => l?.DeepClone() ?? throw new FormatException("A score level cannot be null."))])
                : throw new FormatException("A score question needs a 'criteria' array of levels."),
            _ => throw new FormatException($"Unknown question type '{type}'."),
        };
    }
}

/// <summary>A yes/no question. The answer is the probability that the answer is yes.</summary>
/// <param name="WhenTrue">Optional description of what a yes means.</param>
/// <param name="WhenFalse">Optional description of what a no means.</param>
public sealed record NoulQuestion(JsonNode Instructions, JsonNode? WhenTrue = null, JsonNode? WhenFalse = null) : Question(Instructions)
{
    public override string Type => NoulType;

    protected override JsonNode? CriteriaToJson()
    {
        if (WhenTrue is null && WhenFalse is null)
        {
            return null;
        }

        var criteria = new JsonObject();
        if (WhenTrue is not null)
        {
            criteria["true"] = WhenTrue.DeepClone();
        }

        if (WhenFalse is not null)
        {
            criteria["false"] = WhenFalse.DeepClone();
        }

        return criteria;
    }

    internal static NoulQuestion FromCriteria(JsonNode instructions, JsonNode? criteria) => criteria switch
    {
        null => new NoulQuestion(instructions),
        JsonObject o => new NoulQuestion(instructions, o["true"]?.DeepClone(), o["false"]?.DeepClone()),
        _ => throw new FormatException("Noul 'criteria' must be an object with 'true' and 'false'."),
    };
}

/// <summary>Pick one option from a set. <see cref="Options"/> maps option id to its description (null when none is needed), in order.</summary>
public sealed record ChoiceQuestion(JsonNode Instructions, IReadOnlyList<KeyValuePair<string, JsonNode?>> Options) : Question(Instructions)
{
    public const int MinOptions = 2;
    public const int MaxOptions = 255;

    public override string Type => ChoiceType;

    protected override JsonNode CriteriaToJson()
    {
        var criteria = new JsonObject();
        foreach (var (option, description) in Options)
        {
            criteria[option] = description?.DeepClone();
        }

        return criteria;
    }
}

/// <summary>Rate the state on an ordered rubric. <see cref="Levels"/> are lowest first and indexed from 0.</summary>
public sealed record ScoreQuestion(JsonNode Instructions, IReadOnlyList<JsonNode> Levels) : Question(Instructions)
{
    public const int MinLevels = 2;
    public const int MaxLevels = 10;

    public override string Type => ScoreType;

    protected override JsonNode CriteriaToJson() => new JsonArray([.. Levels.Select(l => l.DeepClone())]);
}

/// <summary>A question under the id the caller chose. The answer comes back under the same id.</summary>
public sealed record NamedQuestion(string Id, Question Question);
