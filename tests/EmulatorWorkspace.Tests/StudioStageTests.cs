using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-04: the Studio stage's one decision.
///
/// The stage used to be a chain of branches inside the window's view model,
/// where no test could reach it. These tests pin the words it printed before
/// the refactor, then prove the resolver returns the same value for every
/// reachable non-lab state and the documented priority for the lab states.
/// </summary>
public class StudioStageTests
{
    private static LabUiState StockLab =>
        LabUiState.From("not_configured", false, false, LabJournalState.None);

    private static DeviceLifecycle Stopped => DeviceLifecycle.From("Stopped", false, false, false);

    private static DeviceLifecycle Starting => DeviceLifecycle.From("Starting", true, true, false);

    private static DeviceLifecycle Running => DeviceLifecycle.From("Running", true, true, true);

    private static DeviceLifecycle Stopping => DeviceLifecycle.From("Stopping", true, true, true);

    private static DeviceLifecycle Failed => DeviceLifecycle.From("Failed", false, false, false);

    private static DeviceLifecycle EngineGone => DeviceLifecycle.From("Running", false, true, false);

    private static DeviceLifecycle Unknown => DeviceLifecycle.From("", null, false, false);

    private static StudioStage.Value Stage(
        DeviceLifecycle? lifecycle,
        LabUiState? lab = null,
        bool connected = true,
        bool hasFrame = false,
        FrameStreamState frame = FrameStreamState.Live,
        string name = "ev04",
        string error = "",
        bool restricted = false,
        string restrictedReason = "",
        double waited = 0) =>
        StudioStage.Resolve(new StudioStageContext(
            lifecycle, frame, lab ?? StockLab, connected, hasFrame,
            name, error, restricted, restrictedReason, waited));

    // ---- the strings as they were, pinned before the refactor ---------------

