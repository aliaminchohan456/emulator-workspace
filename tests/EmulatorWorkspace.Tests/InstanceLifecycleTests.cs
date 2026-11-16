using EmulatorWorkspace.Core.Instances;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-18: the supervisor's own instance lifecycle.
///
/// The record used to reach Running only as a side effect of somebody waiting
/// for readiness, so a guest started by the CLI, by an agent or by the device
/// library stayed "Starting" in durable state until a waiter looked at it.
/// These are the transition rules that replaced that.
/// </summary>
public class InstanceLifecycleTests
{
    private static InstanceEvidence Evidence(
        bool engineAlive = false,
        bool enginePidKnown = false,
        bool adbOnline = false,
        bool bootCompleted = false) =>
        new(engineAlive, enginePidKnown, adbOnline, bootCompleted);

    [Fact]
    public void GuestThatBooted_BecomesRunningWithoutAWaiter()
    {
        var next = InstanceLifecycle.Reconcile(
            InstanceState.Starting,
            Evidence(engineAlive: true, enginePidKnown: true, adbOnline: true, bootCompleted: true));

        Assert.Equal(InstanceState.Running, next);
    }

    [Fact]
    public void RecordThatPointsAtADeadEngine_BecomesStopped()
    {
        var next = InstanceLifecycle.Reconcile(
            InstanceState.Running,
            Evidence(engineAlive: false, enginePidKnown: true));

        Assert.Equal(InstanceState.Stopped, next);
    }

    [Fact]
    public void RecordWithoutAProcess_IsLeftAlone()
    {
        // A start that has not published its pid yet is not a guest that died.
        var next = InstanceLifecycle.Reconcile(
            InstanceState.Starting,
            Evidence(engineAlive: false, enginePidKnown: false));

        Assert.Equal(InstanceState.Starting, next);
    }

    [Fact]
    public void RunningRecord_IsNeverRegressedToStarting()
    {
        // The ADB transport can go away while the guest is still up; that is not
        // evidence that it stopped booting.
        var next = InstanceLifecycle.Reconcile(
            InstanceState.Running,
            Evidence(engineAlive: true, enginePidKnown: true, adbOnline: false, bootCompleted: false));

        Assert.Equal(InstanceState.Running, next);
    }

    [Fact]
    public void RecordedFailure_IsNotOverwrittenByALaterBootAnswer()
    {
        var next = InstanceLifecycle.Reconcile(
            InstanceState.Failed,
            Evidence(engineAlive: true, enginePidKnown: true, adbOnline: true, bootCompleted: true));

        Assert.Equal(InstanceState.Failed, next);
    }

    [Fact]
    public void StoppingRecord_IsLeftToTheStopOperation()
    {
        var next = InstanceLifecycle.Reconcile(
            InstanceState.Stopping,
            Evidence(engineAlive: true, enginePidKnown: true, adbOnline: true, bootCompleted: true));

        Assert.Equal(InstanceState.Stopping, next);
    }

    [Fact]
    public void DefinedAndUnknownRecordsBecomeRunningWhenTheGuestAnswers()
    {
        Assert.Equal(
            InstanceState.Running,
            InstanceLifecycle.Reconcile(
                InstanceState.Defined,
                Evidence(engineAlive: true, enginePidKnown: true, adbOnline: true, bootCompleted: true)));

        Assert.Equal(
            InstanceState.Running,
            InstanceLifecycle.Reconcile(
                InstanceState.Unknown,
                Evidence(engineAlive: true, enginePidKnown: true, adbOnline: true, bootCompleted: true)));
    }

    [Fact]
    public void NoEvidenceIsNoTransition()
    {
        Assert.Equal(
            InstanceState.Starting,
            InstanceLifecycle.Reconcile(InstanceState.Starting, Evidence()));
    }

    [Fact]
    public void TheCapabilityRulesMatchTheSurfaces()
    {
        Assert.True(InstanceLifecycle.CanStart(InstanceState.Stopped));
        Assert.True(InstanceLifecycle.CanStart(InstanceState.Defined));
        Assert.True(InstanceLifecycle.CanStart(InstanceState.Failed));
        Assert.False(InstanceLifecycle.CanStart(InstanceState.Running));
        Assert.False(InstanceLifecycle.CanStart(InstanceState.Starting));

        Assert.True(InstanceLifecycle.CanStop(InstanceState.Running));
        Assert.True(InstanceLifecycle.CanStop(InstanceState.Starting));
        Assert.False(InstanceLifecycle.CanStop(InstanceState.Stopped));
        Assert.False(InstanceLifecycle.CanStop(InstanceState.Stopping));

        Assert.True(InstanceLifecycle.IsTransitioning(InstanceState.Starting));
        Assert.True(InstanceLifecycle.IsTransitioning(InstanceState.Stopping));
        Assert.False(InstanceLifecycle.IsTransitioning(InstanceState.Running));
    }
}
