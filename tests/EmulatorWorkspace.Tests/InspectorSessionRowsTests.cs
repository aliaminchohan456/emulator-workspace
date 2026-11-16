using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-06: the inspector's five session-only rows read from one run of one guest.
/// They report while the guest is Running or Starting and read "not reported"
/// (Boot: one dash) for every other lifecycle, even when a stale frame or a
/// readiness stamp is still in memory.
/// </summary>
public class InspectorSessionRowsTests
{
    public static TheoryData<DevicePhase?> SilentPhases => new()
    {
        null,
        DevicePhase.Stopped,
        DevicePhase.Stopping,
        DevicePhase.Failed,
        DevicePhase.EngineGone,
        DevicePhase.Unknown,
    };

    public static TheoryData<DevicePhase?> ReportedPhases => new()
    {
        DevicePhase.Running,
        DevicePhase.Starting,
    };

    [Theory]
    [MemberData(nameof(SilentPhases))]
    public void APhaseThatIsNotLive_ReportsNothing(DevicePhase? phase)
    {
        Assert.Equal("not reported", InspectorSessionRows.GuestDisplay(phase, 1080, 2400));
        Assert.Equal("not reported", InspectorSessionRows.DeliveredFrame(phase, hasFrame: true, 540, 1200));
        Assert.Equal("not reported", InspectorSessionRows.FrameRate(phase, hasFrame: true, 12.4));
        Assert.Equal("not reported", InspectorSessionRows.Dropped(phase, hasFrame: true, 8));
        Assert.Equal("—", InspectorSessionRows.Boot(phase, 12.3));
    }

    [Theory]
    [MemberData(nameof(ReportedPhases))]
    public void ALivePhase_ReportsTheRunItIsIn(DevicePhase? phase)
    {
        Assert.Equal("1080 × 2400", InspectorSessionRows.GuestDisplay(phase, 1080, 2400));
        Assert.Equal("540 × 1200", InspectorSessionRows.DeliveredFrame(phase, hasFrame: true, 540, 1200));
        Assert.Equal("12.4 fps", InspectorSessionRows.FrameRate(phase, hasFrame: true, 12.4));
        Assert.Equal("8", InspectorSessionRows.Dropped(phase, hasFrame: true, 8));
        Assert.Equal("12.3 s", InspectorSessionRows.Boot(phase, 12.3));
    }

    [Fact]
    public void ARunningGuestWithoutAFrame_ReportsNoFrameValues()
    {
        Assert.Equal("not reported", InspectorSessionRows.DeliveredFrame(DevicePhase.Running, hasFrame: false, 0, 0));
        Assert.Equal("not reported", InspectorSessionRows.FrameRate(DevicePhase.Running, hasFrame: false, 0));
        Assert.Equal("not reported", InspectorSessionRows.Dropped(DevicePhase.Running, hasFrame: false, 0));
        // The guest display comes from the frame's own metadata, so a guest
        // that has not delivered one has no resolution to report either.
        Assert.Equal("not reported", InspectorSessionRows.GuestDisplay(DevicePhase.Running, 0, 0));
    }

    [Fact]
    public void ARunningGuestWithoutAReadinessStamp_ShowsTheDash()
    {
        Assert.Equal("—", InspectorSessionRows.Boot(DevicePhase.Running, null));
        Assert.Equal("—", InspectorSessionRows.Boot(DevicePhase.Starting, null));
    }

    [Fact]
    public void TheEmptyBootValueIsOneEmDash()
    {
        Assert.Equal("—", InspectorSessionRows.NoMeasurement);
        Assert.Single(InspectorSessionRows.NoMeasurement);
    }

    [Fact]
    public void TheEmptyValueIsTheOneApprovedWording()
    {
        Assert.Equal("not reported", InspectorSessionRows.NotReported);
    }
}
