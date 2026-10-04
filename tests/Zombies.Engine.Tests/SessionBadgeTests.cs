using Zombies.Engine.Core.Debugging;
using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

public sealed class SessionBadgeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("session-badge-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DebugSession Titled(string title = "Combat test") => new() { Title = title };

    private static DebugSession Stepped(params StepState[] states) => new()
    {
        Title = "Run",
        Steps = states.Select((s, i) => new SessionStep($"Step {i + 1}", s)).ToList(),
    };

    // --- data layer ---

    [Fact]
    public void Json_TitleOnly_IsEnough()
    {
        Assert.True(SessionJson.TryParse("""{"title":"Hi"}""", out var session, out _));
        Assert.Equal("Hi", session!.Title);
        Assert.Empty(session.Steps);
        Assert.Null(session.ProgressAt(5));
    }

    [Fact]
    public void Json_AcceptsCommentsTrailingCommasAndAnyCase()
    {
        const string json = """
            {
              // why
              "Title": "T", "steps": [ {"title": "a", "state": "DONE"}, ],
            }
            """;
        Assert.True(SessionJson.TryParse(json, out var session, out var error), error);
        Assert.Equal(StepState.Done, session!.Steps[0].State);
    }

    [Theory]
    [InlineData("""{"reason":"x"}""", "title")]
    [InlineData("""{"title":"a","durationSeconds":0}""", "durationSeconds")]
    [InlineData("""{"title":"a","progress":1.5}""", "progress")]
    [InlineData("""{"title":"a","steps":[{"state":"done"}]}""", "step 1")]
    [InlineData("""{"title":"a","steps":[{"title":"s","state":"half"}]}""", "half")]
    [InlineData("""{"title":"a","facts":[{"value":"v"}]}""", "fact 1")]
    [InlineData("{not json", "JSON")]
    public void Json_RejectsBadInputWithAReadableReason(string json, string mention)
    {
        Assert.False(SessionJson.TryParse(json, out var session, out var error));
        Assert.Null(session);
        Assert.Contains(mention, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_StatedValueWinsOverDuration()
    {
        var session = new DebugSession { Title = "t", DurationSeconds = 100, Progress = 0.9 };
        Assert.Equal(0.9, session.ProgressAt(10));
        Assert.Equal(0.25, (session with { Progress = null }).ProgressAt(25));
        Assert.Equal(1.0, (session with { Progress = null }).ProgressAt(500));
    }

    [Fact]
    public void CurrentStep_IsRunning_ElsePending_ElseLast()
    {
        Assert.Equal(1, Stepped(StepState.Done, StepState.Running, StepState.Pending).CurrentStepIndex);
        Assert.Equal(2, Stepped(StepState.Done, StepState.Done, StepState.Pending).CurrentStepIndex);
        Assert.Equal(1, Stepped(StepState.Done, StepState.Done).CurrentStepIndex);
    }

    [Fact]
    public void FileSource_ReportsMissingBadAndGoodFiles_KeepingTheLastGoodSession()
    {
        var path = Path.Combine(_dir, "s.json");
        var source = new FileSessionSource(path, pollSeconds: 0);

        Assert.Equal("SESSION FILE NOT FOUND", source.Poll(0).Warning);

        File.WriteAllText(path, """{"title":"One"}""");
        var good = source.Poll(1);
        Assert.Equal("One", good.Session!.Title);
        Assert.Null(good.Warning);

        File.WriteAllText(path, "{oops");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        var bad = source.Poll(2);
        Assert.Equal("One", bad.Session!.Title);
        Assert.StartsWith("SESSION FILE:", bad.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void FileSource_DoesNotRereadAnUnchangedFile_AndRespectsThePollInterval()
    {
        var path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, """{"title":"One"}""");
        var source = new FileSessionSource(path, pollSeconds: 1);

        source.Poll(0);
        source.Poll(0.5);
        source.Poll(1.5);
        source.Poll(3);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public void Elapsed_UsesStartedAtWhenGiven_ElseFirstSeen()
    {
        var now = DateTimeOffset.UtcNow;
        var stated = new SessionState(new DebugSession { Title = "t", StartedAt = now.AddSeconds(-30) }, null, 99);
        Assert.InRange(stated.ElapsedSeconds(100, now), 29.9, 30.1);

        var seen = new SessionState(Titled(), null, 10);
        Assert.Equal(5, seen.ElapsedSeconds(15, now));
    }

    // --- text ---

    [Fact]
    public void TextWrap_TruncatesWithDotsAndWrapsAtWords()
    {
        Assert.Equal("abcdefg...", TextWrap.Truncate("abcdefghijklmnop", 10));
        Assert.Equal("short", TextWrap.Truncate("short", 10));
        Assert.Equal(["aaa bbb", "ccc"], TextWrap.Wrap("aaa bbb ccc", 7, 5));
        Assert.Equal(["abcd", "efgh", "ij"], TextWrap.Wrap("abcdefghij", 4, 5));
        var capped = TextWrap.Wrap("one two three four five six", 7, 2);
        Assert.Equal(2, capped.Count);
        Assert.EndsWith("...", capped[1], StringComparison.Ordinal);
        Assert.All(capped, line => Assert.True(line.Length <= 7));
    }

    // --- layout ---

    [Fact]
    public void Layout_NothingToShow_ReturnsNull()
    {
        Assert.Null(SessionBadge.Layout(null, null, 0, false, 1280, 720));
    }

    [Fact]
    public void Compact_TitleOnly_IsIconAndTitle_NoBarNoCount()
    {
        var layout = SessionBadge.Layout(Titled(), null, 3, false, 1280, 720)!;
        Assert.Contains(IconNames.Robot, layout.IconNames);
        Assert.Contains("COMBAT TEST", layout.Texts);
        Assert.False(layout.Expanded);
        Assert.Equal(2, layout.Commands.OfType<BadgeRect>().Count());
    }

    [Fact]
    public void Compact_WithDuration_ShowsAProgressBarFilledToTheFraction()
    {
        var session = Titled() with { DurationSeconds = 100 };
        var layout = SessionBadge.Layout(session, null, 25, false, 1280, 720)!;
        var rects = layout.Commands.OfType<BadgeRect>().ToList();
        var track = rects[^2];
        var fill = rects[^1];
        Assert.Equal(track.Width * 0.25f, fill.Width, 0.5f);
    }

    [Fact]
    public void Compact_WithSteps_ShowsTheStepCount()
    {
        var layout = SessionBadge.Layout(Stepped(StepState.Done, StepState.Running, StepState.Pending), null, 0, false, 1280, 720)!;
        Assert.Contains("2/3", layout.Texts);
    }

    [Fact]
    public void Expanded_OmitsSectionsThatHaveNoData()
    {
        var layout = SessionBadge.Layout(Titled(), null, 3, true, 1280, 720)!;
        var texts = layout.Texts.ToList();
        Assert.DoesNotContain(texts, t => t.StartsWith("BY ", StringComparison.Ordinal));
        Assert.Contains("TIME 00:03", texts);
        Assert.DoesNotContain(IconNames.Check, layout.IconNames);
    }

    [Fact]
    public void Expanded_ShowsReasonByTimeStepsFactsOutcomeAndWarning()
    {
        var session = new DebugSession
        {
            Title = "Verify mesher",
            Reason = "Checking greedy merge after the padding change.",
            StartedBy = "claude",
            DurationSeconds = 60,
            Steps = [new("Build", StepState.Done), new("Run", StepState.Failed), new("Report", StepState.Pending)],
            Facts = [new("Seed", "42")],
            Outcome = "1 of 3 failed",
        };
        var layout = SessionBadge.Layout(session, "SESSION FILE: BAD", 12, true, 1280, 720)!;
        var texts = layout.Texts.ToList();
        Assert.Contains("BY CLAUDE", texts);
        Assert.Contains("TIME 00:12 OF 01:00", texts);
        Assert.Contains("1 OF 3 FAILED", texts);
        Assert.Contains("SESSION FILE: BAD", texts);
        Assert.Contains("SEED: ", texts);
        Assert.Contains("42", texts);
        Assert.Contains(IconNames.Check, layout.IconNames);
        Assert.Contains(IconNames.Cross, layout.IconNames);
        Assert.Contains(IconNames.Warning, layout.IconNames);
    }

    [Fact]
    public void Expanded_LongStepLists_ShowAWindowAroundTheCurrentStep()
    {
        var states = Enumerable.Range(0, 20).Select(i => i < 10 ? StepState.Done : i == 10 ? StepState.Running : StepState.Pending).ToArray();
        var texts = SessionBadge.Layout(Stepped(states), null, 0, true, 1280, 720)!.Texts.ToList();
        Assert.Contains("STEP 11", texts);
        Assert.DoesNotContain("STEP 1", texts);
        Assert.Contains(texts, t => t.EndsWith("EARLIER", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.EndsWith("MORE", StringComparison.Ordinal));
        Assert.Equal(SessionBadge.StepWindow, texts.Count(t => t.StartsWith("STEP ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(1280, 720, false)]
    [InlineData(1280, 720, true)]
    [InlineData(800, 600, true)]
    [InlineData(500, 300, true)]
    [InlineData(420, 200, false)]
    public void Layout_StaysOnScreen_AndClearOfTheOverlayWhereThereIsRoom(int w, int h, bool expanded)
    {
        var session = Stepped(StepState.Running, StepState.Pending) with
        {
            Title = "A very long session title that cannot possibly fit on a small screen",
            Reason = string.Join(' ', Enumerable.Repeat("because", 40)),
            Facts = Enumerable.Range(0, 20).Select(i => new SessionFact($"Fact {i}", new string('x', 80))).ToList(),
            DurationSeconds = 10,
        };
        var layout = SessionBadge.Layout(session, null, 1, expanded, w, h)!;
        Assert.True(layout.X >= 0, "left edge");
        Assert.True(layout.X + layout.Width <= w, "right edge");
        Assert.True(layout.Y >= 0 && layout.Y + layout.Height <= h, "bottom edge");
        foreach (var text in layout.Commands.OfType<BadgeText>())
        {
            Assert.True(text.X >= layout.X && text.X <= layout.X + layout.Width, $"text '{text.Text}' starts outside");
        }

        if (w - SessionBadge.OverlayReserve > 200)
        {
            Assert.True(layout.X >= SessionBadge.OverlayReserve - 1, "overlaps the frame-time overlay");
        }
    }

    [Fact]
    public void Compact_Badge_StaysSmall()
    {
        var layout = SessionBadge.Layout(Stepped(StepState.Running), null, 0, false, 1920, 1080)!;
        Assert.True(layout.Width < 1920 * 0.25f);
        Assert.True(layout.Height < 1080 * 0.06f);
    }

    [Fact]
    public void WarningAlone_ShowsASmallWarningPill()
    {
        var layout = SessionBadge.Layout(null, "SESSION FILE NOT FOUND", 0, true, 1280, 720)!;
        Assert.Contains(IconNames.Warning, layout.IconNames);
        Assert.DoesNotContain(IconNames.Robot, layout.IconNames);
        Assert.False(layout.Expanded);
    }

    [Fact]
    public void FormatTime_UsesMinutesAndHours()
    {
        Assert.Equal("00:00", SessionBadge.FormatTime(-3));
        Assert.Equal("01:05", SessionBadge.FormatTime(65.9));
        Assert.Equal("1:01:01", SessionBadge.FormatTime(3661));
    }

    // --- controller ---

    [Fact]
    public void Controller_OpensForAWhileOnStart_ThenFoldsAway()
    {
        var controller = new SessionBadgeController();
        var state = new SessionState(Titled(), null, 0);
        var now = DateTimeOffset.UtcNow;

        Assert.True(controller.Update(state, 0, now, 0, 0, false, 1280, 720)!.Expanded);
        Assert.True(controller.Update(state, 3, now, 0, 0, false, 1280, 720)!.Expanded);
        Assert.False(controller.Update(state, SessionBadgeController.AutoOpenSeconds + 1, now, 0, 0, false, 1280, 720)!.Expanded);
    }

    [Fact]
    public void Controller_ReopensWhenAStepChanges_ButNotWhenOnlyTimeMoves()
    {
        var controller = new SessionBadgeController();
        var now = DateTimeOffset.UtcNow;
        var first = new SessionState(Stepped(StepState.Running, StepState.Pending), null, 0);
        controller.Update(first, 0, now, 0, 0, false, 1280, 720);
        Assert.False(controller.Update(first, 10, now, 0, 0, false, 1280, 720)!.Expanded);

        var second = new SessionState(Stepped(StepState.Done, StepState.Running), null, 0);
        Assert.True(controller.Update(second, 11, now, 0, 0, false, 1280, 720)!.Expanded);
        Assert.False(controller.Update(second, 20, now, 0, 0, false, 1280, 720)!.Expanded);
    }

    [Fact]
    public void Controller_HoverOpens_AndToggleKeyPinsOpenAndShut()
    {
        var controller = new SessionBadgeController();
        var state = new SessionState(Titled(), null, 0);
        var now = DateTimeOffset.UtcNow;
        controller.Update(state, 100, now, 0, 0, false, 1280, 720);
        var compact = controller.Update(state, 110, now, 0, 0, false, 1280, 720)!;
        Assert.False(compact.Expanded);

        var hover = controller.Update(state, 111, now, compact.X + 2, compact.Y + 2, false, 1280, 720)!;
        Assert.True(hover.Expanded);

        var away = controller.Update(state, 112, now, 5, 700, false, 1280, 720)!;
        Assert.False(away.Expanded);

        Assert.True(controller.Update(state, 113, now, 5, 700, true, 1280, 720)!.Expanded);
        Assert.True(controller.Pinned);
        Assert.False(controller.Update(state, 114, now, 5, 700, true, 1280, 720)!.Expanded);
    }

    [Fact]
    public void Controller_NoSession_DrawsNothing()
    {
        var controller = new SessionBadgeController();
        Assert.Null(controller.Update(SessionState.None, 0, DateTimeOffset.UtcNow, 0, 0, false, 1280, 720));
    }
}
