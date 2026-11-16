namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// UX-18: the frame stream's own state.
///
/// The viewport used to call a frame "stale" when no new frame had arrived for
/// five seconds, and it then warned about a stalled stream and dropped the
/// operator's taps and swipes. But the emulator's image stream is change
/// driven: a guest whose screen does not change sends no frames at all. An
/// idle guest therefore looked broken - the amber "Stalled 16s" chip over a
/// perfectly current picture, and taps that went nowhere.
///
/// The states are therefore separated: an idle guest is idle, a held guest is
/// held, and only a stream the supervisor says is not running (or a guest that
/// stopped answering) is stalled.
/// </summary>
public enum FrameStreamState
{
    /// <summary>Frames are arriving.</summary>
    Live,

    /// <summary>
    /// The guest's screen has not changed, so the emulator sent no new frame.
    /// The picture on screen is current and the operator can still work.
    /// </summary>
    Idle,

    /// <summary>
    /// The capture is not running or the guest stopped answering: the picture
    /// may really be out of date, so the input waits.
    /// </summary>
    Stalled,

    /// <summary>
    /// A supervised operation owns the guest's ADB channel (an install, a
    /// launch). The input waits for it to finish.
    /// </summary>
    Held,
}

/// <summary>
/// UX-18: the rules behind the frame stream's state, kept out of WPF so they
/// can be tested without a guest.
/// </summary>
public static class FrameStreamHealth
{
    /// <summary>
    /// How old a frame may get before it is worth a second look. The budget
    /// only matters once the stream is known to be unhealthy: an idle guest
    /// passes it every second.
    /// </summary>
    public const double StaleSeconds = 5;

    /// <summary>
    /// The state the stream is in.
    /// </summary>
    /// <param name="guestRunning">The guest is up (the lifecycle says so).</param>
    /// <param name="hasFrame">A frame has been delivered and presented.</param>
    /// <param name="ageSeconds">How old the presented frame is.</param>
    /// <param name="streamBroken">
    /// The supervisor says the capture is not running while this surface holds
    /// a subscription.
    /// </param>
    /// <param name="guestResponsive">
    /// The guest answered the last status read: its ADB transport was usable.
    /// </param>
    /// <param name="held">A supervised operation owns the guest's ADB channel.</param>
    public static FrameStreamState Evaluate(
        bool guestRunning,
        bool hasFrame,
        double ageSeconds,
        bool streamBroken,
        bool guestResponsive,
        bool held)
    {
        if (held)
        {
            return FrameStreamState.Held;
        }

        if (!guestRunning || !hasFrame || ageSeconds <= StaleSeconds)
        {
            return FrameStreamState.Live;
        }

        // No new frame on its own means the screen did not change. It is only
        // a stalled stream when the supervisor says the capture is not running
        // or the guest stopped answering.
        return streamBroken || !guestResponsive
            ? FrameStreamState.Stalled
            : FrameStreamState.Idle;
    }

    /// <summary>The stream is broken and the picture may be out of date.</summary>
    public static bool IsStalled(FrameStreamState state) => state == FrameStreamState.Stalled;

    /// <summary>
    /// The input waits while the stream is stalled (the picture may be out of
    /// date) and while an operation holds the guest's channel.
    /// </summary>
    public static bool PausesInput(FrameStreamState state) =>
        state is FrameStreamState.Stalled or FrameStreamState.Held;

    /// <summary>The chip's tone: a hold is information, a stall is a warning.</summary>
    public static string Tone(FrameStreamState state) => state switch
    {
        FrameStreamState.Held => "info",
        FrameStreamState.Stalled => "warning",
        _ => "",
    };

    /// <summary>The chip's sentence.</summary>
    public static string Title(FrameStreamState state, string holdLabel, double ageSeconds) => state switch
    {
        FrameStreamState.Held => $"Paused while {holdLabel} runs",
        FrameStreamState.Stalled => $"Frame stalled · last frame {ageSeconds:N0} s ago",
        _ => "",
    };

    /// <summary>The chip's second line.</summary>
    public static string Detail(FrameStreamState state) => state switch
    {
        FrameStreamState.Held =>
            "The supervisor holds the guest's ADB channel. Input resumes when the operation finishes.",
        FrameStreamState.Stalled =>
            "The frame stream resumes with the next frame; input is paused until then.",
        _ => "",
    };
}
