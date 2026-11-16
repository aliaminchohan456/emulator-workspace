using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-18: the Magisk lab phase rules.
///
/// The lab journal reported "recovery required" for every phase that was not
/// prepared, verified or recovered, so a healthy install looked like a broken
/// lab and the desktop told the operator to recover stock while the install
/// was still running.
///
/// A phase word on its own proves nothing about work in progress: the journal
/// keeps the last phase it wrote, so a transaction whose supervisor died
/// still says "activating". The rules therefore take two facts besides the
/// phase: whether this supervisor is driving the transaction (it holds the
/// lab's lease while the operation runs) and whether the journal still moves.
/// </summary>
public class MagiskLabPhaseTests
{
    private const bool Driven = true;
    private const bool Fresh = true;
    private const bool NotDriven = false;
    private const bool Stale = false;

    [Fact]
    public void APreparedLab_NeedsNoRecoveryAndIsNotRunning()
    {
        // "prepared" is a stable state between two operator actions: the patch
        // is staged, the guest is still stock and nothing is running.
        Assert.False(MagiskLabPhase.RequiresRecovery("prepared", Driven, Fresh));
        Assert.False(MagiskLabPhase.InProgress("prepared", Driven, Fresh));
    }

    [Fact]
    public void AStagingLab_NeedsNoRecovery()
    {
        // Staging has not touched the guest yet, so even an interrupted
        // staging leaves a stock guest behind.
        Assert.False(MagiskLabPhase.RequiresRecovery("staging", Driven, Fresh));
        Assert.False(MagiskLabPhase.RequiresRecovery("staging", NotDriven, Stale));
        Assert.True(MagiskLabPhase.InProgress("staging", Driven, Fresh));
    }

    [Fact]
    public void ARunningInstall_NeedsNoRecovery()
    {
        // The transaction is doing exactly what it was asked to do.
        Assert.False(MagiskLabPhase.RequiresRecovery("activating", Driven, Fresh));
        Assert.False(MagiskLabPhase.RequiresRecovery("provisioned", Driven, Fresh));
        Assert.False(MagiskLabPhase.RequiresRecovery("verifying_boot_1", Driven, Fresh));
        Assert.True(MagiskLabPhase.InProgress("activating", Driven, Fresh));
        Assert.True(MagiskLabPhase.InProgress("verifying_boot_2", Driven, Fresh));
    }

    [Fact]
    public void AnInterruptedInstall_NeedsAStockRecovery()
    {
        // The guest already booted with the patched ramdisk and nobody is
        // driving the transaction any more.
        Assert.True(MagiskLabPhase.RequiresRecovery("activating", NotDriven, Fresh));
        Assert.True(MagiskLabPhase.RequiresRecovery("provisioned", NotDriven, Fresh));
        Assert.True(MagiskLabPhase.RequiresRecovery("verifying_boot_1", NotDriven, Fresh));
    }

    [Fact]
    public void AStuckTransaction_NeedsAStockRecovery()
    {
        // The supervisor still holds the transaction, but its journal stopped
        // moving: it is stuck, not working.
        Assert.True(MagiskLabPhase.RequiresRecovery("activating", Driven, Stale));
        Assert.True(MagiskLabPhase.RequiresRecovery("verifying_boot_1", Driven, Stale));
    }

    [Fact]
    public void AnInterruptedInstall_IsNotReportedAsRunning()
    {
        // The phase word survives the supervisor; the work does not. A window
        // that read the phase alone would wait for an install that is dead.
        Assert.False(MagiskLabPhase.InProgress("activating", NotDriven, Fresh));
        Assert.False(MagiskLabPhase.InProgress("provisioned", NotDriven, Fresh));
        Assert.False(MagiskLabPhase.InProgress("verifying_boot_2", NotDriven, Fresh));
    }

    [Fact]
    public void AStuckTransaction_IsNotReportedAsRunning()
    {
        Assert.False(MagiskLabPhase.InProgress("activating", Driven, Stale));
        Assert.False(MagiskLabPhase.InProgress("baseline_boot", Driven, Stale));
    }

    [Fact]
    public void AFailedTransaction_NeedsAStockRecovery()
    {
        Assert.True(MagiskLabPhase.RequiresRecovery("rollback_required", Driven, Fresh));
        Assert.True(MagiskLabPhase.RequiresRecovery("recovery_required", Driven, Fresh));
        Assert.False(MagiskLabPhase.InProgress("rollback_required", Driven, Fresh));
    }

    [Fact]
    public void ARecoveredOrVerifiedLab_NeedsNothing()
    {
        Assert.False(MagiskLabPhase.RequiresRecovery("verified", Driven, Fresh));
        Assert.False(MagiskLabPhase.RequiresRecovery("recovered", Driven, Fresh));
        Assert.False(MagiskLabPhase.InProgress("verified", Driven, Fresh));
        Assert.False(MagiskLabPhase.InProgress("recovered", Driven, Fresh));
    }

    [Fact]
    public void ARecoveringLab_IsRunningNotFailed()
    {
        Assert.False(MagiskLabPhase.RequiresRecovery("recovering", Driven, Fresh));
        Assert.True(MagiskLabPhase.InProgress("recovering", Driven, Fresh));
        Assert.True(MagiskLabPhase.InProgress("recovering_baseline", Driven, Fresh));
    }

    [Fact]
    public void TheStaleBudget_CoversTheLongestLegitimatePhase()
    {
        // A boot is bounded by the transaction's own boot timeout (five
        // minutes by default) plus the provisioning and the root probe, so the
        // budget leaves room for a slow host before a live transaction is
        // called stuck.
        Assert.True(MagiskLabPhase.StaleAfter > TimeSpan.FromMinutes(10));
    }
}
