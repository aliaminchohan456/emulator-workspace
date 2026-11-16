namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// UX-18: the one lifecycle every surface speaks.
///
/// The Studio, the device library, the command palette and the agent table used
/// to read the supervisor's state word four different ways: one surface called
/// "Starting" a running guest, another called a dead engine "dead" and a third
/// printed the raw word. The same guest could therefore be "Running" in one
/// panel and "Stopped" in the next, and a control could be enabled while the
/// supervisor would refuse the action.
/// </summary>
public enum DevicePhase
{
    /// <summary>No engine process: the guest is stopped and can be started.</summary>
    Stopped,

    /// <summary>The engine is up and Android has not reported boot completion.</summary>
    Starting,

    /// <summary>Android reported boot completion: the guest is usable.</summary>
    Running,

    /// <summary>The supervisor is shutting the guest down.</summary>
    Stopping,

    /// <summary>The supervisor recorded a failure; the reason is the guest's own.</summary>
    Failed,

    /// <summary>
    /// The record pointed at an engine process that is gone: the guest died or
    /// was killed outside the supervisor.
    /// </summary>
    EngineGone,

    /// <summary>The supervisor could not say.</summary>
    Unknown,
}

/// <summary>
/// EV-03: the device commands whose enablement is decided by the lifecycle.
///
/// Start and Stop were already gated by UX-18; this names every other command
/// that used to be enabled from "a device is selected", so a control is offered
/// exactly when the supervisor would accept it. AdbUnroot and RefreshAdbState
/// share <see cref="DeviceLifecycle.CanChangeAdbRoot"/>: both need a guest whose
/// ADB daemon is really up.
/// </summary>
public enum DeviceAction
{
    Start,
    Stop,
    Restart,
    ColdBoot,
    AdbRoot,
    AdbUnroot,
    RefreshAdbState,
    Screenshot,
}

