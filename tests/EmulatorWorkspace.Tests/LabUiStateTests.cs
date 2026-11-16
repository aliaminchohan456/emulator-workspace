using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-04: the Magisk lab's one reading.
///
/// The supervisor's status object carries a phase word, two flags it derives
/// from whether it is driving the transaction, and (implicitly) whether a
/// journal exists. Every surface and both lab commands used to interpret those
/// four facts on their own; these tests pin the one interpretation.
/// </summary>
public class LabUiStateTests
{
    [Fact]
    public void AnEmptyJournal_IsNotConfiguredAndOffersAnInstall()
    {
        var lab = LabUiState.From("not_configured", false, false, LabJournalState.None);

        Assert.Equal(LabUiPhase.NotConfigured, lab.Phase);
        Assert.Equal("Not configured", lab.Label);
        Assert.Equal("Magisk lab: not_configured", lab.Tooltip);
        Assert.True(lab.CanInstall);
        Assert.Null(lab.InstallDisabledReason);
        Assert.False(lab.CanRecover);
        Assert.Equal("Lab is stock", lab.RecoverDisabledReason);
        Assert.False(lab.IsNonStock);
        Assert.Equal(LabJournalState.None, lab.Journal);
    }

    [Fact]
    public void APreparedJournal_DisablesInstallEvenThoughTheGuestIsStillStock()
    {
        // EV-03 deferred this: magisk.prepare refuses a second journal, but the
        // command stayed enabled because nothing read the journal.
        var lab = LabUiState.From("prepared", false, false, LabJournalState.Prepared);

        Assert.Equal(LabUiPhase.Prepared, lab.Phase);
        Assert.Equal("Prepared", lab.Label);
        Assert.Equal("Magisk lab: prepared", lab.Tooltip);
        Assert.False(lab.CanInstall);
        Assert.Equal("Lab prepared; the patch is already staged", lab.InstallDisabledReason);
        Assert.False(lab.IsNonStock);
        Assert.False(lab.CanRecover);
        Assert.Equal("Lab is stock", lab.RecoverDisabledReason);
    }

    [Fact]
    public void ARecoveredJournal_AlsoDisablesInstall()
    {
        // A recovered journal stays on disk; magisk.prepare would refuse it.
        var lab = LabUiState.From("recovered", false, false, LabJournalState.Recovered);

        Assert.Equal(LabUiPhase.Recovered, lab.Phase);
        Assert.Equal("Recovered", lab.Label);
        Assert.False(lab.CanInstall);
        Assert.Equal("Lab already recovered", lab.InstallDisabledReason);
        Assert.False(lab.IsNonStock);
        Assert.False(lab.CanRecover);
    }

