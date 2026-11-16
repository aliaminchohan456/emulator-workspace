namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-06: the inspector's session-only rows.
///
/// The guest display, the delivered frame, the frame rate, the dropped count and
/// the boot time describe one run of one guest. They used to survive the run:
/// a stopped guest kept the last frame's resolution, rate and drop count, and a
/// restarted guest briefly showed the previous boot's measurement, because each
/// row only asked "is there a frame?" instead of "is this guest still up?".
///
/// The rule is one question: the lifecycle has to be Running or Starting. A
/// stopped, stopping, failed, dead or unknown guest reports nothing, even if a
/// stale frame or readiness stamp is still in memory. Pure, so the wording is
/// pinned by <c>InspectorSessionRowsTests</c>.
/// </summary>
public static class InspectorSessionRows
{
    /// <summary>What a row reads when the supervisor has no value for this run.</summary>
    public const string NotReported = "not reported";

    /// <summary>The Boot row's empty value: one quiet dash, never a number.</summary>
    public const string NoMeasurement = "—";

    /// <summary>Whether this lifecycle can report session-only rows at all.</summary>
    public static bool IsReported(DevicePhase? phase) =>
        phase is DevicePhase.Running or DevicePhase.Starting;

    public static string GuestDisplay(DevicePhase? phase, int width, int height) =>
        IsReported(phase) && width > 0 && height > 0
            ? $"{width} × {height}"
            : NotReported;

    public static string DeliveredFrame(DevicePhase? phase, bool hasFrame, int width, int height) =>
        IsReported(phase) && hasFrame
            ? $"{width} × {height}"
            : NotReported;

    public static string FrameRate(DevicePhase? phase, bool hasFrame, double framesPerSecond) =>
        IsReported(phase) && hasFrame
            ? $"{framesPerSecond:N1} fps"
            : NotReported;

    public static string Dropped(DevicePhase? phase, bool hasFrame, int dropped) =>
        IsReported(phase) && hasFrame
            ? dropped.ToString("N0")
            : NotReported;

    /// <summary>
    /// Time to interactive. A readiness stamp from an earlier run is not this
    /// run's measurement, so it reads as the dash until the guest is live again.
    /// </summary>
    public static string Boot(DevicePhase? phase, double? interactiveSeconds) =>
        IsReported(phase) && interactiveSeconds is { } seconds
            ? $"{seconds:N1} s"
            : NoMeasurement;
}