    [Fact]
    public void PinnedNoDeviceText()
    {
        var stage = Stage(lifecycle: null);

        Assert.True(stage.Visible);
        Assert.Equal("info", stage.Tone);
        Assert.Equal("No device selected", stage.Title);
        Assert.Equal("Create or select a managed device to stream its Android display.", stage.Detail);
        Assert.Equal("Guests are isolated per instance; nothing boots until you start one.", stage.Hint);
        Assert.False(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedFailedText()
    {
        var stage = Stage(Failed);

        Assert.True(stage.Visible);
        Assert.Equal("error", stage.Tone);
        Assert.Equal("Guest failed", stage.Title);
        Assert.Equal("The supervisor reported a failed state for this guest.", stage.Detail);
        Assert.Equal(
            "Inspect Diagnostics and the instance log, then retry. A cold boot is only safe once the cause is known.",
            stage.Hint);
        Assert.True(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedFailedTextKeepsTheSupervisorsReason()
    {
        var stage = Stage(Failed, error: "The engine exited with code 1.");

        Assert.Equal("The engine exited with code 1.", stage.Detail);
    }

    [Fact]
    public void PinnedEngineGoneText()
    {
        var stage = Stage(EngineGone);

        Assert.True(stage.Visible);
        Assert.Equal("error", stage.Tone);
        Assert.Equal("The guest's engine is gone", stage.Title);
        Assert.Equal("The engine process for ev04 is no longer running.", stage.Detail);
        Assert.Equal("The guest stopped outside the supervisor. Start it again to bring it back.", stage.Hint);
        Assert.False(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedLabRecoveryText()
    {
        // The only stage sentence that changed on purpose: filed issue 1 put the
        // raw phase word on screen; it is now in the lab tooltip only.
        var lab = LabUiState.From("provisioned", false, true, LabJournalState.Open);

        var stage = Stage(Starting, lab);

        Assert.True(stage.Visible);
        Assert.Equal("warning", stage.Tone);
        Assert.Equal("Lab recovery required", stage.Title);
        Assert.Equal(
            "The Magisk lab transaction needs a stock recovery. Use Recover stock in Root Lab before starting or changing this guest.",
            stage.Detail);
        Assert.DoesNotContain("provisioned", stage.Detail, StringComparison.Ordinal);
        Assert.Equal("Agents are fenced while a lab needs recovery. Use Recover stock in Root Lab first.", stage.Hint);
        Assert.False(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedLabInProgressText()
    {
        var lab = LabUiState.From("activating", true, false, LabJournalState.Open);

        var stage = Stage(Starting, lab);

        Assert.True(stage.Visible);
        Assert.Equal("info", stage.Tone);
        Assert.Equal("Magisk lab in progress", stage.Title);
        Assert.Equal("Magisk lab: install in progress", stage.Detail);
        Assert.DoesNotContain("activating", stage.Detail, StringComparison.Ordinal);
        Assert.Equal("The guest reboots while the patch is installed. The lab journal keeps every step.", stage.Hint);
        Assert.False(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedCaptureRestrictedText()
    {
        var stage = Stage(Running, restricted: true, restrictedReason: "The guest refused the last capture.");

        Assert.True(stage.Visible);
        Assert.Equal("warning", stage.Tone);
        Assert.Equal("Capture restricted", stage.Title);
        Assert.Equal("The guest refused the last capture.", stage.Detail);
        Assert.Equal(
            "A FLAG_SECURE window blocks pixels. Close the protected app or window in the guest, then capture again.",
            stage.Hint);
        Assert.False(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedStartingText()
    {
        var stage = Stage(Starting);

        Assert.True(stage.Visible);
        Assert.Equal("info", stage.Tone);
        Assert.Equal("Starting Android", stage.Title);
        Assert.Equal(
            "The engine is running and the supervisor reports each boot stage as it lands.",
            stage.Detail);
        Assert.Equal("", stage.Hint);
        Assert.True(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedStoppingText()
    {
        var stage = Stage(Stopping);

        Assert.True(stage.Visible);
        Assert.Equal("info", stage.Tone);
        Assert.Equal("Stopping the guest", stage.Title);
        Assert.Equal(
            "The supervisor is shutting the engine down. The window can stay open; the guest is not tied to it.",
            stage.Detail);
        Assert.Equal("", stage.Hint);
        Assert.False(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedWaitingText()
    {
        var stage = Stage(Running, waited: 6);

        Assert.True(stage.Visible);
        Assert.Equal("info", stage.Tone);
        Assert.Equal("Waiting for the first frame", stage.Title);
        Assert.Equal(
            "Android reports running. The frame stream has not delivered a frame yet — 6s.",
            stage.Detail);
        Assert.Equal("", stage.Hint);
        Assert.True(stage.ShowBootStages);
    }

    [Fact]
    public void PinnedStoppedText()
    {
        var stage = Stage(Stopped);

        Assert.True(stage.Visible);
        Assert.Equal("info", stage.Tone);
        Assert.Equal("Guest stopped", stage.Title);
        Assert.Equal("ev04 is stopped. Start it to stream the guest display.", stage.Detail);
        Assert.Equal("Stopped guests keep their profile, disks and snapshots.", stage.Hint);
        Assert.False(stage.ShowBootStages);
    }

    // ---- the priority table -------------------------------------------------

    [Fact]
    public void PriorityTable_DisconnectedBeatsEveryLifecycle()
    {
        Assert.Equal("No device selected", Stage(null).Title);
        Assert.Equal("No device selected", Stage(null, connected: true).Title);
        Assert.Equal("No device selected", Stage(Running, connected: false, hasFrame: true).Title);
    }

    [Fact]
    public void PriorityTable_FailedBeatsTheLab()
    {
        var lab = LabUiState.From("provisioned", false, true, LabJournalState.Open);

        Assert.Equal("Guest failed", Stage(Failed, lab).Title);
    }

    [Fact]
    public void PriorityTable_LabRecoveryBeatsAnInstallThatIsAlsoReported()
    {
        var lab = LabUiState.From("activating", installInProgress: true, recoveryRequired: true, LabJournalState.Open);

        Assert.Equal("Lab recovery required", Stage(Starting, lab).Title);
    }

    [Fact]
    public void PriorityTable_LabRecoveryBeatsADeadEngine()
    {
        var lab = LabUiState.From("provisioned", false, true, LabJournalState.Open);

        Assert.Equal("Lab recovery required", Stage(EngineGone, lab).Title);
    }

    [Fact]
    public void PriorityTable_LabRecoveryBeatsAStartingGuest()
    {
        var lab = LabUiState.From("provisioned", false, true, LabJournalState.Open);

        Assert.Equal("Lab recovery required", Stage(Starting, lab).Title);
    }

    [Fact]
    public void PriorityTable_LabInProgressBeatsADeadEngine()
    {
        // The lab reboots the guest by design, so mid-transaction the engine
        // comes and goes; the transaction owns the stage.
        var lab = LabUiState.From("activating", true, false, LabJournalState.Open);

        Assert.Equal("Magisk lab in progress", Stage(EngineGone, lab).Title);
    }

    [Fact]
    public void PriorityTable_LabInProgressBeatsAStartingGuest()
    {
        var lab = LabUiState.From("activating", true, false, LabJournalState.Open);

        Assert.Equal("Magisk lab in progress", Stage(Starting, lab).Title);
    }

    [Fact]
    public void PriorityTable_LabInProgressBeatsALiveFrame()
    {
        var lab = LabUiState.From("activating", true, false, LabJournalState.Open);

        Assert.Equal("Magisk lab in progress", Stage(Running, lab, hasFrame: true).Title);
    }

    [Fact]
    public void PriorityTable_EngineGoneBeatsAStartingGuest()
    {
        Assert.Equal("The guest's engine is gone", Stage(EngineGone).Title);
    }

    [Fact]
    public void PriorityTable_LiveFrameShowsNothing()
    {
        var stage = Stage(Running, hasFrame: true);

        Assert.Equal(StudioStage.Hidden, stage);
        Assert.False(stage.Visible);
    }

    [Fact]
    public void PriorityTable_LiveFrameBeatsACaptureRefusal()
    {
        // The stage is already gone once a frame is delivered; the chip and the
        // toast carry the refusal.
        var stage = Stage(Running, hasFrame: true, restricted: true);

        Assert.False(stage.Visible);
    }

    [Fact]
    public void PriorityTable_CaptureRestrictionBeatsStarting()
    {
        // Not reachable from the boot path, but the resolver is total: a
        // restriction recorded while the guest starts still names itself.
        var stage = Stage(Starting, restricted: true);

        Assert.Equal("Capture restricted", stage.Title);
    }

    [Fact]
    public void PriorityTable_StartingShowsTheBootStages()
    {
        var stage = Stage(Starting);

        Assert.Equal("Starting Android", stage.Title);
        Assert.True(stage.ShowBootStages);
    }

    [Fact]
    public void PriorityTable_StoppingShowsNoBootStages()
    {
        var stage = Stage(Stopping);

        Assert.Equal("Stopping the guest", stage.Title);
        Assert.False(stage.ShowBootStages);
    }

    [Fact]
    public void PriorityTable_WaitingForTheFirstFrameShowsTheBootStages()
    {
        var stage = Stage(Running, waited: 3);

        Assert.Equal("Waiting for the first frame", stage.Title);
        Assert.True(stage.ShowBootStages);
    }

    [Fact]
    public void PriorityTable_StoppedIsTheFallback()
    {
        Assert.Equal("Guest stopped", Stage(Stopped).Title);
        Assert.Equal("Guest stopped", Stage(Unknown).Title);
    }

    [Fact]
    public void WaitingTenSecondsOrMore_OffersTheLogcatHint()
    {
        var early = Stage(Running, waited: 9.9);
        var late = Stage(Running, waited: 10);

        Assert.Equal("", early.Hint);
        Assert.Equal(
            "If the guest stays dark, open Logcat or Diagnostics and check the instance log for the frame pump.",
            late.Hint);
    }

    // ---- one frame source ---------------------------------------------------

    [Theory]
    [InlineData(FrameStreamState.Live)]
    [InlineData(FrameStreamState.Idle)]
    [InlineData(FrameStreamState.Stalled)]
    [InlineData(FrameStreamState.Held)]
    public void TheFrameStateAloneNeverChangesTheStagesWords(FrameStreamState frame)
    {
        var starting = Stage(Starting, frame: frame);
        var waiting = Stage(Running, frame: frame, hasFrame: false);
        var live = Stage(Running, frame: frame, hasFrame: true);

        Assert.Equal("Starting Android", starting.Title);
        Assert.Equal("Waiting for the first frame", waiting.Title);
        Assert.False(live.Visible);
    }

    // ---- equivalence with the code that used to decide ----------------------

    [Fact]
    public void EveryNonLabState_ProducesTheSameValueAsTheLegacyStage()
    {
        var lifecycles = new DeviceLifecycle?[]
        {
            null, Stopped, Starting, Running, Stopping, Failed, EngineGone, Unknown,
        };
        var frames = Enum.GetValues<FrameStreamState>();

        foreach (var lifecycle in lifecycles)
        {
            foreach (var connected in new[] { true, false })
            {
                foreach (var hasFrame in new[] { true, false })
                {
                    foreach (var restricted in new[] { true, false })
                    {
                        foreach (var waited in new[] { 0d, 12d })
                        {
                            foreach (var frame in frames)
                            {
                                var context = new StudioStageContext(
                                    lifecycle, frame, StockLab, connected, hasFrame,
                                    "ev04", "", restricted, "", waited);

                                Assert.Equal(LegacyStage(context), StudioStage.Resolve(context));
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// The branch chain exactly as it stood in <c>MainViewModel.UpdateStageCore</c>
    /// before EV-04 moved it here. The lab details still quote the raw phase
    /// word, which is the one deliberate difference the pins above record.
    /// </summary>
    private static StudioStage.Value LegacyStage(StudioStageContext context)
    {
        if (!context.Connected || context.Lifecycle is not { } lifecycle)
        {
            return new StudioStage.Value(true, "info", "No device selected",
                "Create or select a managed device to stream its Android display.",
                "Guests are isolated per instance; nothing boots until you start one.", false);
        }

        if (lifecycle.Phase == DevicePhase.Failed)
        {
            return new StudioStage.Value(true, "error", "Guest failed",
                string.IsNullOrWhiteSpace(context.LastError)
                    ? "The supervisor reported a failed state for this guest."
                    : context.LastError,
                "Inspect Diagnostics and the instance log, then retry. A cold boot is only safe once the cause is known.",
                true);
        }

        if (lifecycle.Phase == DevicePhase.EngineGone)
        {
            return new StudioStage.Value(true, "error", "The guest's engine is gone",
                string.IsNullOrWhiteSpace(context.LastError)
                    ? $"The engine process for {context.DeviceName} is no longer running."
                    : context.LastError,
                "The guest stopped outside the supervisor. Start it again to bring it back.",
                false);
        }

        if (context.Lab.RequiresRecovery)
        {
            return new StudioStage.Value(true, "warning", "Lab recovery required",
                $"The Magisk lab transaction is in phase '{context.Lab.RawPhase}'. Use Recover stock in Root Lab before starting or changing this guest.",
                "Agents are fenced while a lab needs recovery. Use Recover stock in Root Lab first.",
                false);
        }

        if (context.Lab.InstallInProgress)
        {
            return new StudioStage.Value(true, "info", "Magisk lab in progress",
                context.Lab.Tooltip,
                "The guest reboots while the patch is installed. The lab journal keeps every step.",
                false);
        }

        if (lifecycle.IsRunning && context.HasFrame)
        {
            return StudioStage.Hidden;
        }

        if (context.CaptureRestricted)
        {
            return new StudioStage.Value(true, "warning", "Capture restricted",
                string.IsNullOrWhiteSpace(context.CaptureRestrictedReason)
                    ? "The guest refused the last capture."
                    : context.CaptureRestrictedReason,
                "A FLAG_SECURE window blocks pixels. Close the protected app or window in the guest, then capture again.",
                false);
        }

        if (lifecycle.Phase == DevicePhase.Starting)
        {
            return new StudioStage.Value(true, "info", "Starting Android",
                "The engine is running and the supervisor reports each boot stage as it lands.",
                "", true);
        }

        if (lifecycle.Phase == DevicePhase.Stopping)
        {
            return new StudioStage.Value(true, "info", "Stopping the guest",
                "The supervisor is shutting the engine down. The window can stay open; the guest is not tied to it.",
                "", false);
        }

        if (lifecycle.IsRunning)
        {
            var waited = Math.Max(0, context.FrameWaitSeconds);
            return new StudioStage.Value(true, "info", "Waiting for the first frame",
                $"Android reports running. The frame stream has not delivered a frame yet — {waited:N0}s.",
                waited >= 10
                    ? "If the guest stays dark, open Logcat or Diagnostics and check the instance log for the frame pump."
                    : "",
                true);
        }

        return new StudioStage.Value(true, "info", "Guest stopped",
            $"{context.DeviceName} is stopped. Start it to stream the guest display.",
            "Stopped guests keep their profile, disks and snapshots.", false);
    }
}
