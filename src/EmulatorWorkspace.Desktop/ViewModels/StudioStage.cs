namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-04: everything the Studio's stage decides from, in one value.
/// </summary>
/// <remarks>
/// <see cref="FrameState"/> is carried even though the stage's words do not
/// depend on it: the stage and the guest-display chip must read the same frame
/// snapshot, and a stalled or held stream changes the chip and the input gate,
/// never the stage.
/// </remarks>
public sealed record StudioStageContext(
    DeviceLifecycle? Lifecycle,
    FrameStreamState FrameState,
    LabUiState Lab,
    bool Connected,
    bool HasFrame,
    string DeviceName = "",
    string LastError = "",
    bool CaptureRestricted = false,
    string CaptureRestrictedReason = "",
    double FrameWaitSeconds = 0);

/// <summary>
/// EV-04: the stage's one decision.
///
/// Priority, highest first, with the reason each one outranks the next:
/// <list type="number">
/// <item><description><b>disconnected</b> - no managed device is selected, so
/// there is nothing to describe.</description></item>
/// <item><description><b>guest failed</b> - the supervisor recorded a failure;
/// its reason and the safe next action matter more than anything else.</description></item>
/// <item><description><b>lab recovery</b> - the guest must be recovered to
/// stock before it is used again, so this is the next action whatever the
/// engine is doing.</description></item>
/// <item><description><b>lab in progress</b> - the transaction reboots the
/// guest by design; naming the lab keeps a deliberate reboot from reading as
/// a dead engine.</description></item>
/// <item><description><b>engine gone</b> - the guest died outside the
/// supervisor and must be started again.</description></item>
/// <item><description><b>live frame</b> - a running guest with a picture needs
/// no overlay.</description></item>
/// <item><description><b>capture restricted</b> - the guest is up but a
/// FLAG_SECURE window blocks pixels.</description></item>
/// <item><description><b>starting</b> - the engine is up and Android has not
/// reported boot completion.</description></item>
/// <item><description><b>stopping</b> - the supervisor is shutting the guest
/// down.</description></item>
/// <item><description><b>waiting for the first frame</b> - Android reports
/// running but no frame has been delivered.</description></item>
/// <item><description><b>stopped</b> - the guest is not running.</description></item>
/// </list>
///
/// Every string here is the one the stage printed before this type existed
/// (the tests pin them). The one deliberate exception is the lab's detail
/// line, which no longer quotes the raw phase word (filed issue 1); the raw
/// word stays in the tooltip.
/// </summary>
public static class StudioStage
{
    /// <summary>The resolved stage: what the overlay shows, in the stage's own words.</summary>
    public readonly record struct Value(
        bool Visible,
        string Tone,
        string Title,
        string Detail,
        string Hint,
        bool ShowBootStages);

    /// <summary>No overlay: the guest's frame is the surface.</summary>
    public static Value Hidden { get; } = new(false, "info", "", "", "", false);

    public static Value Resolve(StudioStageContext context)
    {
        // Read for the single-source contract; see the context remarks.
        _ = context.FrameState;

        if (!context.Connected || context.Lifecycle is not { } lifecycle)
        {
            return new Value(
                true,
                "info",
                "No device selected",
                "Create or select a managed device to stream its Android display.",
                "Guests are isolated per instance; nothing boots until you start one.",
                false);
        }

        if (lifecycle.Phase == DevicePhase.Failed)
        {
            return new Value(
                true,
                "error",
                "Guest failed",
                string.IsNullOrWhiteSpace(context.LastError)
                    ? "The supervisor reported a failed state for this guest."
                    : context.LastError,
                "Inspect Diagnostics and the instance log, then retry. A cold boot is only safe once the cause is known.",
                true);
        }

        if (context.Lab.RequiresRecovery)
        {
            return new Value(
                true,
                "warning",
                "Lab recovery required",
                context.Lab.RecoveryStageDetail,
                "Agents are fenced while a lab needs recovery. Use Recover stock in Root Lab first.",
                false);
        }

        if (context.Lab.InstallInProgress)
        {
            return new Value(
                true,
                "info",
                "Magisk lab in progress",
                context.Lab.InstallStageDetail,
                "The guest reboots while the patch is installed. The lab journal keeps every step.",
                false);
        }

        if (lifecycle.Phase == DevicePhase.EngineGone)
        {
            return new Value(
                true,
                "error",
                "The guest's engine is gone",
                string.IsNullOrWhiteSpace(context.LastError)
                    ? $"The engine process for {context.DeviceName} is no longer running."
                    : context.LastError,
                "The guest stopped outside the supervisor. Start it again to bring it back.",
                false);
        }

        if (lifecycle.IsRunning && context.HasFrame)
        {
            return Hidden;
        }

        if (context.CaptureRestricted)
        {
            return new Value(
                true,
                "warning",
                "Capture restricted",
                string.IsNullOrWhiteSpace(context.CaptureRestrictedReason)
                    ? "The guest refused the last capture."
                    : context.CaptureRestrictedReason,
                "A FLAG_SECURE window blocks pixels. Close the protected app or window in the guest, then capture again.",
                false);
        }

        if (lifecycle.Phase == DevicePhase.Starting)
        {
            return new Value(
                true,
                "info",
                "Starting Android",
                "The engine is running and the supervisor reports each boot stage as it lands.",
                "",
                true);
        }

        if (lifecycle.Phase == DevicePhase.Stopping)
        {
            return new Value(
                true,
                "info",
                "Stopping the guest",
                "The supervisor is shutting the engine down. The window can stay open; the guest is not tied to it.",
                "",
                false);
        }

        if (lifecycle.IsRunning)
        {
            var waited = Math.Max(0, context.FrameWaitSeconds);
            return new Value(
                true,
                "info",
                "Waiting for the first frame",
                $"Android reports running. The frame stream has not delivered a frame yet — {waited:N0}s.",
                waited >= 10
                    ? "If the guest stays dark, open Logcat or Diagnostics and check the instance log for the frame pump."
                    : "",
                true);
        }

        return new Value(
            true,
            "info",
            "Guest stopped",
            $"{context.DeviceName} is stopped. Start it to stream the guest display.",
            "Stopped guests keep their profile, disks and snapshots.",
            false);
    }

    /// <summary>
    /// The compact overload the priority table is written against: the five
    /// values that distinguish every reachable state. A test that needs the
    /// guest's own words (a name, a supervisor error, a capture refusal) builds
    /// a <see cref="StudioStageContext"/> directly.
    /// </summary>
    public static Value Resolve(
        DeviceLifecycle? lifecycle,
        FrameStreamState frameState,
        LabUiState lab,
        bool connected,
        bool hasFrame) =>
        Resolve(new StudioStageContext(lifecycle, frameState, lab, connected, hasFrame));
}
