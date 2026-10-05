using Zombies.Domain.Statistics;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class RunViewTests
{
    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "run.title": "End of run",
              "run.scores": "Scores",
              "run.conducts": "Conducts kept",
              "run.no_conducts": "No conducts kept",
              "achievement.toast": "Achievement completed: {0}",
              "statistic.test.statistic.days": "Days survived",
              "statistic.test.statistic.distance": "Distance",
              "conduct.test.conduct.pacifist": "Pacifist",
              "achievement.test.achievement.week": "A Week Alive",
              "achievement.test.achievement.week.description": "Survive seven days.",
              "achievement.test.achievement.slayer": "Slayer",
              "achievement.test.achievement.slayer.description": "Kill many." } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private static readonly RunSummary Run = new(
        [new StatisticValue("test:statistic/days", 7), new StatisticValue("test:statistic/distance", 1234.5)],
        ["test:conduct/pacifist"]);

    [Fact]
    public void TheEndOfRunScreen_ListsEachScoreWithItsLocalizedName()
    {
        var model = new RunSummaryModel(Run, English());

        Assert.Equal("End of run", model.Title);
        Assert.Equal("Scores", model.ScoresHeading);
        Assert.Equal([new RunScoreLine("Days survived", "7"), new RunScoreLine("Distance", "1234.5")], model.Scores);
    }

    [Fact]
    public void TheEndOfRunScreen_ListsTheConductsThePlayerKept()
    {
        var model = new RunSummaryModel(Run, English());

        Assert.Equal("Conducts kept", model.ConductsHeading);
        Assert.Equal(["Pacifist"], model.ConductsKept);
    }

    [Fact]
    public void TheEndOfRunScreen_HasWordsForARunThatKeptNoConduct()
    {
        var model = new RunSummaryModel(new RunSummary([], []), English());

        Assert.Empty(model.ConductsKept);
        Assert.Equal("No conducts kept", model.NoConducts);
    }

    [Fact]
    public void AToast_NamesTheAchievementAndWhatItAskedOfThePlayer()
    {
        var toasts = new AchievementToasts(English());

        toasts.Show("test:achievement/week");

        Assert.Equal(new AchievementToast("Achievement completed: A Week Alive", "Survive seven days."), toasts.Current);
    }

    [Fact]
    public void NoToastIsShown_UntilAnAchievementIsCompleted()
    {
        var toasts = new AchievementToasts(English());

        toasts.Advance(TimeSpan.FromSeconds(10));

        Assert.Null(toasts.Current);
    }

    [Fact]
    public void AToast_StaysUpForItsDuration_ThenGoesAway()
    {
        var toasts = new AchievementToasts(English());
        toasts.Show("test:achievement/week");

        toasts.Advance(AchievementToasts.Duration - TimeSpan.FromMilliseconds(1));
        Assert.NotNull(toasts.Current);

        toasts.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(toasts.Current);
    }

    [Fact]
    public void ToastsCompletedTogether_ShowOneAfterTheOther()
    {
        var toasts = new AchievementToasts(English());
        toasts.Show("test:achievement/week");
        toasts.Show("test:achievement/slayer");

        Assert.Equal("Achievement completed: A Week Alive", toasts.Current?.Title);
        toasts.Advance(AchievementToasts.Duration);
        Assert.Equal("Achievement completed: Slayer", toasts.Current?.Title);
        toasts.Advance(AchievementToasts.Duration);
        Assert.Null(toasts.Current);
    }
}
