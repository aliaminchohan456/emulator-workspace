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
/// </summary>
public class MagiskLabPhaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APreparedLab_NeedsNoRecoveryAndIsNotRunning()
    {
        // "prepared" is a stable state between two operator actions: the patch
        // is staged, the guest is still stock and nothing is running.
        Assert.False(MagiskLabPhase.RequiresRecovery("prepared", Now, Now));
        Assert.False(MagiskLabPhase.InProgress("prepared"));
    }

    [Fact]
    public void AStagingLab_NeedsNoRecovery()
    {
        Assert.False(MagiskLabPhase.RequiresRecovery("staging", Now, Now));
        Assert.True(MagiskLabPhase.InProgress("staging"));
    }

    [Fact]
    public void ARunningInstall_NeedsNoRecovery()
    {
        // The transaction is doing exactly what it was asked to do.
        Assert.False(MagiskLabPhase.RequiresRecovery("activating", Now, Now));
        Assert.False(MagiskLabPhase.RequiresRecovery("provisioned", Now, Now));
        Assert.False(MagiskLabPhase.RequiresRecovery("verifying_boot_1", Now, Now));
        Assert.True(MagiskLabPhase.InProgress("activating"));
        Assert.True(MagiskLabPhase.InProgress("verifying_boot_2"));
    }

    [Fact]
    public void AnInterruptedInstall_NeedsAStockRecovery()
    {
        // The guest already booted with the patched ramdisk and the transaction
        // has not moved for a long time.
        var stale = Now - MagiskLabPhase.StaleAfter - TimeSpan.FromMinutes(1);

        Assert.True(MagiskLabPhase.RequiresRecovery("activating", stale, Now));
        Assert.True(MagiskLabPhase.RequiresRecovery("provisioned", stale, Now));
        Assert.True(MagiskLabPhase.RequiresRecovery("verifying_boot_1", stale, Now));
    }

    [Fact]
    public void AFailedTransaction_NeedsAStockRecovery()
    {
        Assert.True(MagiskLabPhase.RequiresRecovery("rollback_required", Now, Now));
        Assert.True(MagiskLabPhase.RequiresRecovery("recovery_required", Now, Now));
    }

    [Fact]
    public void ARecoveredOrVerifiedLab_NeedsNothing()
    {
        Assert.False(MagiskLabPhase.RequiresRecovery("verified", Now, Now));
        Assert.False(MagiskLabPhase.RequiresRecovery("recovered", Now, Now));
        Assert.False(MagiskLabPhase.InProgress("verified"));
        Assert.False(MagiskLabPhase.InProgress("recovered"));
    }

    [Fact]
    public void ARecoveringLab_IsRunningNotFailed()
    {
        Assert.False(MagiskLabPhase.RequiresRecovery("recovering", Now, Now));
        Assert.True(MagiskLabPhase.InProgress("recovering"));
        Assert.True(MagiskLabPhase.InProgress("recovering_baseline"));
    }
}
