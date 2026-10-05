using System.Text.Json.Nodes;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Judging;

/// <summary>
/// The baseline judge every definition goes through, so <c>dry-run</c> and the pipeline have something to carry
/// before the specific text and icon judges exist. It checks in code that the definition's <c>id</c> matches its
/// Content ID, and asks Jev one noul: are the values plausible for what the Content ID names.
/// </summary>
public sealed class DefinitionJudge : IJudge
{
    public const string PlausibleQuestion = "definition.plausible";

    public string Name => "definition";

    public string ModelName => "jev";

    public bool AppliesTo(JudgeSubject subject) => true;

    public IReadOnlyList<Finding> Check(JudgeSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Json is not JsonObject json)
        {
            return [Finding.Deterministic(Verdict.Fail, "the definition is not a JSON object")];
        }

        var id = json["id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        return string.Equals(id, subject.Id, StringComparison.Ordinal)
            ? []
            : [Finding.Deterministic(Verdict.Fail, $"the definition's id '{id}' does not match its Content ID '{subject.Id}'")];
    }

    public SystemOneRequest BuildRequest(JudgeSubject subject, string model)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var state = new JsonObject
        {
            ["content_id"] = subject.Id,
            ["kind"] = subject.Kind,
            ["definition"] = subject.Json.DeepClone(),
        };
        return new SystemOneRequest(model, state,
        [
            new NamedQuestion(PlausibleQuestion, new NoulQuestion(
                "Are the values in `definition` plausible for the thing its `content_id` names, in a realistic zombie survival game?",
                "Every value is believable for that thing",
                "At least one value is clearly wrong for that thing, such as an impossible mass, size, or count")),
        ]);
    }

    public IReadOnlyList<Finding> Evaluate(JudgeSubject subject, SystemOneRequest request, SystemOneResponse response, Thresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        return thresholds.EvaluateAll(request, response);
    }
}
