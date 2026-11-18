using System.Diagnostics;
using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-12: the capture follows the viewport. These cases pin the shape rules
/// (aspect, even sizes, the guest cap, the 360 px floor), the 2 % resubscribe
/// threshold and the 300 ms burst coalescing without a window, a guest or a
/// display-scaling change.
/// </summary>
public sealed class CaptureSizePolicyTests
{
    private const int GuestWidth = 1080;
    private const int GuestHeight = 2400;

    [Fact]
    public void Fit_KeepsTheGuestAspectInsideTheViewport()
    {
        // 702 x 576 DIP of usable viewport inside a portrait guest: the height
        // is the limiting side, and 360 x 800 carries the same 0.45 ratio.
        var size = CaptureSizePolicy.ForViewport(
            734, 608, 1.0, 1.0, GuestWidth, GuestHeight);

        Assert.Equal(new CaptureSize(360, 800), size);
        Assert.Equal(
            GuestWidth / (double)GuestHeight,
            size.Width / (double)size.Height,
            2);
    }

    [Fact]
    public void Fit_OfALandscapeGuestStaysLandscape()
    {
        var size = CaptureSizePolicy.ForViewport(
            734, 608, 1.0, 1.0, 1920, 1080);

        Assert.Equal(new CaptureSize(702, 394), size);
        Assert.True(size.Width > size.Height);
    }

    [Fact]
    public void EverySizeIsEven()
    {
        // 453.6 x 1008 and 259.2 x 576 both round down to an even row.
        var wide = CaptureSizePolicy.ForViewport(734, 608, 1.75, 1.75, GuestWidth, GuestHeight);
        var narrow = CaptureSizePolicy.ForViewport(500, 500, 1.0, 1.0, 1920, 1080);

        Assert.Equal(0, wide.Width % CaptureSizePolicy.EvenMultiple);
        Assert.Equal(0, wide.Height % CaptureSizePolicy.EvenMultiple);
        Assert.Equal(0, narrow.Width % CaptureSizePolicy.EvenMultiple);
        Assert.Equal(0, narrow.Height % CaptureSizePolicy.EvenMultiple);
    }

    [Fact]
    public void TheShortSideNeverFallsBelowTheFloor()
    {
        // A 100 x 200 DIP viewport would fit a 68 x 151 capture; the floor lifts
        // it to 360 x 800 instead of asking for a postage stamp.
        var size = CaptureSizePolicy.ForViewport(100, 200, 1.0, 1.0, GuestWidth, GuestHeight);

        Assert.Equal(new CaptureSize(360, 800), size);
        Assert.True(Math.Min(size.Width, size.Height) >= CaptureSizePolicy.MinimumShortSide);
    }

    [Fact]
    public void TheGuestResolutionCapsTheSize()
    {
        // 1:1 on a guest smaller than the floor: the guest wins over the floor.
        Assert.Equal(
            new CaptureSize(320, 640),
            CaptureSizePolicy.ForViewport(2000, 2000, 1.0, 1.0, 320, 640, "Actual"));

        // A viewport larger than the guest never asks for more than the guest.
        Assert.Equal(
            new CaptureSize(320, 640),
            CaptureSizePolicy.ForViewport(2000, 2000, 2.0, 2.0, 320, 640));
    }

    [Theory]
    [InlineData(1.00, 360, 800)]
    [InlineData(1.25, 360, 800)]
    [InlineData(1.50, 388, 864)]
    [InlineData(1.75, 452, 1008)]
    public void ScalingFollowsTheDevicePixels(double dpi, int width, int height)
    {
        var size = CaptureSizePolicy.ForViewport(734, 608, dpi, dpi, GuestWidth, GuestHeight);

        Assert.Equal(new CaptureSize(width, height), size);
    }

    [Fact]
    public void ActualAndZoomDriveTheSizeLikeTheToolbarButtons()
    {
        Assert.Equal(
            new CaptureSize(1080, 2400),
            CaptureSizePolicy.ForViewport(734, 608, 1.25, 1.25, GuestWidth, GuestHeight, "Actual"));

        Assert.Equal(
            new CaptureSize(540, 1200),
            CaptureSizePolicy.ForViewport(734, 608, 1.25, 1.25, GuestWidth, GuestHeight, "Zoom", 0.5));

        // Zooming in cannot add pixels the guest does not have.
        Assert.Equal(
            new CaptureSize(1080, 2400),
            CaptureSizePolicy.ForViewport(734, 608, 1.25, 1.25, GuestWidth, GuestHeight, "Zoom", 4.0));
    }

