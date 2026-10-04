namespace Zombies.Engine.Core.Debugging;

public enum StepState
{
    Pending,
    Running,
    Done,
    Failed,
}

public sealed record SessionStep(string Title, StepState State = StepState.Pending);

public sealed record SessionFact(string Label, string Value);

/// <summary>Which step a session is on, counted from one, out of how many.</summary>
public sealed record StepSummary(int Current, int Total);

/// <summary>
/// A run of the game started to inspect, test, or demonstrate something, described as data so the screen can say what is going on and why.
/// Only <see cref="Title"/> is required; everything else is shown only when it is present.
/// </summary>
public sealed record DebugSession
{
    public required string Title { get; init; }

    /// <summary>Why the session was started. Shown when the badge is expanded.</summary>
    public string Reason { get; init; } = string.Empty;

    public string StartedBy { get; init; } = string.Empty;

    /// <summary>How long the session is meant to run. When set, the badge shows progress through it.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Progress from 0 to 1 stated by whoever drives the session. Wins over progress worked out from the duration.</summary>
    public double? Progress { get; init; }

    /// <summary>When the session began, on the wall clock. If absent, the first time the game saw it counts.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public IReadOnlyList<SessionStep> Steps { get; init; } = [];

    public IReadOnlyList<SessionFact> Facts { get; init; } = [];

    /// <summary>How the session ended, such as "passed" or "3 of 5 checks failed". Shown once present.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Progress from 0 to 1 after <paramref name="elapsedSeconds"/>, or null when the session has neither a duration nor a stated progress.</summary>
    public double? ProgressAt(double elapsedSeconds)
    {
        if (Progress is { } stated)
        {
            return Math.Clamp(stated, 0, 1);
        }

        return DurationSeconds is { } duration && duration > 0 ? Math.Clamp(elapsedSeconds / duration, 0, 1) : null;
    }

    /// <summary>The step in progress: the first running one, else the first still pending, else the last. Null without steps.</summary>
    public StepSummary? Summary
    {
        get
        {
            if (Steps.Count == 0)
            {
                return null;
            }

            return new StepSummary(CurrentStepIndex + 1, Steps.Count);
        }
    }

    /// <summary>Index of the step to highlight. Zero when there are no steps.</summary>
    public int CurrentStepIndex
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].State == StepState.Running)
                {
                    return i;
                }
            }

            for (var i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].State is StepState.Pending or StepState.Failed)
                {
                    return i;
                }
            }

            return Math.Max(0, Steps.Count - 1);
        }
    }

    /// <summary>Changes whenever something a watcher would care about changes: the title, any step state, or the outcome.</summary>
    public string Signature => string.Concat(Title, "|", string.Join(',', Steps.Select(s => (int)s.State)), "|", Outcome);
}
