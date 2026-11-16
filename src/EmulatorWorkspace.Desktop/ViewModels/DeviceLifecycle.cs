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
