using System.Text.Json.Nodes;
using Zombies.ContentJudge.SystemOne;
using Zombies.Domain.Mods;

namespace Zombies.ContentJudge.Judging;

/// <summary>One thing a judge looks at: a loaded definition, with any images that go with it.</summary>
/// <param name="Id">The definition's Content ID, or another stable name for subjects that are not definitions.</param>
/// <param name="Kind">The definition kind, such as <c>item</c> or <c>loot</c>.</param>
/// <param name="Json">The final JSON after every mod was applied.</param>
public sealed record JudgeSubject(string Id, string Kind, JsonNode Json, string DefinedBy)
{
    public IReadOnlyList<JudgeImage> Images { get; init; } = [];

    public static JudgeSubject FromDefinition(Definition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new JudgeSubject(definition.Id.Value, definition.Id.Kind, JsonNode.Parse(definition.Json) ?? new JsonObject(), definition.DefinedBy);
    }
}

/// <summary>
/// A content judge, split so it unit-tests without a network: deterministic <see cref="Check"/>s, a pure
/// <see cref="BuildRequest"/>, and a pure <see cref="Evaluate"/>. The runner makes the HTTP call in between.
/// Only <see cref="Check"/> can produce <see cref="Verdict.Fail"/>; <see cref="Evaluate"/> builds model findings,
/// which are Pass or Review.
/// </summary>
public interface IJudge
{
    /// <summary>A short name used in reports and on the command line.</summary>
    string Name { get; }

    /// <summary>The <c>models.json</c> key of the model this judge asks, such as <c>jev</c> or <c>clef-flash</c>.</summary>
    string ModelName { get; }

    bool AppliesTo(JudgeSubject subject);

    /// <summary>Checks computed in code. Any <see cref="Verdict.Fail"/> here skips the model call.</summary>
    IReadOnlyList<Finding> Check(JudgeSubject subject);

    /// <summary>The request to send, for <paramref name="model"/> (the request body's <c>model</c> field). Pure.</summary>
    SystemOneRequest BuildRequest(JudgeSubject subject, string model);

    /// <summary>Turns the answers into findings using <paramref name="thresholds"/>. Pure.</summary>
    IReadOnlyList<Finding> Evaluate(JudgeSubject subject, SystemOneRequest request, SystemOneResponse response, Thresholds thresholds);
}
