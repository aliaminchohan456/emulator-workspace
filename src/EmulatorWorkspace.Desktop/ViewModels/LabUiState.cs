namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-04: what the guest's Magisk lab journal holds.
///
/// The supervisor's status object carries the journal's phase word, and the
/// journal either exists or it does not. "not_configured" is the only phase
/// the supervisor reports without a journal; every other phase means a
/// transaction file is on disk and <c>magisk.prepare</c> will refuse to make a
/// second one. That is why the install command needs this value and not just
/// the phase word.
/// </summary>
public enum LabJournalState
{
    /// <summary>No journal: the guest never had a lab transaction.</summary>
    None,

    /// <summary>The patch is staged and the guest is still stock.</summary>
    Prepared,

    /// <summary>A transaction is on disk and has not reached a final phase.</summary>
    Open,

    /// <summary>The journal says the install was verified: the guest boots Magisk.</summary>
    Installed,

    /// <summary>The journal says the stock recovery finished.</summary>
    Recovered,
}

/// <summary>
/// EV-04: the one lab reading every surface speaks.
///
/// <see cref="Label"/> is the operator's word; <see cref="RawPhase"/> keeps the
/// supervisor's phase word for the tooltip only. Filed issue 1: the inspector
/// row, the rail dot and the stage used to print the raw word ("verifying_boot_1",
/// "baseline_boot") where a human label belongs.
/// </summary>
public enum LabUiPhase
{
    /// <summary>No status read yet, or the read failed.</summary>
    Unknown,

    /// <summary>No journal: the lab is stock and never configured.</summary>
    NotConfigured,

    /// <summary>The patch is staged; nothing is running.</summary>
    Prepared,

    /// <summary>The supervisor is driving the install now.</summary>
    Installing,

    /// <summary>The install was verified: the guest boots Magisk.</summary>
    Installed,

    /// <summary>The stock recovery finished.</summary>
    Recovered,

    /// <summary>A recovery is due; agents are fenced until stock is restored.</summary>
    RecoveryRequired,

    /// <summary>
    /// A transaction is on disk but the supervisor is not driving it: the work
    /// stopped where the journal left it.
    /// </summary>
    Interrupted,
}

