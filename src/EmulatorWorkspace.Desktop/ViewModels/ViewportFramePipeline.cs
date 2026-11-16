using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-10: a frame's source pixel layout, as the supervisor publishes it. The
/// controller's "rgb24" payload is 3 bytes per pixel in R,G,B order (see
/// docs/phase1-1-evidence.md: a BGR interpretation showed a blue marker where
/// the sample expected red). The other three are accepted because the viewport
/// conversion is a pure function of the declared format.
/// </summary>
public enum FramePixelFormat
{
    Rgb24,
    Bgr24,
    Rgba32,
    Bgra32,
}

/// <summary>
/// EV-10: the pixel conversion behind the viewport. It is pure so the 2x2 pixel
/// cases and the stride/tail cases are pinned without a window or a guest: the
/// source is expanded into the one BGRA layout a WPF <c>WriteableBitmap</c> can
/// be written in place.
/// </summary>
public static class FramePixelConverter
{
    public const int Bgra32BytesPerPixel = 4;

    public static int BytesPerPixel(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Rgb24 or FramePixelFormat.Bgr24 => 3,
        _ => 4,
    };

    public static bool TryParse(string? format, out FramePixelFormat value)
    {
        switch (format)
        {
            case "rgb24":
                value = FramePixelFormat.Rgb24;
                return true;
            case "bgr24":
                value = FramePixelFormat.Bgr24;
                return true;
            case "rgba32":
                value = FramePixelFormat.Rgba32;
                return true;
            case "bgra32":
                value = FramePixelFormat.Bgra32;
                return true;
            default:
                value = default;
                return false;
        }
    }

    /// <summary>
    /// Expands one whole frame into BGRA. Rows are copied with their own stride,
    /// so a padded source and WPF's back-buffer stride are both honoured.
    /// </summary>
    public static void Convert(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        FramePixelFormat format,
        Span<byte> destination,
        int destinationStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var sourceBytesPerPixel = BytesPerPixel(format);
        var sourceRowBytes = checked(width * sourceBytesPerPixel);
        var destinationRowBytes = checked(width * Bgra32BytesPerPixel);
        if (sourceStride < sourceRowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "The source stride is narrower than one row.");
        }

