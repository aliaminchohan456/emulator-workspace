using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-08: the Logcat drawer's presentation rules. These tests pin the two things
/// a screenshot cannot show - that a level filter never hides a line it did not
/// name, and that every empty state's action maps to a command the app really
/// has - plus the exact words of the count line and the stream chip.
/// </summary>
public sealed class LogcatPresentationTests
{
    [Fact]
    public void CountTextIsEmptyForAnEmptyDrawer()
    {
        Assert.Equal("", LogcatPresentation.CountText(0, 0, 0));
        Assert.Equal("", LogcatPresentation.CountText(-4, 0, 0));
    }

    [Fact]
    public void CountTextPrintsLinesWarningsAndErrors()
    {
        Assert.Equal("1,284 lines · 3 warnings · 1 error", LogcatPresentation.CountText(1284, 3, 1));
    }

    [Fact]
    public void CountTextDropsThePartsThatAreZero()
    {
        Assert.Equal("12 lines", LogcatPresentation.CountText(12, 0, 0));
        Assert.Equal("12 lines · 2 warnings", LogcatPresentation.CountText(12, 2, 0));
        Assert.Equal("12 lines · 2 errors", LogcatPresentation.CountText(12, 0, 2));
    }

    [Fact]
    public void CountTextKeepsTheSingularForms()
    {
        Assert.Equal("1 line · 1 warning · 1 error", LogcatPresentation.CountText(1, 1, 1));
        Assert.Equal("2 lines", LogcatPresentation.CountText(2, 0, 0));
    }

    [Fact]
    public void AllChipsOnShowsEveryLevelIncludingFatalAssertAndUnknown()
    {
        var filter = LogcatPresentation.Filter(true, true, true, true, true);

        foreach (var level in new[] { "V", "D", "I", "W", "E", "F", "A", "", "x" })
        {
            Assert.True(filter.Allows(level), $"level '{level}' should pass an all-on filter");
        }
    }

    [Fact]
    public void TurningVerboseOffHidesOnlyVerbose()
    {
        var filter = LogcatPresentation.Filter(false, true, true, true, true);

        Assert.False(filter.Allows("V"));
        Assert.True(filter.Allows("D"));
        Assert.True(filter.Allows("I"));
        Assert.True(filter.Allows("W"));
        Assert.True(filter.Allows("E"));
    }

    [Fact]
    public void FatalAndAssertReadAsErrors()
    {
        var onlyErrors = LogcatPresentation.Filter(false, false, false, false, true);

        Assert.True(onlyErrors.Allows("E"));
        Assert.True(onlyErrors.Allows("F"));
        Assert.True(onlyErrors.Allows("A"));
        Assert.False(onlyErrors.Allows("W"));
        Assert.False(onlyErrors.Allows("I"));
    }

    [Fact]
    public void AnUnknownOrMissingLevelIsReadAsInfoRatherThanHiddenSilently()
    {
        Assert.Equal("I", LogLevelFilter.Normalize(""));
        Assert.Equal("I", LogLevelFilter.Normalize(null));
        Assert.Equal("I", LogLevelFilter.Normalize("x"));

        var onlyInfo = LogcatPresentation.Filter(false, false, true, false, false);
        Assert.True(onlyInfo.Allows(""));
        Assert.True(onlyInfo.Allows("nonsense"));
        Assert.False(onlyInfo.Allows("V"));
    }

    [Fact]
    public void NormalizeAcceptsLowercaseAndPadding()
    {
        Assert.Equal("W", LogLevelFilter.Normalize(" w "));
        Assert.Equal("E", LogLevelFilter.Normalize("f"));
    }

    [Fact]
    public void StreamStateNamesTheGuestAndNeverClaimsToFollowWithoutOne()
    {
        var noGuest = LogcatPresentation.Stream(guestRunning: false, subscribed: true, paused: false, "lab");

        Assert.Equal("No guest", noGuest.Text);
        Assert.Equal("info", noGuest.Tone);
        Assert.DoesNotContain("Following", noGuest.Tooltip);
    }

    [Fact]
    public void StreamStateIsNotAttachedWithoutASubscription()
    {
        var state = LogcatPresentation.Stream(guestRunning: true, subscribed: false, paused: false, "lab");

        Assert.Equal("Not attached", state.Text);
        Assert.Equal("info", state.Tone);
    }

