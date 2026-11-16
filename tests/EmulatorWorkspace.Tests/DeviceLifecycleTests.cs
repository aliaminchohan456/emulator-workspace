using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-18: the one lifecycle every surface reads.
///
/// These are the rules the Studio, the device library, the command palette and
/// the agent table all resolve a guest through, so they are tested once here
/// instead of four times in four surfaces.
/// </summary>
public class DeviceLifecycleTests
{
    [Fact]
    public void RunningRecordWithALiveEngine_IsTheRunningPhase()
    {
        var lifecycle = DeviceLifecycle.From("Running", true, true, true);

        Assert.Equal(DevicePhase.Running, lifecycle.Phase);
        Assert.Equal("Running", lifecycle.Label);
        Assert.Equal("ok", lifecycle.Tone);
        Assert.True(lifecycle.IsRunning);
        Assert.True(lifecycle.EngineAlive);
        Assert.True(lifecycle.CanSendInput);
        Assert.True(lifecycle.CanAttachFrames);
        Assert.True(lifecycle.CanStop);
        Assert.False(lifecycle.CanStart);
        Assert.False(lifecycle.IsTransitioning);
    }

    [Fact]
    public void GuestThatReportedBootCompletion_IsRunningEvenWhileTheRecordSaysStarting()
    {
        // The record only reached Running as a side effect of somebody waiting
        // for readiness, so a guest started anywhere else stayed "Starting".
        // The guest's own answer outranks the record.
        var lifecycle = DeviceLifecycle.From("Starting", true, true, true);

        Assert.Equal(DevicePhase.Running, lifecycle.Phase);
        Assert.True(lifecycle.CanSendInput);
        Assert.True(lifecycle.CanAttachFrames);
    }

    [Fact]
    public void RecordThatPointsAtADeadEngine_IsTheEngineGonePhase()
    {
        var lifecycle = DeviceLifecycle.From("Running", false, true, false);

        Assert.Equal(DevicePhase.EngineGone, lifecycle.Phase);
        Assert.Equal("Engine gone", lifecycle.Label);
        Assert.Equal("err", lifecycle.Tone);
        Assert.False(lifecycle.IsRunning);
        Assert.False(lifecycle.CanSendInput);
        Assert.False(lifecycle.CanStop);
        Assert.True(lifecycle.CanStart);
    }

    [Fact]
    public void StoppedRecordThatKeepsItsDeadPid_StillReadsEngineGone()
    {
        // The supervisor keeps the pid as the evidence that the guest had a
        // process and lost it.
        var lifecycle = DeviceLifecycle.From("Stopped", false, true, false);

        Assert.Equal(DevicePhase.EngineGone, lifecycle.Phase);
        Assert.Equal("dead", lifecycle.Key);
    }

    [Fact]
    public void GuestThatWasNeverStarted_IsStoppedAndCanBeStarted()
    {
        var lifecycle = DeviceLifecycle.From("Defined", null, false, false);

        Assert.Equal(DevicePhase.Stopped, lifecycle.Phase);
        Assert.Equal("Stopped", lifecycle.Label);
        Assert.True(lifecycle.CanStart);
        Assert.False(lifecycle.CanStop);
        Assert.False(lifecycle.CanSendInput);
        Assert.Equal("stopped", lifecycle.Key);
    }

    [Fact]
    public void BootingGuest_CanBeStoppedButDoesNotTakeInput()
    {
        // "Starting" used to count as running, so the input and app controls
        // were live while Android was still booting and every one of them came
        // back with a refusal.
        var lifecycle = DeviceLifecycle.From("Starting", true, true, false);

        Assert.Equal(DevicePhase.Starting, lifecycle.Phase);
        Assert.True(lifecycle.IsTransitioning);
        Assert.True(lifecycle.CanStop);
        Assert.False(lifecycle.CanStart);
        Assert.False(lifecycle.CanSendInput);
        Assert.False(lifecycle.CanAttachFrames);
        Assert.False(lifecycle.CanInstall);
    }

    [Fact]
    public void StoppingGuest_HasNeitherAStartNorAStop()
    {
        var lifecycle = DeviceLifecycle.From("Stopping", true, true, true);

        Assert.Equal(DevicePhase.Stopping, lifecycle.Phase);
        Assert.True(lifecycle.IsTransitioning);
        Assert.False(lifecycle.CanStart);
        Assert.False(lifecycle.CanStop);
        Assert.False(lifecycle.CanSendInput);
    }