        if (destinationStride < destinationRowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStride), "The destination stride is narrower than one row.");
        }

        if (source.Length < checked(sourceStride * (height - 1) + sourceRowBytes))
        {
            throw new ArgumentException("The source buffer is shorter than the declared frame.", nameof(source));
        }

        if (destination.Length < checked(destinationStride * (height - 1) + destinationRowBytes))
        {
            throw new ArgumentException("The destination buffer is shorter than the declared frame.", nameof(destination));
        }

        for (var row = 0; row < height; row++)
        {
            ConvertRow(
                source.Slice(row * sourceStride, sourceRowBytes),
                format,
                destination.Slice(row * destinationStride, destinationRowBytes));
        }
    }

    private static void ConvertRow(ReadOnlySpan<byte> source, FramePixelFormat format, Span<byte> destination)
    {
        switch (format)
        {
            case FramePixelFormat.Bgra32:
                source.CopyTo(destination);
                break;
            case FramePixelFormat.Rgb24:
                Convert24(source, destination, Rgb24ToBgra, swapRedBlue: true);
                break;
            case FramePixelFormat.Bgr24:
                Convert24(source, destination, Bgr24ToBgra, swapRedBlue: false);
                break;
            case FramePixelFormat.Rgba32:
                Convert32(source, destination, swapRedBlue: true);
                break;
        }
    }

    /// <summary>
    /// Four pixels at a time: one 16-byte load, one shuffle, one OR for the alpha
    /// byte, one 16-byte store. The load reads four bytes past the twelve it
    /// consumes, so it runs only while the row still has room; the last pixels
    /// take the scalar tail.
    /// </summary>
    private static void Convert24(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        Vector128<byte> shuffle,
        bool swapRedBlue)
    {
        var pixels = destination.Length / Bgra32BytesPerPixel;
        var pixel = 0;
        if (Ssse3.IsSupported)
        {
            for (; pixel + 4 <= pixels && (pixel * 3) + 16 <= source.Length; pixel += 4)
            {
                var loaded = Vector128.Create(source.Slice(pixel * 3, 16));
                var expanded = Sse2.Or(Ssse3.Shuffle(loaded, shuffle), AlphaMask);
                expanded.CopyTo(destination.Slice(pixel * 4, 16));
            }
        }

        for (; pixel < pixels; pixel++)
        {
            var s = pixel * 3;
            var d = pixel * 4;
            destination[d] = swapRedBlue ? source[s + 2] : source[s];
            destination[d + 1] = source[s + 1];
            destination[d + 2] = swapRedBlue ? source[s] : source[s + 2];
            destination[d + 3] = 255;
        }
    }

    /// <summary>
    /// Four pixels at a time for 32-bit sources: RGBA is a byte swap, BGRA is a
    /// plain copy (handled by the caller). The scalar tail covers the remainder.
    /// </summary>
    private static void Convert32(ReadOnlySpan<byte> source, Span<byte> destination, bool swapRedBlue)
    {
        var pixels = destination.Length / Bgra32BytesPerPixel;
        var pixel = 0;
        if (swapRedBlue && Ssse3.IsSupported)
        {
            for (; pixel + 4 <= pixels && (pixel * 4) + 16 <= source.Length; pixel += 4)
            {
                var loaded = Vector128.Create(source.Slice(pixel * 4, 16));
                Ssse3.Shuffle(loaded, Rgba32ToBgra).CopyTo(destination.Slice(pixel * 4, 16));
            }
        }

        for (; pixel < pixels; pixel++)
        {
            var s = pixel * 4;
            var d = pixel * 4;
            destination[d] = swapRedBlue ? source[s + 2] : source[s];
            destination[d + 1] = source[s + 1];
            destination[d + 2] = swapRedBlue ? source[s] : source[s + 2];
            destination[d + 3] = source[s + 3];
        }
    }

    private static readonly Vector128<byte> AlphaMask =
        Vector128.Create((byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);

    private static readonly Vector128<byte> Rgb24ToBgra =
        Vector128.Create((byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80);

    private static readonly Vector128<byte> Bgr24ToBgra =
        Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);

    private static readonly Vector128<byte> Rgba32ToBgra =
        Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
}

/// <summary>
/// EV-10: what one viewport surface is. The bitmap is reused only while the key
/// is identical, so a resolution change reallocates and a steady stream does not.
/// </summary>
public readonly record struct ViewportSurfaceKey(int Width, int Height, string PixelFormat);

public enum ViewportSurfaceAction
{
    Reuse,
    Reallocate,
}

public static class ViewportSurfacePolicy
{
    /// <summary>Bgra32 is the conversion target when the source layout is not WPF-native.</summary>
    public const string TargetPixelFormat = "Bgra32";

    public static ViewportSurfaceKey For(int width, int height) =>
        new(width, height, TargetPixelFormat);

    public static ViewportSurfaceKey For(int width, int height, string pixelFormat) =>
        new(width, height, pixelFormat);

    public static ViewportSurfaceAction Decide(ViewportSurfaceKey? current, ViewportSurfaceKey next) =>
        current is { } held && held == next ? ViewportSurfaceAction.Reuse : ViewportSurfaceAction.Reallocate;
}

/// <summary>
/// EV-10b: how a frame is prepared for the surface. <see cref="ShuffleToBgra"/>
/// is the EV-10 path (SSSE3 expansion into a 4-byte-per-pixel buffer).
/// <see cref="NativeCopy"/> keeps a source layout WPF can render directly
/// (Rgb24, Bgr24 or Bgra32) and leaves the conversion to a plain row copy.
/// </summary>
public enum ViewportStageMode
{
    NativeCopy,
    ShuffleToBgra,
}