    [Fact]
    public void StreamStateIsPausedWhenTheStreamIsHeld()
    {
        var state = LogcatPresentation.Stream(guestRunning: true, subscribed: true, paused: true, "lab");

        Assert.Equal("Paused", state.Text);
        Assert.Equal("warning", state.Tone);
        Assert.Contains("Resume", state.Tooltip);
    }

    [Fact]
    public void StreamStateIsFollowingOnlyWhenEverythingIsTrue()
    {
        var state = LogcatPresentation.Stream(guestRunning: true, subscribed: true, paused: false, "emulator-5554");

        Assert.Equal("Following", state.Text);
        Assert.Equal("ok", state.Tone);
        Assert.Contains("emulator-5554", state.Tooltip);
    }

    [Fact]
    public void EmptyStateIsHiddenWhileLinesAreVisible()
    {
        Assert.Same(LogEmptyState.None, LogcatPresentation.Empty(true, 4, true, true, false, false));
        Assert.False(LogEmptyState.None.IsVisible);
    }

    [Fact]
    public void EmptyStateOffersClearFiltersWhenTheFilterHidesEverything()
    {
        var empty = LogcatPresentation.Empty(true, 0, true, true, false, false);

        Assert.Equal("No lines match these filters", empty.Title);
        Assert.Equal("filters", empty.Action);
    }

    [Fact]
    public void EmptyStateReportsTheRefusedSubscriptionFirst()
    {
        var empty = LogcatPresentation.Empty(false, 0, true, false, false, true);

        Assert.Equal("The log stream is not running", empty.Title);
        Assert.Equal("retry", empty.Action);
    }

    [Fact]
    public void EmptyStateAsksForAStartWhenNoGuestRuns()
    {
        var empty = LogcatPresentation.Empty(false, 0, false, false, false, false);

        Assert.Equal("The guest is not running", empty.Title);
        Assert.Equal("start", empty.Action);
    }

    [Fact]
    public void EmptyStateOffersResumeWhenPausedBeforeTheFirstLine()
    {
        var empty = LogcatPresentation.Empty(false, 0, true, true, true, false);

        Assert.Equal("Paused before the first line", empty.Title);
        Assert.Equal("resume", empty.Action);
    }

    [Fact]
    public void EmptyStateWaitsQuietlyWithNoActionWhileTheStreamIsOpen()
    {
        var empty = LogcatPresentation.Empty(false, 0, true, true, false, false);

        Assert.Equal("Waiting for the first line", empty.Title);
        Assert.Equal("", empty.Action);
    }

    [Fact]
    public void EmptyStateOffersRetryWhenTheSubscriptionIsNotAttached()
    {
        var empty = LogcatPresentation.Empty(false, 0, true, false, false, false);

        Assert.Equal("Waiting for the log stream", empty.Title);
        Assert.Equal("retry", empty.Action);
    }

    [Fact]
    public void EveryEmptyStateActionIsOneTheDrawerCanRun()
    {
        // The XAML maps these keys to ClearLogFiltersCommand, RetryLogcatCommand,
        // StartCommand and ResumeLogCommand; any other key would render a dead
        // button, so the set is closed here.
        var known = new[] { "", "filters", "retry", "start", "resume" };

        var states = new[]
        {
            LogcatPresentation.Empty(false, 0, false, false, false, false),
            LogcatPresentation.Empty(false, 0, false, false, false, true),
            LogcatPresentation.Empty(false, 0, true, false, false, false),
            LogcatPresentation.Empty(false, 0, true, true, true, false),
            LogcatPresentation.Empty(false, 0, true, true, false, false),
            LogcatPresentation.Empty(true, 0, true, true, false, false),
            LogcatPresentation.Empty(true, 3, true, true, false, false),
        };

        foreach (var state in states)
        {
            Assert.Contains(state.Action, known);
            Assert.Equal(state.Title.Length > 0, state.IsVisible);
        }
    }

    // ---- UX-08 fix pass: the dock's segmented summary and the full page -----

    [Fact]
    public void SummarySegmentsKeepTheColouredCountsOutOfTheLineCount()
    {
        // The dock header prints three segments so warn/err can carry their own
        // tokens; each still has to agree with the combined line the tests above
        // pin.
        Assert.Equal("1 line", LogcatPresentation.LineCount(1));
        Assert.Equal("1,284 lines", LogcatPresentation.LineCount(1284));
        Assert.Equal("3 warnings", LogcatPresentation.WarningCount(3));
        Assert.Equal("1 warning", LogcatPresentation.WarningCount(1));
        Assert.Equal("1,024 errors", LogcatPresentation.ErrorCount(1024));
        Assert.Equal("1 error", LogcatPresentation.ErrorCount(1));
    }

