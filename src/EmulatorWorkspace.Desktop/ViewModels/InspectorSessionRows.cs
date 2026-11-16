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

    /// <summary>The identity row's empty value: one dash, never a blank cell.</summary>
    public const string NoValue = "—";

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

    /// <summary>
    /// EV-07: the inspector's ABI / API row. A stopped guest's status carries no
    /// <c>abi</c> or <c>sdkLevel</c>, so the row used to go blank the moment the
    /// engine stopped. The last reported pair is cached, and the image package
    /// the record already carries is the fallback before any run; only when
    /// neither exists does the row read the dash. It is never an empty string.
    /// </summary>
    public static string AbiApi(string reportedAbi, string reportedApi, string cachedAbi, string cachedApi)
    {
        var abi = FirstReported(reportedAbi, cachedAbi);
        var api = FirstReported(reportedApi, cachedApi);
        if (abi.Length == 0 && api.Length == 0)
        {
            return NoValue;
        }

        if (abi.Length == 0)
        {
            return api;
        }

        return api.Length == 0 ? abi : $"{abi} · {api}";
    }

    private static string FirstReported(string reported, string cached) =>
        reported.Trim().Length > 0 ? reported.Trim() : cached.Trim();
}