public static class ViewportPixelPolicy
{
    public const string Bgra32Format = "Bgra32";
    public const string Rgb24Format = "Rgb24";
    public const string Bgr24Format = "Bgr24";

    /// <summary>
    /// EW_VIEWPORT_FORMAT=native copies a WPF-native source layout; =bgra forces
    /// the EV-10 shuffle. The default is the path the live A/B measured faster.
    /// </summary>
    public static ViewportStageMode ParseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "bgra" or "shuffle" => ViewportStageMode.ShuffleToBgra,
            "native" or "copy" => ViewportStageMode.NativeCopy,
            _ => DefaultMode,
        };

    /// <summary>Measured on the validation host; see ev10b-report.md.</summary>
    public const ViewportStageMode DefaultMode = ViewportStageMode.NativeCopy;

    /// <summary>
    /// The <c>WriteableBitmap</c> format for one staged frame. Only Rgb24,
    /// Bgr24 and Bgra32 have a direct WPF layout; Rgba32 still needs the BGRA
    /// expansion (alpha is preserved, the red and blue bytes swap).
    /// </summary>
    public static string SurfaceFormatFor(FramePixelFormat source, ViewportStageMode mode) =>
        mode == ViewportStageMode.NativeCopy
            ? source switch
            {
                FramePixelFormat.Rgb24 => Rgb24Format,
                FramePixelFormat.Bgr24 => Bgr24Format,
                FramePixelFormat.Bgra32 => Bgra32Format,
                _ => Bgra32Format,
            }
            : Bgra32Format;

    /// <summary>Whether the staged buffer already has the surface's layout.</summary>
    public static bool NeedsConversion(FramePixelFormat source, string surfaceFormat) =>
        source switch
        {
            FramePixelFormat.Rgb24 => !string.Equals(surfaceFormat, Rgb24Format, StringComparison.Ordinal),
            FramePixelFormat.Bgr24 => !string.Equals(surfaceFormat, Bgr24Format, StringComparison.Ordinal),
            FramePixelFormat.Bgra32 => !string.Equals(surfaceFormat, Bgra32Format, StringComparison.Ordinal),
            _ => true,
        };

    public static int BytesPerPixel(string surfaceFormat) =>
        string.Equals(surfaceFormat, Bgra32Format, StringComparison.Ordinal) ? 4 : 3;
}

/// <summary>
/// EV-10b: the UI-thread row copy. When the staged stride already matches the
/// surface's back-buffer stride one block copy carries the whole frame;
/// otherwise rows are copied one by one so padding is never read or written.
/// </summary>
public static class FrameCopier
{
    public static void CopyRows(
        ReadOnlySpan<byte> source,
        int sourceStride,
        int rowBytes,
        int height,
        Span<byte> destination,
        int destinationStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (sourceStride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "The source stride is narrower than one row.");
        }

        if (destinationStride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStride), "The destination stride is narrower than one row.");
        }

        if (source.Length < checked(sourceStride * (height - 1) + rowBytes))
        {
            throw new ArgumentException("The source buffer is shorter than the declared frame.", nameof(source));
        }

        if (destination.Length < checked(destinationStride * (height - 1) + rowBytes))
        {
            throw new ArgumentException("The destination buffer is shorter than the declared frame.", nameof(destination));
        }

        if (sourceStride == destinationStride)
        {
            // The last row may end at rowBytes instead of the full stride, so
            // copy the validated prefix rather than stride * height.
            var length = checked(sourceStride * (height - 1) + rowBytes);
            source[..length].CopyTo(destination);
            return;
        }

        for (var row = 0; row < height; row++)
        {
            source.Slice(row * sourceStride, rowBytes).CopyTo(destination.Slice(row * destinationStride, rowBytes));
        }
    }
}

