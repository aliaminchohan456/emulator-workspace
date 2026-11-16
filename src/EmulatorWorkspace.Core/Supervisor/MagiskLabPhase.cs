namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// The Magisk lab transaction's phases, and what each one means for the guest
/// that carries the journal.
///
/// The lab journal used to report "recovery required" for every phase that was
/// not "prepared", "verified" or "recovered". That made a healthy, successful
/// install look like a broken lab: the desktop showed "Lab recovery required"
/// through the whole transaction, and the operator was told to recover stock
/// while the install was still doing exactly what it was asked to do.
///
/// The phases are therefore classified here, in one place: a phase that has
/// failed needs a stock recovery, a phase that is still running is simply
/// running, and a phase that has already booted the guest with the patched
/// ramdisk and then went quiet is an interrupted transaction that does need a
/// stock recovery.
/// </summary>
public static class MagiskLabPhase
{
    /// <summary>
    /// A transaction whose journal has not moved for this long is not running
    /// any more: the supervisor was interrupted in the middle of it.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The guest needs a stock recovery before it is used again: the
    /// transaction failed, or it was interrupted after the guest had already
    /// booted with the patched ramdisk.
    /// </summary>
    public static bool RequiresRecovery(string? phase, DateTimeOffset? updatedUtc, DateTimeOffset now)
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

        // The guest has the patched ramdisk and the transaction has not moved
        // for a long time: the install was interrupted and the guest's state
        // is unknown until it is recovered to stock.
        return updatedUtc is null || now - updatedUtc.Value > StaleAfter;
    }

    /// <summary>
    /// The transaction is still running: it has a journal, it is between two
    /// steps and it has not failed.
    ///
    /// "prepared" is deliberately not one of them. That phase is a stable state
    /// between two operator actions - the patch is staged and the guest is
    /// still stock - so nothing is running, and a surface that said "install in
    /// progress" would be claiming work that is not happening.
    /// </summary>
    public static bool InProgress(string? phase)
    {
        var value = Normalize(phase);
        return value is "staging" or "baseline_boot" or "activating" or "provisioned"
            or "recovering_baseline" or "recovering"
            || value.StartsWith("verifying_boot_", StringComparison.Ordinal);
    }

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
