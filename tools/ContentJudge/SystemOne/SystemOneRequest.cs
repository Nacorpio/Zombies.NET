using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zombies.ContentJudge.SystemOne;

/// <summary>
/// A System One request: <c>{ model, state, questions }</c>, plus <c>images</c> for Clef Flash. <see cref="State"/> is a
/// string, or structured data such as a definition's JSON. Questions keep their order on the wire.
/// </summary>
public sealed partial record SystemOneRequest(string Model, JsonNode State, IReadOnlyList<NamedQuestion> Questions)
{
    public const int MaxQuestions = 64;
    public const int MaxQuestionIdLength = 100;
    public const int MaxImages = 4;
    public const int MaxImageBytes = 4 * 1024 * 1024;
    public const int MaxTotalImageBytes = 8 * 1024 * 1024;
    public const long MaxImagePixels = 16_000_000;

    /// <summary>Embedded images, sent before the state. Only Clef Flash accepts them.</summary>
    public IReadOnlyList<JudgeImage> Images { get; init; } = [];

    public JsonObject ToJson()
    {
        var questions = new JsonObject();
        foreach (var question in Questions)
        {
            questions[question.Id] = question.Question.ToJson();
        }

        var json = new JsonObject
        {
            ["model"] = Model,
            ["state"] = State.DeepClone(),
            ["questions"] = questions,
        };
        if (Images.Count > 0)
        {
            json["images"] = new JsonArray([.. Images.Select(i => i.ToJson())]);
        }

        return json;
    }

    public string ToJsonString(bool indented = false) => indented ? ToJson().ToJsonString(Wire.Indented) : ToJson().ToJsonString(Wire.Compact);

    /// <exception cref="FormatException">The body is not a System One request.</exception>
    public static SystemOneRequest Parse(string body)
    {
        var root = Wire.ParseNode(body, "request") as JsonObject ?? throw new FormatException("The request must be a JSON object.");
        var state = root["state"]?.DeepClone() ?? throw new FormatException("The request needs a 'state'.");
        if (root["questions"] is not JsonObject questions)
        {
            throw new FormatException("The request needs a 'questions' object.");
        }

        var named = new List<NamedQuestion>();
        foreach (var (id, question) in questions)
        {
            try
            {
                named.Add(new NamedQuestion(id, Question.FromJson(question)));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Question '{id}': {ex.Message}", ex);
            }
        }

        var images = root["images"] switch
        {
            null => [],
            JsonArray array => array.Select(JudgeImage.FromJson).ToList(),
            _ => throw new FormatException("'images' must be an array."),
        };

        return new SystemOneRequest(Wire.RequiredString(root, "model"), state, named) { Images = images };
    }

    /// <summary>
    /// Everything that would make the API refuse this request: question ids (letters, digits, <c>_</c>, <c>.</c>,
    /// <c>-</c>, at most 100 characters, 1 to 64 per request), question shapes, and image limits (at most 4 PNG, JPEG,
    /// or WebP images of at most 4 MiB and 16 megapixels each, 8 MiB in all). Empty when the request is valid.
    /// </summary>
    /// <param name="imagesAllowed">False for a model that takes no images, such as Jev.</param>
    public IReadOnlyList<string> Validate(bool imagesAllowed = true)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Model))
        {
            problems.Add("The model is empty.");
        }

        if (Questions.Count is 0 or > MaxQuestions)
        {
            problems.Add($"A request needs 1 to {MaxQuestions} questions, not {Questions.Count}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, question) in Questions)
        {
            if (!QuestionIdPattern().IsMatch(id))
            {
                problems.Add($"Question id '{id}' must be 1 to {MaxQuestionIdLength} letters, digits, '_', '.', or '-'.");
            }

            if (!seen.Add(id))
            {
                problems.Add($"Question id '{id}' is used twice.");
            }

            problems.AddRange(ValidateQuestion(question).Select(p => $"Question '{id}': {p}"));
        }

        if (Images.Count > 0 && !imagesAllowed)
        {
            problems.Add($"Model '{Model}' does not accept images.");
        }

        if (Images.Count > MaxImages)
        {
            problems.Add($"A request holds at most {MaxImages} images, not {Images.Count}.");
        }

        long total = 0;
        for (var i = 0; i < Images.Count; i++)
        {
            var image = Images[i];
            total += image.Bytes.Length;
            if (!JudgeImage.SupportedTypes.Contains(image.ContentType, StringComparer.Ordinal))
            {
                problems.Add($"Image {i} is '{image.ContentType}'; only PNG, JPEG, and WebP are accepted.");
            }
            else if (!string.Equals(JudgeImage.DetectContentType(image.Bytes), image.ContentType, StringComparison.Ordinal))
            {
                problems.Add($"Image {i} is labelled '{image.ContentType}' but its bytes are not.");
            }

            if (image.Bytes.Length > MaxImageBytes)
            {
                problems.Add($"Image {i} is {image.Bytes.Length} bytes; the limit is {MaxImageBytes} (4 MiB).");
            }

            if (!JudgeImage.TryReadSize(image.Bytes, out var width, out var height))
            {
                problems.Add($"Image {i}: could not read its width and height.");
            }
            else if ((long)width * height > MaxImagePixels)
            {
                problems.Add($"Image {i} is {width}x{height}; the limit is {MaxImagePixels} pixels (16 megapixels).");
            }
        }

        if (total > MaxTotalImageBytes)
        {
            problems.Add($"Images total {total} bytes; the limit is {MaxTotalImageBytes} (8 MiB).");
        }

        return problems;
    }

    private static IEnumerable<string> ValidateQuestion(Question question)
    {
        if (question.Instructions is JsonValue value && (!value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
        {
            yield return "instructions must be a non-empty string, an object, or an array.";
        }

        switch (question)
        {
            case ChoiceQuestion choice:
                if (choice.Options.Count is < ChoiceQuestion.MinOptions or > ChoiceQuestion.MaxOptions)
                {
                    yield return $"a choice needs {ChoiceQuestion.MinOptions} to {ChoiceQuestion.MaxOptions} options, not {choice.Options.Count}.";
                }

                if (choice.Options.Any(o => string.IsNullOrEmpty(o.Key)))
                {
                    yield return "choice option ids cannot be empty.";
                }

                if (choice.Options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != choice.Options.Count)
                {
                    yield return "choice option ids must be unique.";
                }

                break;
            case ScoreQuestion score when score.Levels.Count is < ScoreQuestion.MinLevels or > ScoreQuestion.MaxLevels:
                yield return $"a score needs {ScoreQuestion.MinLevels} to {ScoreQuestion.MaxLevels} levels, not {score.Levels.Count}.";
                break;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex QuestionIdPattern();
}
