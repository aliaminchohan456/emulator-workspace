using System.Diagnostics;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>EV-12: a capture size in device pixels.</summary>
public readonly record struct CaptureSize(int Width, int Height)
{
    /// <summary>True when there is no usable size (a viewport that is not laid out yet).</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// EV-12: how big the frame subscription should be, from the viewport.
///
/// The viewport used to ask for one hard-coded capture (540x1200, or half of
/// that for the small-window probe). At 125 %, 150 % and 175 % display scaling
/// the same 540-pixel-wide capture is stretched over more physical pixels, so
/// the guest text blurs; at a small window it was larger than the picture and
/// the supervisor copied pixels nobody saw.
///
/// The capture now follows what the viewport actually shows, in device pixels:
/// the largest guest-shaped rectangle that fits the viewport, converted to
/// physical pixels with the window's DPI scale, rounded down to an even size,
/// capped at the guest's own resolution and never smaller than 360 px on its
/// short side. Zoom is part of the same rule: 1:1 asks for the guest's native
/// resolution, a zoom multiplies the native size and Fit uses the viewport, so
/// the three toolbar buttons drive the capture as well as the display.
///
/// The rule is pure, so the aspect, even, cap, floor, threshold and DPI cases
/// are pinned without a window or a guest.
/// </summary>
public static class CaptureSizePolicy
{
    /// <summary>The floor for the short side, in device pixels.</summary>
    public const int MinimumShortSide = 360;

    /// <summary>Guest captures are even so a row stays whole in every copy.</summary>
    public const int EvenMultiple = 2;

    /// <summary>
    /// A resize is worth a resubscribe only above this relative change in
    /// either dimension. Below it the new size would be the same picture with a
    /// few pixels moved, and the stream would churn for nothing.
    /// </summary>
    public const double ChangeThreshold = 0.02;

    /// <summary>
    /// The viewport's own inset, in DIP, on both axes. <c>UpdateViewportSize</c>
    /// fits the guest into the viewport minus this padding, so the capture uses
    /// the same rectangle the picture is drawn into.
    /// </summary>
    public const int ViewportInsetDip = 32;

    /// <summary>The guest size assumed before a frame reports the real display.</summary>
    public const int DefaultGuestWidth = 1080;

    public const int DefaultGuestHeight = 2400;

    /// <summary>
    /// The capture size for a viewport, in device pixels. Returns an empty size
    /// when the viewport has not been measured yet.
    /// </summary>
    /// <param name="viewportWidthDip">ViewportScroll.ActualWidth.</param>
    /// <param name="viewportHeightDip">ViewportScroll.ActualHeight.</param>
    /// <param name="dpiScaleX">VisualTreeHelper.GetDpi(window).DpiScaleX.</param>
    /// <param name="dpiScaleY">VisualTreeHelper.GetDpi(window).DpiScaleY.</param>
    /// <param name="guestWidth">The guest's native width, or 0 when not known yet.</param>
    /// <param name="guestHeight">The guest's native height, or 0 when not known yet.</param>
    /// <param name="zoomMode">"Fit", "Actual" or any other word for an explicit zoom.</param>
    /// <param name="zoomFactor">The explicit zoom factor (1.0 at 100 %).</param>
    public static CaptureSize ForViewport(
        double viewportWidthDip,
        double viewportHeightDip,
        double dpiScaleX,
        double dpiScaleY,
        int guestWidth,
        int guestHeight,
        string? zoomMode = "Fit",
        double zoomFactor = 1.0)
    {
        guestWidth = guestWidth > 0 ? guestWidth : DefaultGuestWidth;
        guestHeight = guestHeight > 0 ? guestHeight : DefaultGuestHeight;
        if (!IsPositive(dpiScaleX))
        {
            dpiScaleX = 1.0;
        }

        if (!IsPositive(dpiScaleY))
        {
            dpiScaleY = 1.0;
        }

        var availableWidth = (viewportWidthDip - ViewportInsetDip) * dpiScaleX;
        var availableHeight = (viewportHeightDip - ViewportInsetDip) * dpiScaleY;
        if (!IsPositive(availableWidth) || !IsPositive(availableHeight))
        {
            return default;
        }

        double width;
        double height;
        if (string.Equals(zoomMode, "Actual", StringComparison.Ordinal))
        {
            // 1:1 maps one guest pixel to one physical pixel.
            width = guestWidth;
            height = guestHeight;
        }
        else if (string.Equals(zoomMode, "Fit", StringComparison.Ordinal) || !IsPositive(zoomFactor))
        {
            // Fit inside the viewport, keeping the guest's aspect.
            var scale = Math.Min(availableWidth / guestWidth, availableHeight / guestHeight);
            width = guestWidth * scale;
            height = guestHeight * scale;
        }
        else
        {
            // An explicit zoom is the picture's device-pixel size; the cap below
            // keeps it at the guest's own resolution.
            width = guestWidth * zoomFactor;
            height = guestHeight * zoomFactor;
        }

        // The guest has no more pixels than its own display.
        width = Math.Min(width, guestWidth);
        height = Math.Min(height, guestHeight);

        var shortSide = Math.Min(width, height);
        if (shortSide < MinimumShortSide)
        {
            // The floor wins over the viewport, but never over the guest: a
            // guest whose own display is smaller than the floor stays at its
            // native size.
            var up = MinimumShortSide / Math.Max(1.0, shortSide);
            width = Math.Min(width * up, guestWidth);
            height = Math.Min(height * up, guestHeight);
        }

        return new CaptureSize(
            EvenFloor(Math.Min(width, guestWidth)),
            EvenFloor(Math.Min(height, guestHeight)));
    }

    /// <summary>
    /// Whether a candidate is worth a resubscribe: more than 2 % in either
    /// dimension from the size the stream is asked for now. A first size (no
    /// applied size yet) is always a change.
    /// </summary>
    public static bool IsSignificantChange(int appliedWidth, int appliedHeight, CaptureSize candidate)
    {
        if (candidate.IsEmpty)
        {
            return false;
        }

        if (appliedWidth <= 0 || appliedHeight <= 0)
        {
            return true;
        }

        return Math.Abs(candidate.Width - appliedWidth) > appliedWidth * ChangeThreshold
               || Math.Abs(candidate.Height - appliedHeight) > appliedHeight * ChangeThreshold;
    }

    private static int EvenFloor(double value)
    {
        if (!IsPositive(value))
        {
            return 0;
        }

        // The epsilon keeps an exact 360.0 that floating point put a hair under
        // (359.99999999999994) from being rounded down to 358.
        var whole = (long)Math.Floor(value + 1e-6);
        return (int)Math.Max(EvenMultiple, whole - (whole % EvenMultiple));
    }

    private static bool IsPositive(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>
/// EV-12: the 300 ms quiet period in front of a resubscribe.
///
/// A window drag delivers a resize per mouse move, and Windows can send a DPI
/// change beside them. Only the size at the end of a burst is worth a
/// resubscribe, so every signal restarts the clock and exactly one candidate
/// leaves the burst. The rule is pure and takes its clock from the caller, so
/// the coalescing cases are unit tests instead of a timed UI test.
/// </summary>
public sealed class CaptureResizeScheduler
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(300);

    private bool _pending;
    private CaptureSize _size;
    private long _lastSignal;

    /// <summary>Whether a signal is waiting for its quiet period.</summary>
    public bool HasPending => _pending;

    /// <summary>Records a candidate and restarts the quiet period.</summary>
    public void Signal(CaptureSize size, long timestamp)
    {
        if (size.IsEmpty)
        {
            return;
        }

        _size = size;
        _lastSignal = timestamp;
        _pending = true;
    }

    /// <summary>
    /// Takes the burst's candidate once the quiet period has passed and the
    /// candidate differs from the applied size by more than 2 %. A candidate
    /// that is too close is consumed without a resubscribe.
    /// </summary>
    public bool TryTake(long now, int appliedWidth, int appliedHeight, out CaptureSize size)
    {
        size = default;
        if (!_pending || Stopwatch.GetElapsedTime(_lastSignal, now) < QuietPeriod)
        {
            return false;
        }

        _pending = false;
        if (!CaptureSizePolicy.IsSignificantChange(appliedWidth, appliedHeight, _size))
        {
            return false;
        }

        size = _size;
        return true;
    }

    /// <summary>Drops a pending candidate (the window is closing, or the guest changed).</summary>
    public void Clear()
    {
        _pending = false;
        _size = default;
        _lastSignal = 0;
    }
}