/// <summary>
/// EV-10b: the staging mailbox between the background converter and the
/// CompositionTarget.Rendering tick. It is a one-deep mailbox with two
/// operations:
/// <list type="bullet">
/// <item><see cref="Post"/> replaces a waiting frame and returns it to the
/// caller, which owns its pooled buffer.</item>
/// <item><see cref="PutBack"/> is the TryLock retry: when nothing newer arrived
/// the held frame is stored again and <b>not</b> counted; when a newer frame is
/// already waiting the older held frame is discarded and counted once.</item>
/// </list>
/// So a lock retry is free, and only a frame that a newer one actually replaced
/// is a drop.
/// </summary>
public sealed class FrameStageSlot<T>
    where T : class
{
    private readonly object _gate = new();
    private T? _slot;
    private long _replaced;

    /// <summary>Frames that a newer frame replaced before presentation.</summary>
    public long Replaced => Interlocked.Read(ref _replaced);

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _slot is not null;
            }
        }
    }

    /// <summary>Stores the newest frame and returns the frame it replaced.</summary>
    public T? Post(T frame)
    {
        T? replaced;
        lock (_gate)
        {
            replaced = _slot;
            if (replaced is not null)
            {
                Interlocked.Increment(ref _replaced);
            }

            _slot = frame;
        }

        return replaced;
    }

    public bool TryTake(out T frame)
    {
        lock (_gate)
        {
            if (_slot is null)
            {
                frame = null!;
                return false;
            }

            frame = _slot;
            _slot = null;
            return true;
        }
    }

    /// <summary>
    /// Returns a frame whose TryLock failed. Returns <c>null</c> when the frame
    /// is stored again (a retry, not a drop) and returns the argument when a
    /// newer frame already occupies the slot (this older frame is dropped).
    /// </summary>
    public T? PutBack(T frame)
    {
        lock (_gate)
        {
            if (_slot is null)
            {
                _slot = frame;
                return null;
            }

            Interlocked.Increment(ref _replaced);
            return frame;
        }
    }

    public void ResetReplaced() => Interlocked.Exchange(ref _replaced, 0);
}

/// <summary>
/// EV-10b: which path the viewport runs. The env var keeps an override for the
/// A/B, and the default is the result the measurement selected.
/// </summary>
public static class ViewportPathSelection
{
    /// <summary>The path the measured build ships; see ev10b-report.md.</summary>
    public const bool LegacyByDefault = false;

    /// <summary>
    /// Truthy values select the legacy per-frame frozen bitmap; a false value
    /// selects the new path; an unset value takes the measured default. When
    /// <see cref="LegacyByDefault"/> is true, <c>EW_VIEWPORT_LEGACY=0</c> is the
    /// documented opt-out.
    /// </summary>
    public static bool LegacyEnabled(string? value, bool legacyByDefault) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => legacyByDefault,
        };
}

/// <summary>
/// EV-10: the one-deep frame mailbox. The newest frame always wins: a frame that
/// arrives while another is still waiting replaces it, and the replacement is
/// counted as a dropped frame. The reader never blocks the pipe thread, and the
/// queue can never grow, so presentation latency has one decode's bound.
/// </summary>
public sealed class FrameSlot<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private T? _slot;
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _slot is not null;
            }
        }
    }

    public void Post(T frame)
    {
        lock (_gate)
        {
            if (_slot is not null)
            {
                Interlocked.Increment(ref _dropped);
            }

            _slot = frame;
        }

        Release();
    }

    public bool TryTake(out T frame)
    {
        lock (_gate)
        {
            if (_slot is null)
            {
                frame = null!;
                return false;
            }

            frame = _slot;
            _slot = null;
            return true;
        }
    }

    public async ValueTask<T> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_slot is { } frame)
                {
                    _slot = null;
                    return frame;
                }
            }
        }
    }

    public void ResetDropped() => Interlocked.Exchange(ref _dropped, 0);

    private void Release()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // The reader is already awake with the newest frame in the slot.
        }
    }
}
