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
    /// <summary>Bgra32 is the only layout the viewport writes in place.</summary>
    public const string TargetPixelFormat = "Bgra32";

    public static ViewportSurfaceKey For(int width, int height) =>
        new(width, height, TargetPixelFormat);

    public static ViewportSurfaceAction Decide(ViewportSurfaceKey? current, ViewportSurfaceKey next) =>
        current is { } held && held == next ? ViewportSurfaceAction.Reuse : ViewportSurfaceAction.Reallocate;
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