    [Fact]
    public void PausedChipNamesTheBufferedCountOnlyWhenItHasOne()
    {
        Assert.Equal("Paused", LogcatPresentation.PausedBuffered(0));
        Assert.Equal("Paused · 12 buffered", LogcatPresentation.PausedBuffered(12));
        Assert.Equal("Paused · 1,284 buffered", LogcatPresentation.PausedBuffered(1284));
    }

    [Fact]
    public void PageStreamSaysLiveOnlyWhileTheStreamIsAttachedAndFollowing()
    {
        var live = LogcatPresentation.PageStream(true, true, false, "emulator-5554");

        Assert.Equal("Live", live.Text);
        Assert.Equal("ok", live.Tone);
        Assert.Contains("emulator-5554", live.Tooltip);

        Assert.Equal("Paused", LogcatPresentation.PageStream(true, true, true, "emulator-5554").Text);
        Assert.Equal("Not attached", LogcatPresentation.PageStream(true, false, false, "emulator-5554").Text);
        Assert.Equal("No guest", LogcatPresentation.PageStream(false, false, false, "emulator-5554").Text);
    }

    [Fact]
    public void PageEmptyNamesTheSerialItIsWaitingForAndOffersNoButton()
    {
        var empty = LogcatPresentation.PageEmpty(false, 0, true, true, false, false, "emulator-5554");

        Assert.Equal("No lines yet · waiting for emulator-5554", empty.Title);
        Assert.Equal("", empty.Action);
    }

    [Fact]
    public void PageEmptyOffersClearFiltersAndFallsBackWhenNoSerialIsKnown()
    {
        var filtered = LogcatPresentation.PageEmpty(true, 0, true, true, false, false, "emulator-5554");

        Assert.Equal("No lines match these filters", filtered.Title);
        Assert.Equal("filters", filtered.Action);

        var unknown = LogcatPresentation.PageEmpty(false, 0, true, true, false, false, null);
        Assert.Equal("No lines yet · waiting for the selected guest", unknown.Title);
    }

    [Fact]
    public void PageEmptyKeepsTheRealRecoveryActionsForGuestAndSubscriptionFailures()
    {
        Assert.Equal("start", LogcatPresentation.PageEmpty(false, 0, false, false, false, false, "lab").Action);
        Assert.Equal("retry", LogcatPresentation.PageEmpty(false, 0, true, false, false, false, "lab").Action);
        Assert.Equal("resume", LogcatPresentation.PageEmpty(false, 0, true, true, true, false, "lab").Action);
        Assert.Equal("retry", LogcatPresentation.PageEmpty(false, 0, true, false, false, true, "lab").Action);
    }

    [Fact]
    public void PageEmptyIsHiddenWhileAnyLineIsVisible()
    {
        Assert.Same(LogEmptyState.None, LogcatPresentation.PageEmpty(true, 3, true, true, false, false, "lab"));
    }

    [Fact]
    public void SearchIndexesCoverEveryOccurrenceCaseInsensitively()
    {
        // "init" appears twice in "Init the init"; each hit contributes its four
        // offsets, so the page paints the same characters the filter kept.
        var indexes = LogcatPresentation.IndexesOf("Init the init", "init");

        Assert.Equal(new[] { 0, 1, 2, 3, 9, 10, 11, 12 }, indexes);
    }

    [Fact]
    public void SearchIndexesAreEmptyWhenNothingMatchesOrTheQueryIsBlank()
    {
        Assert.Empty(LogcatPresentation.IndexesOf("Collecting garbage", "zzz"));
        Assert.Empty(LogcatPresentation.IndexesOf("Collecting garbage", ""));
        Assert.Empty(LogcatPresentation.IndexesOf("Collecting garbage", "  "));
        Assert.Empty(LogcatPresentation.IndexesOf("", "init"));
        Assert.Empty(LogcatPresentation.IndexesOf(null, "init"));
        Assert.Empty(LogcatPresentation.IndexesOf("init", null));
    }

    [Fact]
    public void SearchIndexesHandleOverlappingStartPositionsWithoutDuplicating()
    {
        // After a hit the scan resumes past it, so "aa" in "aaaa" marks 0-1 and
        // 2-3 rather than overlapping at 1.
        Assert.Equal(new[] { 0, 1, 2, 3 }, LogcatPresentation.IndexesOf("aaaa", "aa"));
    }
}
