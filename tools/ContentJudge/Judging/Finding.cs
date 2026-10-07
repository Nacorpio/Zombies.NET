namespace Zombies.ContentJudge.Judging;

/// <summary>The outcome of judging one subject. Only a deterministic check can produce <see cref="Fail"/>.</summary>
public enum Verdict
{
    Pass,
    Review,
    Fail,
}

/// <summary>What a model answer can lead to. There is no Fail: a model can only flag content for a person to review.</summary>
public enum ModelVerdict
{
    Pass,
    Review,
}

public enum FindingSource
{
    /// <summary>A check computed in code from the content itself.</summary>
    Deterministic,

    /// <summary>A model answer compared against a threshold.</summary>
    Model,
}

/// <summary>
/// One observation about a subject. Built only through <see cref="Deterministic"/> and <see cref="FromModel"/>, so a
/// model finding can never carry <see cref="Verdict.Fail"/>.
/// </summary>
public sealed record Finding
{
    private Finding(Verdict verdict, FindingSource source, string? questionId, string message)
    {
        Verdict = verdict;
        Source = source;
        QuestionId = questionId;
        Message = message;
    }

    public Verdict Verdict { get; }

    public FindingSource Source { get; }

    /// <summary>The question a model finding answers; null for a deterministic finding.</summary>
    public string? QuestionId { get; }

    public string Message { get; }

    public static Finding Deterministic(Verdict verdict, string message) => new(verdict, FindingSource.Deterministic, null, message);

    public static Finding FromModel(ModelVerdict verdict, string questionId, string message) =>
        new(verdict == ModelVerdict.Pass ? Verdict.Pass : Verdict.Review, FindingSource.Model, questionId, message);

    /// <summary>The worst verdict of <paramref name="findings"/>: Fail over Review over Pass. Pass when there are none.</summary>
    public static Verdict Worst(IEnumerable<Finding> findings) =>
        findings.Select(f => f.Verdict).DefaultIfEmpty(Verdict.Pass).Max();
}
