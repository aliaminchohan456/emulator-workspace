namespace EmulatorWorkspace.Core.Instances;

/// <summary>
/// What the supervisor observed about one managed guest at one moment. Every
/// field is a fact the supervisor read for itself: the engine process, the ADB
/// transport and the guest's own <c>sys.boot_completed</c> property. Nothing
/// here is an intent or a guess.
/// </summary>
/// <param name="EngineAlive">The engine process is running right now.</param>
/// <param name="EnginePidKnown">
/// The record points at an engine process. A record without a process is a guest
/// that was never started (or whose start has not published its pid yet), which
/// is a different fact from a guest whose process is gone.
/// </param>
/// <param name="AdbOnline">The ADB transport answered in the "device" state.</param>
/// <param name="BootCompleted">The guest itself reported <c>sys.boot_completed=1</c>.</param>
public readonly record struct InstanceEvidence(
    bool EngineAlive,
    bool EnginePidKnown,
    bool AdbOnline,
    bool BootCompleted);

/// <summary>
/// The managed instance's lifecycle, written down as one explicit state machine.
///
/// The supervisor used to move a record to <see cref="InstanceState.Running"/>
/// only as a side effect of somebody waiting for readiness, so a guest that was
/// started by the CLI, by an agent or by the device library stayed "Starting" in
/// durable state until a waiter happened to look at it - and every surface that
/// keyed off the record's word then disagreed with the guest that was really
/// there. This class is the single place where the record's word and the
/// observed evidence are reconciled, so the transition has one owner.
///
/// The rules are deliberately monotonic. Evidence moves a record forward
/// (Starting to Running) or reports that the process is gone (to Stopped); it
/// never invents a state and never overwrites a recorded failure with a guess.
/// </summary>
public static class InstanceLifecycle
{
    /// <summary>
    /// The state a record moves to when the observed evidence is applied.
    ///
    /// - A record that points at an engine process which is no longer there is
    ///   <see cref="InstanceState.Stopped"/>: the guest is not running, whatever
    ///   the record said before.
    /// - A guest that reported boot completion is <see cref="InstanceState.Running"/>,
    ///   whatever the record said before. This is the transition that used to
    ///   require a waiter.
    /// - A record in <see cref="InstanceState.Failed"/> keeps its failure and its
    ///   reason; a record in <see cref="InstanceState.Stopping"/> is left to the
    ///   stop operation that owns it.
    /// - Anything else stays exactly as it was: no evidence is no transition.
    /// </summary>
    public static InstanceState Reconcile(InstanceState current, InstanceEvidence evidence)
    {
        if (evidence.EnginePidKnown
            && !evidence.EngineAlive
            && current is InstanceState.Running or InstanceState.Starting)
        {
            return InstanceState.Stopped;
        }

        if (evidence.BootCompleted
            && current is InstanceState.Starting or InstanceState.Defined or InstanceState.Unknown)
        {
            return InstanceState.Running;
        }

        return current;
    }

    /// <summary>The guest is up and Android reported boot completion.</summary>
    public static bool IsRunning(InstanceState state) => state == InstanceState.Running;

    /// <summary>
    /// The guest is between two states: the engine is coming up or going down.
    /// A transition is not a failure, and a surface must not offer a mutation
    /// that the supervisor would refuse.
    /// </summary>
    public static bool IsTransitioning(InstanceState state) =>
        state is InstanceState.Starting or InstanceState.Stopping;

    /// <summary>The guest can be started: no engine process is attached to it.</summary>
    public static bool CanStart(InstanceState state) =>
        state is InstanceState.Defined or InstanceState.Stopped or InstanceState.Failed;

    /// <summary>
    /// The guest can be stopped: an engine process is attached to it, so there is
    /// something to shut down (a guest that is still booting included).
    /// </summary>
    public static bool CanStop(InstanceState state) =>
        state is InstanceState.Starting or InstanceState.Running;
}