    [Fact]
    public void FailedGuest_CanBeStartedAgain()
    {
        var lifecycle = DeviceLifecycle.From("Failed", false, true, false);

        Assert.Equal(DevicePhase.Failed, lifecycle.Phase);
        Assert.Equal("Failed", lifecycle.Label);
        Assert.True(lifecycle.CanStart);
        Assert.False(lifecycle.CanSendInput);
    }

    [Fact]
    public void UnknownState_IsNeutralAndOffersNoMutation()
    {
        // The supervisor found a process it could not verify: it is not the
        // guest's own engine, so neither starting nor stopping it is safe.
        var lifecycle = DeviceLifecycle.From("Something New", null, false, false);

        Assert.Equal(DevicePhase.Unknown, lifecycle.Phase);
        Assert.Equal("Unknown", lifecycle.Label);
        Assert.Equal("neutral", lifecycle.Tone);
        Assert.False(lifecycle.CanStart);
        Assert.False(lifecycle.CanStop);
        Assert.False(lifecycle.CanSendInput);
    }

    [Fact]
    public void TheLibraryKeyVocabulary_IsUnchanged()
    {
        Assert.Equal("running", DeviceLifecycle.From("Running", true, true, true).Key);
        Assert.Equal("starting", DeviceLifecycle.From("Starting", true, true, false).Key);
        Assert.Equal("stopping", DeviceLifecycle.From("Stopping", true, true, true).Key);
        Assert.Equal("failed", DeviceLifecycle.From("Failed", null, false, false).Key);
        Assert.Equal("dead", DeviceLifecycle.From("Running", false, true, false).Key);
        Assert.Equal("stopped", DeviceLifecycle.From("Stopped", null, false, false).Key);
    }

    [Fact]
    public void TheStateWord_IsTheOneEverySurfacePrints()
    {
        Assert.Equal("Stopped", DeviceLifecycle.StateWord("Defined"));
        Assert.Equal("Stopped", DeviceLifecycle.StateWord("stopped"));
        Assert.Equal("Running", DeviceLifecycle.StateWord("Running"));
        Assert.Equal("Unknown", DeviceLifecycle.StateWord(""));
        Assert.Equal("Unknown", DeviceLifecycle.StateWord(null));
    }

    // EV-03: the whole table, every phase by every command. The expected cell
    // is either null (the command is allowed) or the exact reason a disabled
    // control and its tooltip print. This is the one place the enablement rule
    // is pinned, so a button, a menu item and a palette row cannot drift.
    private static readonly Dictionary<DevicePhase, DeviceLifecycle> PhaseLifecycles = new()
    {
        [DevicePhase.Stopped] = DeviceLifecycle.From("Stopped", null, false, false),
        [DevicePhase.Starting] = DeviceLifecycle.From("Starting", true, true, false),
        [DevicePhase.Running] = DeviceLifecycle.From("Running", true, true, true),
        [DevicePhase.Stopping] = DeviceLifecycle.From("Stopping", true, true, true),
        [DevicePhase.Failed] = DeviceLifecycle.From("Failed", false, true, false),
        [DevicePhase.EngineGone] = DeviceLifecycle.From("Running", false, true, false),
        [DevicePhase.Unknown] = DeviceLifecycle.From("Something New", null, false, false),
    };