/// <summary>
/// EV-04: one lab state for the inspector row, the rail dot, the stage, the
/// two lab commands and their tooltips.
/// </summary>
/// <remarks>
/// The supervisor already classifies the phase (<c>recoveryRequired</c>,
/// <c>installInProgress</c>) from whether it is driving the transaction and
/// whether the journal still moves. This type adds the two facts a command
/// needs - a journal exists, and the guest is no longer stock - and one human
/// label. It decides nothing about the guest's boot image; it only reads what
/// the supervisor reported.
/// </remarks>
public sealed record LabUiState(
    LabUiPhase Phase,
    string Label,
    string RawPhase,
    bool InstallInProgress,
    bool RequiresRecovery,
    LabJournalState Journal)
{
    /// <summary>The initial reading, before the first <c>magisk.status</c> answer.</summary>
    public static LabUiState NotChecked { get; } =
        From("not checked", installInProgress: false, recoveryRequired: false, LabJournalState.None);

    /// <summary>
    /// The supervisor's own sentence, raw phase word included. This is the one
    /// place the raw word is allowed to be read, and every surface that shows
    /// it is a tooltip.
    /// </summary>
    public string Tooltip => "Magisk lab: " + RawPhase
        + (RequiresRecovery
            ? " (recovery required)"
            : InstallInProgress ? " (install in progress)" : "");

    /// <summary>
    /// A journal on disk makes <c>magisk.prepare</c> refuse ("This instance
    /// already has a lab journal"), so install is offered only when there is
    /// none. EV-03 deferred this: a prepared or recovered journal kept the
    /// command enabled even though the supervisor would refuse it.
    /// </summary>
    public bool CanInstall => Phase is LabUiPhase.NotConfigured or LabUiPhase.Unknown;

    /// <summary>The operator's reason while <see cref="CanInstall"/> is false.</summary>
    public string? InstallDisabledReason => Phase switch
    {
        LabUiPhase.Installing => "Lab install in progress",
        LabUiPhase.Prepared => "Lab prepared; the patch is already staged",
        LabUiPhase.Installed => "Lab already installed",
        LabUiPhase.Recovered => "Lab already recovered",
        LabUiPhase.RecoveryRequired => "Lab recovery required",
        LabUiPhase.Interrupted => "Lab journal exists; finish or recover it first",
        _ => null,
    };

    /// <summary>
    /// The guest is no longer stock: the patched ramdisk booted, a lab
    /// operation is running, or recovery is due. A prepared journal is still
    /// stock (the patch is staged, the guest is untouched) and a recovered
    /// journal is stock again.
    /// </summary>
    public bool IsNonStock => Phase is LabUiPhase.Installing or LabUiPhase.Installed
        or LabUiPhase.RecoveryRequired or LabUiPhase.Interrupted;

    /// <summary>
    /// Recovery restores the stock boot image. It is not offered while the
    /// supervisor is driving an install (the two would race), and it is offered
    /// whenever recovery is due or the guest is no longer stock.
    /// </summary>
    public bool CanRecover => !InstallInProgress && (RequiresRecovery || IsNonStock);

    /// <summary>The operator's reason while <see cref="CanRecover"/> is false.</summary>
    public string? RecoverDisabledReason => CanRecover
        ? null
        : InstallInProgress ? "Lab install in progress" : "Lab is stock";

    /// <summary>
    /// The stage's detail while a recovery is due. Filed issue 1: it used to
    /// quote the raw phase word; the word now lives in <see cref="Tooltip"/>.
    /// </summary>
    public string RecoveryStageDetail =>
        "The Magisk lab transaction needs a stock recovery. Use Recover stock in Root Lab before starting or changing this guest.";

    /// <summary>The stage's detail while the install is running.</summary>
    public string InstallStageDetail => "Magisk lab: " + Label.ToLowerInvariant();

    /// <summary>
    /// Classifies the supervisor's reading.
    /// </summary>
    /// <param name="phase">The journal's phase word, as <c>magisk.status</c> reports it.</param>
    /// <param name="installInProgress">The supervisor is driving the install now.</param>
    /// <param name="recoveryRequired">A stock recovery is really due.</param>
    /// <param name="journal">What the journal holds, from <see cref="JournalFor"/>.</param>
    public static LabUiState From(
        string? phase,
        bool installInProgress,
        bool recoveryRequired,
        LabJournalState journal)
    {
        var raw = (phase ?? "").Trim();
        var word = raw.ToLowerInvariant();

        LabUiPhase classified;
        if (recoveryRequired)
        {
            classified = LabUiPhase.RecoveryRequired;
        }
        else if (installInProgress)
        {
            classified = LabUiPhase.Installing;
        }
        else if (word is "prepared" or "staging" || journal == LabJournalState.Prepared)
        {
            classified = LabUiPhase.Prepared;
        }
        else if (word is "verified" || journal == LabJournalState.Installed)
        {
            classified = LabUiPhase.Installed;
        }
        else if (word is "recovered" || journal == LabJournalState.Recovered)
        {
            classified = LabUiPhase.Recovered;
        }
        else if (word is "not_configured" or "" && journal == LabJournalState.None)
        {
            classified = LabUiPhase.NotConfigured;
        }
        else
        {
            classified = journal == LabJournalState.None
                ? LabUiPhase.Unknown
                : LabUiPhase.Interrupted;
        }

        return new LabUiState(
            classified,
            LabelFor(classified, raw),
            raw,
            installInProgress,
            recoveryRequired,
            journal);
    }

    /// <summary>
    /// What the journal holds, from the phase word and whether the status
    /// object carries a transaction at all.
    /// </summary>
    public static LabJournalState JournalFor(string? phase, bool hasTransaction)
    {
        if (!hasTransaction)
        {
            return LabJournalState.None;
        }

        return (phase ?? "").Trim().ToLowerInvariant() switch
        {
            "prepared" => LabJournalState.Prepared,
            "verified" => LabJournalState.Installed,
            "recovered" => LabJournalState.Recovered,
            _ => LabJournalState.Open,
        };
    }

    private static string LabelFor(LabUiPhase phase, string raw) => phase switch
    {
        LabUiPhase.NotConfigured => "Not configured",
        LabUiPhase.Prepared => "Prepared",
        LabUiPhase.Installing => "Install in progress",
        LabUiPhase.Installed => "Installed",
        LabUiPhase.Recovered => "Recovered",
        LabUiPhase.RecoveryRequired => "Stock recovery required",
        _ => Humanize(raw),
    };

    /// <summary>
    /// The old display transform for any phase that is not classified above:
    /// "not checked" reads "Not checked", "baseline_boot" reads "Baseline boot".
    /// </summary>
    private static string Humanize(string raw)
    {
        if (raw.Length == 0)
        {
            return "Unknown";
        }

        var text = raw.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