    [Fact]
    public void AnUnmeasuredViewportAsksForNothing()
    {
        Assert.True(CaptureSizePolicy.ForViewport(0, 0, 1.25, 1.25, GuestWidth, GuestHeight).IsEmpty);
        Assert.True(CaptureSizePolicy.ForViewport(32, 608, 1.25, 1.25, GuestWidth, GuestHeight).IsEmpty);
        Assert.True(CaptureSizePolicy.ForViewport(double.NaN, 608, 1.25, 1.25, GuestWidth, GuestHeight).IsEmpty);
    }

    [Fact]
    public void WithoutAKnownGuestThePortraitDefaultIsUsed()
    {
        Assert.Equal(
            CaptureSizePolicy.ForViewport(734, 608, 1.25, 1.25, GuestWidth, GuestHeight),
            CaptureSizePolicy.ForViewport(734, 608, 1.25, 1.25, 0, 0));
    }

    [Fact]
    public void TheThresholdIsMoreThanTwoPercentInEitherDimension()
    {
        Assert.False(CaptureSizePolicy.IsSignificantChange(360, 800, new CaptureSize(367, 800)));
        Assert.False(CaptureSizePolicy.IsSignificantChange(360, 800, new CaptureSize(360, 816)));
        Assert.True(CaptureSizePolicy.IsSignificantChange(360, 800, new CaptureSize(368, 800)));
        Assert.True(CaptureSizePolicy.IsSignificantChange(360, 800, new CaptureSize(360, 817)));
        Assert.True(CaptureSizePolicy.IsSignificantChange(0, 0, new CaptureSize(360, 800)));
        Assert.False(CaptureSizePolicy.IsSignificantChange(360, 800, default));
    }

    [Fact]
    public void ABurstOfResizesProducesOneResubscribeAtItsEnd()
    {
        var start = Stopwatch.GetTimestamp();
        var oneMs = Stopwatch.Frequency / 1000;
        var scheduler = new CaptureResizeScheduler();

        scheduler.Signal(new CaptureSize(420, 900), start);
        scheduler.Signal(new CaptureSize(400, 880), start + (100 * oneMs));
        scheduler.Signal(new CaptureSize(380, 840), start + (250 * oneMs));

        Assert.True(scheduler.HasPending);

        // The last signal was at 250 ms, so the burst is not quiet until 550 ms.
        Assert.False(scheduler.TryTake(start + (290 * oneMs), 360, 800, out _));
        Assert.True(scheduler.TryTake(start + (560 * oneMs), 360, 800, out var taken));
        Assert.Equal(new CaptureSize(380, 840), taken);
        Assert.False(scheduler.HasPending);

        // One burst, one resubscribe: nothing is left for the next tick.
        Assert.False(scheduler.TryTake(start + (900 * oneMs), 360, 800, out _));
    }

    [Fact]
    public void AResizeUnderTheThresholdDoesNotResubscribe()
    {
        var start = Stopwatch.GetTimestamp();
        var oneMs = Stopwatch.Frequency / 1000;
        var scheduler = new CaptureResizeScheduler();

        scheduler.Signal(new CaptureSize(366, 800), start);

        Assert.False(scheduler.TryTake(start + (320 * oneMs), 360, 800, out _));
        Assert.False(scheduler.HasPending);
    }

    [Fact]
    public void ALaterBurstAfterAnAppliedSizeStillResubscribes()
    {
        var start = Stopwatch.GetTimestamp();
        var oneMs = Stopwatch.Frequency / 1000;
        var scheduler = new CaptureResizeScheduler();

        scheduler.Signal(new CaptureSize(380, 840), start);
        Assert.True(scheduler.TryTake(start + (320 * oneMs), 360, 800, out var first));
        Assert.Equal(new CaptureSize(380, 840), first);

        scheduler.Signal(new CaptureSize(420, 930), start + (1000 * oneMs));
        Assert.True(scheduler.TryTake(start + (1350 * oneMs), 380, 840, out var second));
        Assert.Equal(new CaptureSize(420, 930), second);
    }
}
