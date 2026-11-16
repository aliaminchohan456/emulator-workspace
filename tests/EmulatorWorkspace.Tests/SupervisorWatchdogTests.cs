using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-16 item C: the watchdog is bounded, backs off, absorbs losses while it
/// runs, and stops for good until somebody deliberately retries.
/// </summary>
public class SupervisorWatchdogTests
{
    [Fact]
    public void Run_IsBoundedToFiveAttemptsWithGrowingDelays()
    {
        var watchdog = new SupervisorWatchdog();
        Assert.True(watchdog.SupervisorLost());

        var delays = new List<TimeSpan>();
        while (watchdog.TryNextDelay(out var delay))
        {
            delays.Add(delay);
            watchdog.AttemptFailed();
        }

        Assert.Equal(SupervisorWatchdog.MaxAttempts, delays.Count);
        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(15),
            },
            delays);
        Assert.Equal(SupervisorWatchState.GaveUp, watchdog.State);
        Assert.False(watchdog.IsRunning);
    }

    [Fact]
    public void LosingTheSupervisorAgainWhileRunning_StartsNoSecondRun()
    {
        var watchdog = new SupervisorWatchdog();
        Assert.True(watchdog.SupervisorLost());

        // The pipe closes, then a pending request fails a moment later.
        Assert.False(watchdog.SupervisorLost());
        Assert.Equal(0, watchdog.Attempts);
    }

    [Fact]
    public void LosingTheSupervisorAfterTheBudgetIsSpent_LeavesItToRetry()
    {
        var watchdog = new SupervisorWatchdog();
        watchdog.SupervisorLost();
        while (watchdog.TryNextDelay(out _))
        {
            watchdog.AttemptFailed();
        }

        Assert.Equal(SupervisorWatchState.GaveUp, watchdog.State);
        Assert.False(watchdog.SupervisorLost());
    }

    [Fact]
    public void AReconnect_RestoresTheFullBudget()
    {
        var watchdog = new SupervisorWatchdog();
        watchdog.SupervisorLost();
        watchdog.TryNextDelay(out _);
        watchdog.AttemptFailed();
        watchdog.TryNextDelay(out _);
        watchdog.AttemptSucceeded();

        Assert.Equal(SupervisorWatchState.Idle, watchdog.State);
        Assert.Equal(0, watchdog.Attempts);

        // The next loss is a fresh run with all five attempts again.
        Assert.True(watchdog.SupervisorLost());
        Assert.Equal(SupervisorWatchState.Waiting, watchdog.State);
    }

    [Fact]
    public void AnExplicitRetry_ReArmsAWatchdogThatGaveUp()
    {
        var watchdog = new SupervisorWatchdog();
        watchdog.SupervisorLost();
        while (watchdog.TryNextDelay(out _))
        {
            watchdog.AttemptFailed();
        }

        watchdog.Reset();

        Assert.True(watchdog.SupervisorLost());
        Assert.True(watchdog.TryNextDelay(out var delay));
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void TryNextDelay_DoesNothingUntilASupervisorIsLost()
    {
        var watchdog = new SupervisorWatchdog();

        Assert.False(watchdog.TryNextDelay(out _));
        Assert.Equal(SupervisorWatchState.Idle, watchdog.State);
    }
}
