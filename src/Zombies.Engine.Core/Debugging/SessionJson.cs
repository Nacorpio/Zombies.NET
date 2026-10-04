using System.Text.Json;

namespace Zombies.Engine.Core.Debugging;

/// <summary>
/// Reads a session description from JSON. Hand-written files are welcome: comments and trailing commas are allowed, names are not
/// case sensitive, and unknown fields are ignored so a driver can add its own notes.
/// </summary>
public static class SessionJson
{
    public const int MaxSteps = 100;
    public const int MaxFacts = 50;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
    };

    private sealed record SessionDto
    {
        public string? Title { get; init; }

        public string? Reason { get; init; }

        public string? StartedBy { get; init; }

        public double? DurationSeconds { get; init; }

        public double? Progress { get; init; }

        public DateTimeOffset? StartedAt { get; init; }

        public List<StepDto>? Steps { get; init; }

        public List<FactDto>? Facts { get; init; }

        public string? Outcome { get; init; }
    }

    private sealed record StepDto
    {
        public string? Title { get; init; }

        public string? State { get; init; }
    }

    private sealed record FactDto
    {
        public string? Label { get; init; }

        public string? Value { get; init; }
    }

    /// <summary>Parses a session. On failure, <paramref name="error"/> is a short sentence saying what is wrong.</summary>
    public static bool TryParse(string json, out DebugSession? session, out string error)
    {
        ArgumentNullException.ThrowIfNull(json);
        session = null;

        SessionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SessionDto>(json, Options);
        }
        catch (JsonException ex)
        {
            error = $"not valid JSON: {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            error = "the file must contain a JSON object";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            error = "\"title\" is required";
            return false;
        }

        if (dto.DurationSeconds is <= 0 or double.NaN)
        {
            error = "\"durationSeconds\" must be above zero";
            return false;
        }

        if (dto.Progress is < 0 or > 1 or double.NaN)
        {
            error = "\"progress\" must be between 0 and 1";
            return false;
        }

        var steps = new List<SessionStep>();
        foreach (var (step, index) in (dto.Steps ?? []).Select((s, i) => (s, i)))
        {
            if (string.IsNullOrWhiteSpace(step.Title))
            {
                error = $"step {index + 1} needs a \"title\"";
                return false;
            }

            if (!TryParseState(step.State, out var state))
            {
                error = $"step {index + 1} has state \"{step.State}\"; use pending, running, done, or failed";
                return false;
            }

            steps.Add(new SessionStep(step.Title.Trim(), state));
        }

        var facts = new List<SessionFact>();
        foreach (var (fact, index) in (dto.Facts ?? []).Select((f, i) => (f, i)))
        {
            if (string.IsNullOrWhiteSpace(fact.Label))
            {
                error = $"fact {index + 1} needs a \"label\"";
                return false;
            }

            facts.Add(new SessionFact(fact.Label.Trim(), fact.Value?.Trim() ?? string.Empty));
        }

        if (steps.Count > MaxSteps || facts.Count > MaxFacts)
        {
            error = $"too many entries: at most {MaxSteps} steps and {MaxFacts} facts";
            return false;
        }

        session = new DebugSession
        {
            Title = dto.Title.Trim(),
            Reason = dto.Reason?.Trim() ?? string.Empty,
            StartedBy = dto.StartedBy?.Trim() ?? string.Empty,
            DurationSeconds = dto.DurationSeconds,
            Progress = dto.Progress,
            StartedAt = dto.StartedAt,
            Steps = steps,
            Facts = facts,
            Outcome = dto.Outcome?.Trim() ?? string.Empty,
        };
        error = string.Empty;
        return true;
    }

    private static bool TryParseState(string? text, out StepState state)
    {
        state = StepState.Pending;
        return string.IsNullOrWhiteSpace(text) || (Enum.TryParse(text.Trim(), ignoreCase: true, out state) && Enum.IsDefined(state));
    }
}
