namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// The Magisk lab transaction's phases, and what each one means for the guest
/// that carries the journal.
/// </summary>
/// <remarks>
/// The lab journal used to report "recovery required" for every phase that was
/// not "prepared", "verified" or "recovered". That made a healthy, successful
/// install look like a broken lab: the desktop showed "Lab recovery required"
/// through the whole transaction, and the operator was told to recover stock
/// while the install was still doing exactly what it was asked to do.
///
/// The phases are therefore classified here, in one place: a phase that has
/// failed needs a stock recovery, a phase that is still being driven is
/// running, and a phase that already booted the guest with the patched
/// ramdisk and is no longer being driven is an interrupted transaction that
/// does need a stock recovery.
///
/// A phase word on its own proves nothing about work in progress: the
/// journal keeps the last phase it wrote, so a transaction whose supervisor
/// died still says "activating". Two facts decide it instead:
/// <list type="bullet">
/// <item><description><c>driven</c> - this supervisor is driving the
/// transaction now (it holds the lab's lease while the operation runs).</description></item>
/// <item><description><c>fresh</c> - the journal still moves.</description></item>
/// </list>
/// </remarks>
public static class MagiskLabPhase
{
    /// <summary>
    /// A transaction whose journal has not moved for this long is not making
    /// progress any more: it is stuck, whoever is holding it.
    ///
    /// The longest legitimate phase is a boot bounded by the transaction's
    /// own boot timeout (five minutes by default) plus the provisioning and
    /// the root probe, so this leaves room for a slow host.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The guest needs a stock recovery before it is used again: the
    /// transaction failed, or it was interrupted after the guest had already
    /// booted with the patched ramdisk.
    /// </summary>
    /// <param name="phase">The phase the journal records.</param>
    /// <param name="driven">
    /// This supervisor is running the transaction right now.
    /// </param>
    /// <param name="fresh">The journal is still moving.</param>
    public static bool RequiresRecovery(string? phase, bool driven, bool fresh)
    {
        var value = Normalize(phase);
        if (value is "rollback_required" or "recovery_required")
        {
            return true;
        }

        if (!TouchesPatchedGuest(value))
        {
            return false;
        }

        // The guest has the patched ramdisk and nobody is driving the
        // transaction any more - or the journal stopped moving: the install
        // was interrupted and the guest's state is unknown until it is
        // recovered to stock.
        return !driven || !fresh;
    }

    /// <summary>
    /// The transaction is still running: it has a journal, it is between two
    /// steps, it has not failed and this supervisor is driving it.
    ///
    /// "prepared" is deliberately not one of them. That phase is a stable state
    /// between two operator actions - the patch is staged and the guest is
    /// still stock - so nothing is running, and a surface that said "install in
    /// progress" would be claiming work that is not happening.
    /// </summary>
    public static bool InProgress(string? phase, bool driven, bool fresh)
    {
        var value = Normalize(phase);
        if (!IsRunningPhase(value))
        {
            return false;
        }

        // A phase word alone is not proof of work: the transaction runs only
        // while the supervisor that owns it is driving it and its journal is
        // still moving.
        return driven && fresh;
    }

    /// <summary>
    /// The phases in which the transaction is between two steps.
    /// </summary>
    private static bool IsRunningPhase(string phase) =>
        phase is "staging" or "baseline_boot" or "activating" or "provisioned"
            or "recovering_baseline" or "recovering"
            || phase.StartsWith("verifying_boot_", StringComparison.Ordinal);

    /// <summary>
    /// The phases in which the guest has already booted with the patched
    /// ramdisk. A transaction interrupted in one of them leaves the guest in a
    /// state that only a stock recovery can vouch for.
    /// </summary>
    private static bool TouchesPatchedGuest(string phase) =>
        phase is "activating" or "provisioned"
        || phase.StartsWith("verifying_boot_", StringComparison.Ordinal);

    private static string Normalize(string? phase) => (phase ?? "").Trim().ToLowerInvariant();
}