    [Fact]
    public void ADrivenInstall_IsInProgressAndBlocksBothCommands()
    {
        var lab = LabUiState.From("activating", installInProgress: true, recoveryRequired: false, LabJournalState.Open);

        Assert.Equal(LabUiPhase.Installing, lab.Phase);
        Assert.Equal("Install in progress", lab.Label);
        Assert.Equal("Magisk lab: activating (install in progress)", lab.Tooltip);
        Assert.True(lab.InstallInProgress);
        Assert.False(lab.CanInstall);
        Assert.Equal("Lab install in progress", lab.InstallDisabledReason);
        Assert.False(lab.CanRecover);
        Assert.Equal("Lab install in progress", lab.RecoverDisabledReason);
        Assert.True(lab.IsNonStock);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("baseline_boot")]
    [InlineData("activating")]
    [InlineData("provisioned")]
    [InlineData("recovering_baseline")]
    [InlineData("verifying_boot_1")]
    [InlineData("verifying_boot_2")]
    public void ADrivenTransaction_IsInstallInProgressWhateverItsStep(string phase)
    {
        var lab = LabUiState.From(phase, installInProgress: true, recoveryRequired: false, LabJournalState.Open);

        Assert.Equal(LabUiPhase.Installing, lab.Phase);
        Assert.Equal("Install in progress", lab.Label);
        Assert.EndsWith("(install in progress)", lab.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerifiedJournal_ReadsInstalledNotTheRawPhaseWord()
    {
        // Filed issue 1: "verified" is a phase word; "Installed" is the label.
        var lab = LabUiState.From("verified", false, false, LabJournalState.Installed);

        Assert.Equal(LabUiPhase.Installed, lab.Phase);
        Assert.Equal("Installed", lab.Label);
        Assert.NotEqual("Verified", lab.Label);
        Assert.Equal("Magisk lab: verified", lab.Tooltip);
        Assert.False(lab.CanInstall);
        Assert.Equal("Lab already installed", lab.InstallDisabledReason);
        Assert.True(lab.IsNonStock);
        Assert.True(lab.CanRecover);
    }

    [Fact]
    public void ARecoveryThatIsDue_OutranksEveryOtherReading()
    {
        var lab = LabUiState.From("provisioned", installInProgress: false, recoveryRequired: true, LabJournalState.Open);

        Assert.Equal(LabUiPhase.RecoveryRequired, lab.Phase);
        Assert.Equal("Stock recovery required", lab.Label);
        Assert.Equal("Magisk lab: provisioned (recovery required)", lab.Tooltip);
        Assert.True(lab.RequiresRecovery);
        Assert.False(lab.CanInstall);
        Assert.Equal("Lab recovery required", lab.InstallDisabledReason);
        Assert.True(lab.CanRecover);
        Assert.True(lab.IsNonStock);
    }

    [Fact]
    public void AFailedTransaction_NeedsStockRecovery()
    {
        var lab = LabUiState.From("rollback_required", false, true, LabJournalState.Open);

        Assert.Equal(LabUiPhase.RecoveryRequired, lab.Phase);
        Assert.Equal("Stock recovery required", lab.Label);
        Assert.True(lab.CanRecover);
    }

    [Fact]
    public void AJournalNobodyIsDriving_IsInterruptedAndStillRecoverable()
    {
        // The supervisor died during baseline_boot: nothing is running, the
        // journal exists, and the only way out is recovery.
        var lab = LabUiState.From("baseline_boot", false, false, LabJournalState.Open);

        Assert.Equal(LabUiPhase.Interrupted, lab.Phase);
        Assert.Equal("Baseline boot", lab.Label);
        Assert.False(lab.CanInstall);
        Assert.Equal("Lab journal exists; finish or recover it first", lab.InstallDisabledReason);
        Assert.True(lab.CanRecover);
        Assert.True(lab.IsNonStock);
    }

    [Theory]
    [InlineData("baseline_boot")]
    [InlineData("someday_phase")]
    public void AnOpenJournalWithoutAFlag_KeepsInstallOff(string phase)
    {
        var lab = LabUiState.From(phase, false, false, LabJournalState.Open);

        Assert.Equal(LabUiPhase.Interrupted, lab.Phase);
        Assert.False(lab.CanInstall);
    }

    [Fact]
    public void AnUnreadLab_OffersInstallAndDoesNotClaimRecovery()
    {
        var lab = LabUiState.NotChecked;

        Assert.Equal(LabUiPhase.Unknown, lab.Phase);
        Assert.Equal("Not checked", lab.Label);
        Assert.Equal("Magisk lab: not checked", lab.Tooltip);
        Assert.True(lab.CanInstall);
        Assert.False(lab.CanRecover);
        Assert.False(lab.IsNonStock);
    }

    [Fact]
    public void AStatusReadThatFailed_KeepsTheSupervisorsSentenceInTheTooltip()
    {
        var lab = LabUiState.From("supervisor is not connected", false, false, LabJournalState.None);

        Assert.Equal(LabUiPhase.Unknown, lab.Phase);
        Assert.Equal("Supervisor is not connected", lab.Label);
        Assert.Equal("Magisk lab: supervisor is not connected", lab.Tooltip);
        Assert.True(lab.CanInstall);
        Assert.False(lab.CanRecover);
    }

    [Fact]
    public void ARawPhaseWordNeverReachesTheLabel()
    {
        // Every classified phase prints a word a person wrote, not the
        // supervisor's key.
        foreach (var lab in new[]
        {
            LabUiState.From("not_configured", false, false, LabJournalState.None),
            LabUiState.From("prepared", false, false, LabJournalState.Prepared),
            LabUiState.From("activating", true, false, LabJournalState.Open),
            LabUiState.From("verified", false, false, LabJournalState.Installed),
            LabUiState.From("recovered", false, false, LabJournalState.Recovered),
            LabUiState.From("provisioned", false, true, LabJournalState.Open),
        })
        {
            Assert.DoesNotContain('_', lab.Label);
        }
    }

    [Theory]
    [InlineData("not_configured", false, LabJournalState.None)]
    [InlineData("prepared", true, LabJournalState.Prepared)]
    [InlineData("activating", true, LabJournalState.Open)]
    [InlineData("verified", true, LabJournalState.Installed)]
    [InlineData("recovered", true, LabJournalState.Recovered)]
    public void TheJournalIsReadFromThePhaseAndTheTransaction(
        string phase,
        bool hasTransaction,
        LabJournalState expected)
    {
        Assert.Equal(expected, LabUiState.JournalFor(phase, hasTransaction));
    }

    [Fact]
    public void NoTransactionMeansNoJournal_WhateverTheWord()
    {
        Assert.Equal(LabJournalState.None, LabUiState.JournalFor("not_configured", hasTransaction: false));
        Assert.Equal(LabJournalState.None, LabUiState.JournalFor("", hasTransaction: false));
    }

    [Fact]
    public void AnEmptyPhaseWithNoJournal_IsNotConfigured()
    {
        var lab = LabUiState.From("", false, false, LabJournalState.None);

        Assert.Equal(LabUiPhase.NotConfigured, lab.Phase);
        Assert.Equal("Not configured", lab.Label);
        Assert.True(lab.CanInstall);
    }

    [Fact]
    public void TheStageDetailsAreTheHumanOnes()
    {
        var installing = LabUiState.From("activating", true, false, LabJournalState.Open);
        var recovery = LabUiState.From("activating", false, true, LabJournalState.Open);

        Assert.Equal("Magisk lab: install in progress", installing.InstallStageDetail);
        Assert.Equal(
            "The Magisk lab transaction needs a stock recovery. Use Recover stock in Root Lab before starting or changing this guest.",
            recovery.RecoveryStageDetail);
        Assert.DoesNotContain("activating", recovery.RecoveryStageDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryIsNeverOfferedWhileATransactionIsDriven()
    {
        // The recovery would race the install it is trying to undo.
        var lab = LabUiState.From("activating", installInProgress: true, recoveryRequired: true, LabJournalState.Open);

        Assert.True(lab.RequiresRecovery);
        Assert.True(lab.InstallInProgress);
        Assert.False(lab.CanRecover);
    }

    [Fact]
    public void EveryLabPhaseHasADecision()
    {
        // Guards the enum: a new phase must be classified on purpose, not fall
        // through to an accident of ordering.
        var covered = new[]
        {
            LabUiState.NotChecked.Phase,
            LabUiState.From("not_configured", false, false, LabJournalState.None).Phase,
            LabUiState.From("prepared", false, false, LabJournalState.Prepared).Phase,
            LabUiState.From("activating", true, false, LabJournalState.Open).Phase,
            LabUiState.From("verified", false, false, LabJournalState.Installed).Phase,
            LabUiState.From("recovered", false, false, LabJournalState.Recovered).Phase,
            LabUiState.From("provisioned", false, true, LabJournalState.Open).Phase,
            LabUiState.From("baseline_boot", false, false, LabJournalState.Open).Phase,
        };

        Assert.Equal(Enum.GetValues<LabUiPhase>().Length, covered.Distinct().Count());
    }
}
