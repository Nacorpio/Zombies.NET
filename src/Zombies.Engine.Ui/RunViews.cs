using System.Globalization;
using Zombies.Domain.Statistics;

namespace Zombies.Engine.Ui;

/// <summary>One score on the end-of-run screen: what was counted and how much of it.</summary>
public sealed record RunScoreLine(string Label, string Value);

/// <summary>
/// What the end-of-run screen shows once a life is over: the scores of the run and the Conducts the player kept, as localized
/// text, so the drawing code only has to place it.
/// </summary>
public sealed class RunSummaryModel(RunSummary run, Localizer localizer)
{
    public string Title => localizer.Get("run.title");

    public string ScoresHeading => localizer.Get("run.scores");

    public string ConductsHeading => localizer.Get("run.conducts");

    /// <summary>Shown in place of the Conducts when the player kept none.</summary>
    public string NoConducts => localizer.Get("run.no_conducts");

    public IReadOnlyList<RunScoreLine> Scores =>
        [.. run.Scores.Select(s => new RunScoreLine(localizer.Get(ContentKey("statistic", s.Statistic)), s.Value.ToString("0.##", CultureInfo.InvariantCulture)))];

    public IReadOnlyList<string> ConductsKept =>
        [.. run.ConductsKept.Select(c => localizer.Get(ContentKey("conduct", c)))];

    internal static string ContentKey(string kind, string id) => $"{kind}.{id.Replace(':', '.').Replace('/', '.')}";
}

/// <summary>An Achievement toast: its name and what it asked of the player.</summary>
public sealed record AchievementToast(string Title, string Description);

/// <summary>
/// The toasts announcing completed Achievements. They show one at a time, each for <see cref="Duration"/>, so a burst of
/// Achievements is read in turn instead of piling up.
/// </summary>
public sealed class AchievementToasts(Localizer localizer)
{
    /// <summary>How long each toast stays up.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    private readonly Queue<string> _waiting = new();
    private TimeSpan _shown;

    /// <summary>The toast on screen, or null when none is.</summary>
    public AchievementToast? Current { get; private set; }

    /// <summary>Announces the Achievement with this Content ID. It shows now when nothing else is up, and otherwise after the toasts before it.</summary>
    public void Show(string achievement)
    {
        ArgumentNullException.ThrowIfNull(achievement);
        _waiting.Enqueue(achievement);
        if (Current is null)
        {
            Next();
        }
    }

    /// <summary>Lets time pass, ending the toast that has been up long enough and showing the next.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (Current is null)
        {
            return;
        }

        _shown += elapsed;
        if (_shown >= Duration)
        {
            Next();
        }
    }

    private void Next()
    {
        _shown = TimeSpan.Zero;
        if (!_waiting.TryDequeue(out var achievement))
        {
            Current = null;
            return;
        }

        var key = RunSummaryModel.ContentKey("achievement", achievement);
        Current = new AchievementToast(
            localizer.Format("achievement.toast", localizer.Get(key)),
            localizer.Get($"{key}.description"));
    }
}
