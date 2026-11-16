using EmulatorWorkspace.Core.Instances;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-18: the guest that outlived its engine process.
///
/// The engine is a wrapper: when it dies, the guest it started can survive it
/// and keep the instance's reserved ports. The supervisor may only touch that
/// guest when the console says it belongs to this instance.
/// </summary>
public class OrphanedGuestTests
{
    [Fact]
    public void TheConsoleName_IsRead()
    {
        Assert.Equal("ws_ux18", OrphanedGuest.ReadConsoleAvdName("ws_ux18\n\nOK\n"));
    }

    [Fact]
    public void ADiscoveryPath_IsNotAName()
    {
        Assert.Null(OrphanedGuest.ReadConsoleAvdName(
            "C:\\Users\\someone\\AppData\\Local\\Temp\\avd\\running\\pid_21444.ini\n\nOK\n"));
    }

    [Fact]
    public void AnEmptyAnswer_HasNoName()
    {
        Assert.Null(OrphanedGuest.ReadConsoleAvdName(""));
        Assert.Null(OrphanedGuest.ReadConsoleAvdName(null));
        Assert.Null(OrphanedGuest.ReadConsoleAvdName("OK\n"));
    }

    [Fact]
    public void TheSameAvd_IsThisInstancesGuest()
    {
        Assert.True(OrphanedGuest.BelongsTo("ws_ux18", "ws_ux18"));
        Assert.True(OrphanedGuest.BelongsTo("WS_UX18", "ws_ux18"));
        Assert.True(OrphanedGuest.BelongsTo(" ws_ux18 ", "ws_ux18"));
    }

    [Fact]
    public void AnotherAvd_IsNotThisInstancesGuest()
    {
        // Somebody else's emulator on the same serial must never be touched.
        Assert.False(OrphanedGuest.BelongsTo("ws_other", "ws_ux18"));
        Assert.False(OrphanedGuest.BelongsTo("Pixel_6_API_35", "ws_ux18"));
    }

    [Fact]
    public void NoName_IsNotThisInstancesGuest()
    {
        Assert.False(OrphanedGuest.BelongsTo(null, "ws_ux18"));
        Assert.False(OrphanedGuest.BelongsTo("ws_ux18", null));
        Assert.False(OrphanedGuest.BelongsTo("  ", "ws_ux18"));
    }

    [Fact]
    public void ThePortConflictMessage_NamesTheInstanceAndTheCommand()
    {
        var message = OrphanedGuest.PortConflictMessage("ux18", "emulator-5554", 5554);

        Assert.Contains("ux18", message, StringComparison.Ordinal);
        Assert.Contains("emulator-5554", message, StringComparison.Ordinal);
        Assert.Contains("5554", message, StringComparison.Ordinal);
        Assert.Contains("wsctl stop --name ux18 --force", message, StringComparison.Ordinal);
    }
}