/// <summary>
/// UX-18: one guest's lifecycle, derived from the supervisor's record and the
/// evidence that came with it.
///
/// The rule that matters: the evidence outranks the record. A guest that
/// reported boot completion is running even when the record still says
/// "Starting", and a record that points at an engine process which is gone is a
/// guest that died, not a guest that is running. Every surface therefore
/// agrees about the same guest at the same moment, and every control is enabled
/// from the same capability.
/// </summary>
public sealed record DeviceLifecycle(
    DevicePhase Phase,
    string Label,
    string Tone,
    bool EngineAlive,
    bool IsRunning,
    bool IsTransitioning,
    bool CanStart,
    bool CanStop,
    bool CanSendInput,
    bool CanAttachFrames)
{
    /// <summary>
    /// The device library card's state key. The vocabulary is unchanged, so the
    /// card's own templates keep their colours and their layout.
    /// </summary>
    public string Key => Phase switch
    {
        DevicePhase.Running => "running",
        DevicePhase.Starting => "starting",
        DevicePhase.Stopping => "stopping",
        DevicePhase.Failed => "failed",
        DevicePhase.EngineGone => "dead",
        _ => "stopped",
    };

    /// <summary>
    /// An app can be installed or launched: the guest is up and Android is
    /// interactive, which is what the supervisor's install path needs.
    /// </summary>
    public bool CanInstall => IsRunning;

    /// <summary>
    /// EV-03: restart is offered while the guest runs and while it boots. The
    /// supervisor accepts both: restarting a booting guest is a clean way to
    /// abandon a boot that is not going anywhere.
    /// </summary>
    public bool CanRestart => Phase is DevicePhase.Running or DevicePhase.Starting;

    /// <summary>
    /// EV-03: a cold boot starts the engine from a clean disk. It is the one
    /// action that is also offered for a guest that is already running (the
    /// supervisor stops it first), and it is the way back from a failed or dead
    /// guest.
    /// </summary>
    public bool CanColdBoot => Phase is DevicePhase.Stopped or DevicePhase.Failed
        or DevicePhase.EngineGone or DevicePhase.Running;

    /// <summary>
    /// EV-03: changing the ADB daemon's user needs a guest whose daemon is
    /// answering, so it is offered only while the guest is really running.
    /// </summary>
    public bool CanChangeAdbRoot => Phase == DevicePhase.Running;

    /// <summary>EV-03: a screenshot needs the same live, booted guest.</summary>
    public bool CanScreenshot => Phase == DevicePhase.Running;

    /// <summary>
    /// EV-03: whether this lifecycle allows the named command. It is the one
    /// predicate every surface uses, so a button, a menu item and a palette row
    /// cannot disagree about the same guest.
    /// </summary>
    public bool Allows(DeviceAction action) => action switch
    {
        DeviceAction.Start => CanStart,
        DeviceAction.Stop => CanStop,
        DeviceAction.Restart => CanRestart,
        DeviceAction.ColdBoot => CanColdBoot,
        DeviceAction.AdbRoot or DeviceAction.AdbUnroot
            or DeviceAction.RefreshAdbState => CanChangeAdbRoot,
        DeviceAction.Screenshot => CanScreenshot,
        _ => false,
    };

    /// <summary>
    /// EV-03: why the named command is refused right now, in the operator's
    /// words, or null when the command is allowed. The reason names the state
    /// the guest is in, never a raw enum, so a disabled control and its tooltip
    /// say the same true thing.
    /// </summary>
    public string? DisabledReason(DeviceAction action)
    {
        if (Allows(action))
        {
            return null;
        }

        // Start is the one command whose refusal is not "the guest is not
        // running": the guest is running and that is exactly why it is refused.
        if (action == DeviceAction.Start && Phase == DevicePhase.Running)
        {
            return "Already running";
        }

        return Phase switch
        {
            DevicePhase.Stopped or DevicePhase.Failed => "Not running",
            DevicePhase.Starting => "Starting…",
            DevicePhase.Stopping => "Stopping…",
            DevicePhase.EngineGone => "Engine gone",
            _ => "Unknown state",
        };
    }

    /// <summary>
    /// The guest's state as the supervisor's own word, normalised for a chip.
    /// "Defined" is the record's internal word for a guest that was created and
    /// never started, so it reads "Stopped".
    /// </summary>
    public static string StateWord(string? state)
    {
        var value = (state ?? "").Trim();
        if (value.Length == 0)
        {
            return "Unknown";
        }

        return value.ToLowerInvariant() switch
        {
            "defined" => "Stopped",
            "starting" => "Starting",
            "running" => "Running",
            "stopping" => "Stopping",
            "stopped" => "Stopped",
            "failed" => "Failed",
            "unknown" => "Unknown",
            _ => char.ToUpperInvariant(value[0]) + value[1..],
        };
    }

    /// <summary>
    /// Builds the lifecycle from what the supervisor reported.
    /// </summary>
    /// <param name="state">The supervisor's state word ("Running", "Starting"...).</param>
    /// <param name="engineAlive">
    /// Whether the engine process is alive. Null means the liveness was not
    /// reported, which is not the same as "not alive".
    /// </param>
    /// <param name="enginePidKnown">
    /// Whether the record points at an engine process. A record without a
    /// process is a guest that was never started; a record with a process that
    /// is gone is a guest that died.
    /// </param>
    /// <param name="bootCompleted">Whether the guest reported sys.boot_completed=1.</param>
    public static DeviceLifecycle From(
        string? state,
        bool? engineAlive,
        bool enginePidKnown,
        bool bootCompleted)
    {
        var phase = Parse(state);

        // The guest answered for itself: it is running, whatever the record
        // still says. This is the transition that used to need a waiter.
        if (phase == DevicePhase.Starting && bootCompleted)
        {
            phase = DevicePhase.Running;
        }

        // The record points at an engine process and that process is gone.
        if (enginePidKnown
            && engineAlive == false
            && phase is DevicePhase.Running or DevicePhase.Starting or DevicePhase.Stopped)
        {
            phase = DevicePhase.EngineGone;
        }

        var alive = engineAlive ?? phase is DevicePhase.Running or DevicePhase.Starting;
        var running = phase == DevicePhase.Running;
        var transitioning = phase is DevicePhase.Starting or DevicePhase.Stopping;

        return new DeviceLifecycle(
            phase,
            LabelFor(phase),
            ToneFor(phase),
            alive,
            running,
            transitioning,
            CanStart: phase is DevicePhase.Stopped or DevicePhase.EngineGone or DevicePhase.Failed,
            CanStop: phase is DevicePhase.Running or DevicePhase.Starting,
            CanSendInput: running,
            CanAttachFrames: running);
    }

    private static DevicePhase Parse(string? state) => (state ?? "").Trim().ToLowerInvariant() switch
    {
        "defined" or "stopped" => DevicePhase.Stopped,
        "starting" => DevicePhase.Starting,
        "running" => DevicePhase.Running,
        "stopping" => DevicePhase.Stopping,
        "failed" => DevicePhase.Failed,
        _ => DevicePhase.Unknown,
    };

    private static string LabelFor(DevicePhase phase) => phase switch
    {
        DevicePhase.Stopped => "Stopped",
        DevicePhase.Starting => "Starting",
        DevicePhase.Running => "Running",
        DevicePhase.Stopping => "Stopping",
        DevicePhase.Failed => "Failed",
        DevicePhase.EngineGone => "Engine gone",
        _ => "Unknown",
    };

    private static string ToneFor(DevicePhase phase) => phase switch
    {
        DevicePhase.Running => "ok",
        DevicePhase.Starting or DevicePhase.Stopping => "warn",
        DevicePhase.Failed or DevicePhase.EngineGone => "err",
        _ => "neutral",
    };
}
