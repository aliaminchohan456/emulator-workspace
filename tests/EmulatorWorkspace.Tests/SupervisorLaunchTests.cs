using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-11b: the launch flags and the one fallback rule, without starting a
/// process. CREATE_BREAKAWAY_FROM_JOB is what lets the supervisor outlive the
/// shell that launched it; a job that forbids breakaway answers
/// ERROR_ACCESS_DENIED and that is the only failure worth a second attempt.
/// </summary>
public sealed class SupervisorLaunchTests
{
    [Fact]
    public void Flags_AskForBreakawayGroupAndNoWindow()
    {
        var flags = SupervisorLaunch.Flags(true);
        Assert.Equal(0x01000000u, flags & SupervisorLaunch.CreateBreakawayFromJob);
        Assert.Equal(0x00000200u, flags & SupervisorLaunch.CreateNewProcessGroup);
        Assert.Equal(0x08000000u, flags & SupervisorLaunch.CreateNoWindow);
    }

    [Fact]
    public void Fallback_KeepsGroupAndWindowFlagsAndDropsOnlyBreakaway()
    {
        var fallback = SupervisorLaunch.Flags(false);
        Assert.Equal(0u, fallback & SupervisorLaunch.CreateBreakawayFromJob);
        Assert.Equal(SupervisorLaunch.CreateNewProcessGroup, fallback & SupervisorLaunch.CreateNewProcessGroup);
        Assert.Equal(SupervisorLaunch.CreateNoWindow, fallback & SupervisorLaunch.CreateNoWindow);
        Assert.Equal(SupervisorLaunch.Flags(true) & ~SupervisorLaunch.CreateBreakawayFromJob, fallback);
    }

    [Fact]
    public void DeniedBreakaway_IsRetriedWithoutTheFlag()
    {
        Assert.True(SupervisorLaunch.IsBreakawayDenial(5));
        Assert.Equal(LaunchRetry.WithoutBreakaway, SupervisorLaunch.NextAttempt(5, breakawayRequested: true));
    }

    [Fact]
    public void EveryOtherFailure_IsReportedAsItIs()
    {
        Assert.Equal(LaunchRetry.GiveUp, SupervisorLaunch.NextAttempt(2, breakawayRequested: true));
        Assert.Equal(LaunchRetry.GiveUp, SupervisorLaunch.NextAttempt(87, breakawayRequested: true));
        Assert.Equal(LaunchRetry.GiveUp, SupervisorLaunch.NextAttempt(0, breakawayRequested: true));
    }

    [Fact]
    public void AFallbackThatAlsoFails_IsNotRetriedInALoop()
    {
        Assert.Equal(LaunchRetry.GiveUp, SupervisorLaunch.NextAttempt(5, breakawayRequested: false));
    }

    [Fact]
    public void FallbackNote_IsOneLineAndNamesTheFlagAndTheConsequence()
    {
        var note = SupervisorLaunch.FallbackNote(5);
        Assert.DoesNotContain('\n', note);
        Assert.DoesNotContain('\r', note);
        Assert.Contains("CREATE_BREAKAWAY_FROM_JOB", note);
        Assert.Contains("error 5", note);
        Assert.Contains("shares the launcher's job object", note);
    }
}