    private static readonly Dictionary<DevicePhase, Dictionary<DeviceAction, string?>> Expected =
        new()
        {
            [DevicePhase.Stopped] = new()
            {
                [DeviceAction.Start] = null,
                [DeviceAction.Stop] = "Not running",
                [DeviceAction.Restart] = "Not running",
                [DeviceAction.ColdBoot] = null,
                [DeviceAction.AdbRoot] = "Not running",
                [DeviceAction.AdbUnroot] = "Not running",
                [DeviceAction.RefreshAdbState] = "Not running",
                [DeviceAction.Screenshot] = "Not running",
            },
            [DevicePhase.Starting] = new()
            {
                [DeviceAction.Start] = "Starting…",
                [DeviceAction.Stop] = null,
                [DeviceAction.Restart] = null,
                [DeviceAction.ColdBoot] = "Starting…",
                [DeviceAction.AdbRoot] = "Starting…",
                [DeviceAction.AdbUnroot] = "Starting…",
                [DeviceAction.RefreshAdbState] = "Starting…",
                [DeviceAction.Screenshot] = "Starting…",
            },
            [DevicePhase.Running] = new()
            {
                [DeviceAction.Start] = "Already running",
                [DeviceAction.Stop] = null,
                [DeviceAction.Restart] = null,
                [DeviceAction.ColdBoot] = null,
                [DeviceAction.AdbRoot] = null,
                [DeviceAction.AdbUnroot] = null,
                [DeviceAction.RefreshAdbState] = null,
                [DeviceAction.Screenshot] = null,
            },
            [DevicePhase.Stopping] = new()
            {
                [DeviceAction.Start] = "Stopping…",
                [DeviceAction.Stop] = "Stopping…",
                [DeviceAction.Restart] = "Stopping…",
                [DeviceAction.ColdBoot] = "Stopping…",
                [DeviceAction.AdbRoot] = "Stopping…",
                [DeviceAction.AdbUnroot] = "Stopping…",
                [DeviceAction.RefreshAdbState] = "Stopping…",
                [DeviceAction.Screenshot] = "Stopping…",
            },
            [DevicePhase.Failed] = new()
            {
                [DeviceAction.Start] = null,
                [DeviceAction.Stop] = "Not running",
                [DeviceAction.Restart] = "Not running",
                [DeviceAction.ColdBoot] = null,
                [DeviceAction.AdbRoot] = "Not running",
                [DeviceAction.AdbUnroot] = "Not running",
                [DeviceAction.RefreshAdbState] = "Not running",
                [DeviceAction.Screenshot] = "Not running",
            },
            [DevicePhase.EngineGone] = new()
            {
                [DeviceAction.Start] = null,
                [DeviceAction.Stop] = "Engine gone",
                [DeviceAction.Restart] = "Engine gone",
                [DeviceAction.ColdBoot] = null,
                [DeviceAction.AdbRoot] = "Engine gone",
                [DeviceAction.AdbUnroot] = "Engine gone",
                [DeviceAction.RefreshAdbState] = "Engine gone",
                [DeviceAction.Screenshot] = "Engine gone",
            },
            [DevicePhase.Unknown] = new()
            {
                [DeviceAction.Start] = "Unknown state",
                [DeviceAction.Stop] = "Unknown state",
                [DeviceAction.Restart] = "Unknown state",
                [DeviceAction.ColdBoot] = "Unknown state",
                [DeviceAction.AdbRoot] = "Unknown state",
                [DeviceAction.AdbUnroot] = "Unknown state",
                [DeviceAction.RefreshAdbState] = "Unknown state",
                [DeviceAction.Screenshot] = "Unknown state",
            },
        };

    public static IEnumerable<object[]> EveryPhaseAndAction()
    {
        foreach (var (phase, lifecycle) in PhaseLifecycles)
        {
            foreach (var action in Enum.GetValues<DeviceAction>())
            {
                var reason = Expected[phase][action];
                yield return new object[] { phase.ToString(), action, reason is null, reason ?? "" };
            }
        }
    }

    [Theory]
    [MemberData(nameof(EveryPhaseAndAction))]
    public void EveryPhaseAndAction_AgreesWithItsReason(
        string phase, DeviceAction action, bool allowed, string reason)
    {
        var lifecycle = PhaseLifecycles[Enum.Parse<DevicePhase>(phase)];

        Assert.Equal(allowed, lifecycle.Allows(action));
        Assert.Equal(allowed, lifecycle.DisabledReason(action) is null);
        Assert.Equal(reason, lifecycle.DisabledReason(action) ?? "");

        // A reason is the operator's words: it never leaks the enum's own name
        // and it is one of the five states the brief allows (plus the one
        // honest "Already running" for Start).
        if (!allowed)
        {
            Assert.NotEqual(action.ToString(), reason);
            Assert.Contains(reason, new[]
            {
                "Not running", "Starting…", "Stopping…", "Unknown state", "Engine gone", "Already running",
            });
        }
    }

    [Fact]
    public void TheNamedCapabilities_AreTheActionsTheyGate()
    {
        foreach (var lifecycle in PhaseLifecycles.Values)
        {
            Assert.Equal(lifecycle.CanRestart, lifecycle.Allows(DeviceAction.Restart));
            Assert.Equal(lifecycle.CanColdBoot, lifecycle.Allows(DeviceAction.ColdBoot));
            Assert.Equal(lifecycle.CanChangeAdbRoot, lifecycle.Allows(DeviceAction.AdbRoot));
            Assert.Equal(lifecycle.CanChangeAdbRoot, lifecycle.Allows(DeviceAction.AdbUnroot));
            Assert.Equal(lifecycle.CanChangeAdbRoot, lifecycle.Allows(DeviceAction.RefreshAdbState));
            Assert.Equal(lifecycle.CanScreenshot, lifecycle.Allows(DeviceAction.Screenshot));
        }
    }
}
