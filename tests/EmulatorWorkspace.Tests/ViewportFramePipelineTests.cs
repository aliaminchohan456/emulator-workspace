using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-10: the viewport's three pure decisions. The pixel cases pin the BGRA
/// expansion for every format the pipeline accepts (2x2 known pixels plus a
/// SIMD/scalar agreement case), the mailbox pins latest-wins and its drop count,
/// and the surface policy pins reuse vs reallocate.
/// </summary>
public sealed class ViewportFramePipelineTests
{
    [Fact]
    public void Rgb24_2x2_KnownPixelsBecomeBgra()
    {
        byte[] source =
        [
            1, 2, 3, 4, 5, 6,
            7, 8, 9, 10, 11, 12,
        ];
        var destination = new byte[2 * 2 * 4];

        FramePixelConverter.Convert(source, 2, 2, 6, FramePixelFormat.Rgb24, destination, 8);

        Assert.Equal(
        [
            3, 2, 1, 255, 6, 5, 4, 255,
            9, 8, 7, 255, 12, 11, 10, 255,
        ], destination);
    }

    [Fact]
    public void Bgr24_2x2_KnownPixelsBecomeBgra()
    {
        byte[] source =
        [
            3, 2, 1, 6, 5, 4,
            9, 8, 7, 12, 11, 10,
        ];
        var destination = new byte[2 * 2 * 4];

        FramePixelConverter.Convert(source, 2, 2, 6, FramePixelFormat.Bgr24, destination, 8);

        Assert.Equal(
        [
            3, 2, 1, 255, 6, 5, 4, 255,
            9, 8, 7, 255, 12, 11, 10, 255,
        ], destination);
    }

    [Fact]
    public void Rgba32_2x2_KnownPixelsBecomeBgra()
    {
        byte[] source =
        [
            1, 2, 3, 40, 4, 5, 6, 41,
            7, 8, 9, 42, 10, 11, 12, 43,
        ];
        var destination = new byte[2 * 2 * 4];

        FramePixelConverter.Convert(source, 2, 2, 8, FramePixelFormat.Rgba32, destination, 8);

        Assert.Equal(
        [
            3, 2, 1, 40, 6, 5, 4, 41,
            9, 8, 7, 42, 12, 11, 10, 43,
        ], destination);
    }

    [Fact]
    public void Bgra32_2x2_IsCopiedUnchanged()
    {
        byte[] source =
        [
            1, 2, 3, 4, 5, 6, 7, 8,
            9, 10, 11, 12, 13, 14, 15, 16,
        ];
        var destination = new byte[16];

        FramePixelConverter.Convert(source, 2, 2, 8, FramePixelFormat.Bgra32, destination, 8);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void Convert_HonoursBothStridesAndLeavesPaddingAlone()
    {
        // Two 2-pixel rows with 2 bytes of source padding and 4 bytes of
        // destination padding after each row.
        byte[] source = new byte[16];
        source[0] = 1;
        source[1] = 2;
        source[2] = 3;
        source[3] = 4;
        source[4] = 5;
        source[5] = 6;
        source[8] = 7;
        source[9] = 8;
        source[10] = 9;
        source[11] = 10;
        source[12] = 11;
        source[13] = 12;
        var destination = new byte[24];

        FramePixelConverter.Convert(source, 2, 2, 8, FramePixelFormat.Rgb24, destination, 12);

        Assert.Equal([3, 2, 1, 255, 6, 5, 4, 255], destination[..8]);
        Assert.All(destination[8..12], value => Assert.Equal(0, value));
        Assert.Equal([9, 8, 7, 255, 12, 11, 10, 255], destination[12..20]);
        Assert.All(destination[20..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(FramePixelFormat.Rgb24)]
    [InlineData(FramePixelFormat.Bgr24)]
    [InlineData(FramePixelFormat.Rgba32)]
    [InlineData(FramePixelFormat.Bgra32)]
    public void Convert_VectorPathAndTailAgreeWithScalar(FramePixelFormat format)
    {
        const int width = 37; // 9 vectors of 4 pixels + a 1-pixel tail
        const int height = 5;
        var bytesPerPixel = FramePixelConverter.BytesPerPixel(format);
        var source = new byte[width * height * bytesPerPixel];
        for (var index = 0; index < source.Length; index++)
        {
            source[index] = (byte)((index * 37 + 11) & 0xFF);
        }

        var actual = new byte[width * height * 4];
        FramePixelConverter.Convert(source, width, height, width * bytesPerPixel, format, actual, width * 4);

        var expected = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = (row * width + x) * bytesPerPixel;
                var d = (row * width + x) * 4;
                switch (format)
                {
                    case FramePixelFormat.Rgb24:
                        expected[d] = source[s + 2];
                        expected[d + 1] = source[s + 1];
                        expected[d + 2] = source[s];
                        expected[d + 3] = 255;
                        break;
                    case FramePixelFormat.Bgr24:
                        expected[d] = source[s];
                        expected[d + 1] = source[s + 1];
                        expected[d + 2] = source[s + 2];
                        expected[d + 3] = 255;
                        break;
                    case FramePixelFormat.Rgba32:
                        expected[d] = source[s + 2];
                        expected[d + 1] = source[s + 1];
                        expected[d + 2] = source[s];
                        expected[d + 3] = source[s + 3];
                        break;
                    case FramePixelFormat.Bgra32:
                        expected[d] = source[s];
                        expected[d + 1] = source[s + 1];
                        expected[d + 2] = source[s + 2];
                        expected[d + 3] = source[s + 3];
                        break;
                }
            }
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Convert_RejectsAShortBufferAndANarrowStride()
    {
        var destination = new byte[16];

        Assert.Throws<ArgumentException>(() =>
            FramePixelConverter.Convert(new byte[11], 2, 2, 6, FramePixelFormat.Rgb24, destination, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FramePixelConverter.Convert(new byte[12], 2, 2, 5, FramePixelFormat.Rgb24, destination, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FramePixelConverter.Convert(new byte[12], 2, 2, 6, FramePixelFormat.Rgb24, destination, 7));
    }

    [Theory]
    [InlineData("rgb24", FramePixelFormat.Rgb24)]
    [InlineData("bgr24", FramePixelFormat.Bgr24)]
    [InlineData("rgba32", FramePixelFormat.Rgba32)]
    [InlineData("bgra32", FramePixelFormat.Bgra32)]
    public void TryParse_AcceptsEverySupportedFormat(string text, FramePixelFormat expected)
    {
        Assert.True(FramePixelConverter.TryParse(text, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsPngAndUnknownFormats(string? text)
    {
        Assert.False(FramePixelConverter.TryParse(text, out _));
    }

    [Fact]
    public void FrameSlot_KeepsOnlyTheNewestFrameAndCountsTheReplacedOnes()
    {
        var slot = new FrameSlot<string>();

        slot.Post("a");
        slot.Post("b");
        slot.Post("c");

        Assert.Equal(2L, slot.Dropped);
        Assert.True(slot.HasPending);
        Assert.True(slot.TryTake(out var newest));
        Assert.Equal("c", newest);
        Assert.False(slot.TryTake(out _));
        Assert.Equal(2L, slot.Dropped);
    }

    [Fact]
    public async Task FrameSlot_TakeWaitsForTheNextPost()
    {
        var slot = new FrameSlot<string>();
        var take = slot.TakeAsync(CancellationToken.None).AsTask();

        Assert.False(take.IsCompleted);
        slot.Post("first");
        var frame = await take.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("first", frame);
        Assert.Equal(0L, slot.Dropped);
    }

    [Fact]
    public void FrameSlot_ResetDroppedClearsTheCounter()
    {
        var slot = new FrameSlot<string>();
        slot.Post("1");
        slot.Post("2");

        slot.ResetDropped();

        Assert.Equal(0L, slot.Dropped);
    }

    [Fact]
    public void SurfacePolicy_ReusesTheSameSizeAndFormat()
    {
        var held = ViewportSurfacePolicy.For(540, 1200);

        Assert.Equal(ViewportSurfaceAction.Reuse, ViewportSurfacePolicy.Decide(held, held));
        Assert.Equal("Bgra32", held.PixelFormat);
    }

    [Fact]
    public void SurfacePolicy_ReallocatesWhenTheSizeChanges()
    {
        var held = ViewportSurfacePolicy.For(540, 1200);

        Assert.Equal(ViewportSurfaceAction.Reallocate,
            ViewportSurfacePolicy.Decide(held, ViewportSurfacePolicy.For(1080, 2400)));
    }

    [Fact]
    public void SurfacePolicy_ReallocatesWhenTheFormatChanges()
    {
        var held = ViewportSurfacePolicy.For(540, 1200);

        Assert.Equal(ViewportSurfaceAction.Reallocate,
            ViewportSurfacePolicy.Decide(held, new ViewportSurfaceKey(540, 1200, "Bgr32")));
    }

    [Fact]
    public void SurfacePolicy_AllocatesWhenNothingIsHeld()
    {
        Assert.Equal(ViewportSurfaceAction.Reallocate,
            ViewportSurfacePolicy.Decide(null, ViewportSurfacePolicy.For(540, 1200)));
    }

    [Fact]
    public void FrameStageSlot_PostReplacesTheWaitingFrameAndReturnsIt()
    {
        var slot = new FrameStageSlot<string>();

        Assert.Null(slot.Post("a"));
        Assert.Equal("a", slot.Post("b"));
        Assert.Equal("b", slot.Post("c"));
        Assert.Equal(2L, slot.Replaced);

        Assert.True(slot.TryTake(out var newest));
        Assert.Equal("c", newest);
        Assert.False(slot.TryTake(out _));
        Assert.Equal(2L, slot.Replaced);
    }

    [Fact]
    public void FrameStageSlot_PutBackKeepsTheSameFrameOnATryLockRetry()
    {
        var slot = new FrameStageSlot<string>();
        slot.Post("a");
        Assert.True(slot.TryTake(out var held));

        // The first TryLock failed: the frame goes back and waits for the next
        // render tick. That is a retry, not a drop.
        Assert.Null(slot.PutBack(held));
        Assert.Equal(0L, slot.Replaced);
        Assert.True(slot.HasPending);

        Assert.True(slot.TryTake(out var retried));
        Assert.Same(held, retried);
        Assert.Equal(0L, slot.Replaced);
    }

    [Fact]
    public void FrameStageSlot_PutBackDropsTheOlderFrameWhenANewerOneArrived()
    {
        var slot = new FrameStageSlot<string>();
        slot.Post("old");
        Assert.True(slot.TryTake(out var held));

        // While the old frame waited for the lock a new one was staged.
        Assert.Null(slot.Post("new"));

        // The held frame is now the one the policy drops, exactly once.
        Assert.Same(held, slot.PutBack(held));
        Assert.Equal(1L, slot.Replaced);
        Assert.True(slot.TryTake(out var newest));
        Assert.Equal("new", newest);
    }

    [Fact]
    public void FrameStageSlot_ManyRetriesOfOneFrameAreNeverDrops()
    {
        var slot = new FrameStageSlot<string>();
        slot.Post("a");
        Assert.True(slot.TryTake(out var held));

        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Null(slot.PutBack(held));
            Assert.True(slot.TryTake(out var again));
            Assert.Same(held, again);
        }

        Assert.Equal(0L, slot.Replaced);
    }

    [Fact]
    public void ViewportPixelPolicy_NativeCopyKeepsEveryWpfNativeLayout()
    {
        Assert.Equal("Rgb24", ViewportPixelPolicy.SurfaceFormatFor(
            FramePixelFormat.Rgb24, ViewportStageMode.NativeCopy));
        Assert.Equal("Bgr24", ViewportPixelPolicy.SurfaceFormatFor(
            FramePixelFormat.Bgr24, ViewportStageMode.NativeCopy));
        Assert.Equal("Bgra32", ViewportPixelPolicy.SurfaceFormatFor(
            FramePixelFormat.Bgra32, ViewportStageMode.NativeCopy));

        // Rgba32 has no direct WPF layout; alpha survives the BGRA expansion.
        Assert.Equal("Bgra32", ViewportPixelPolicy.SurfaceFormatFor(
            FramePixelFormat.Rgba32, ViewportStageMode.NativeCopy));
        Assert.False(ViewportPixelPolicy.NeedsConversion(FramePixelFormat.Rgb24, "Rgb24"));
        Assert.True(ViewportPixelPolicy.NeedsConversion(FramePixelFormat.Rgba32, "Bgra32"));
    }

    [Fact]
    public void ViewportPixelPolicy_ShuffleAlwaysTargetsBgra()
    {
        foreach (var format in Enum.GetValues<FramePixelFormat>())
        {
            Assert.Equal("Bgra32", ViewportPixelPolicy.SurfaceFormatFor(
                format, ViewportStageMode.ShuffleToBgra));
        }

        Assert.True(ViewportPixelPolicy.NeedsConversion(FramePixelFormat.Rgb24, "Bgra32"));
        Assert.False(ViewportPixelPolicy.NeedsConversion(FramePixelFormat.Bgra32, "Bgra32"));
    }

    [Theory]
    [InlineData(null, ViewportStageMode.NativeCopy)]
    [InlineData("", ViewportStageMode.NativeCopy)]
    [InlineData("bgra", ViewportStageMode.ShuffleToBgra)]
    [InlineData("BGRA", ViewportStageMode.ShuffleToBgra)]
    [InlineData("native", ViewportStageMode.NativeCopy)]
    [InlineData("copy", ViewportStageMode.NativeCopy)]
    [InlineData("unknown", ViewportStageMode.NativeCopy)]
    public void ViewportPixelPolicy_ParsesTheFormatOverride(string? value, ViewportStageMode expected)
    {
        Assert.Equal(expected, ViewportPixelPolicy.ParseMode(value));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("0", true, false)]
    [InlineData("false", true, false)]
    [InlineData("1", false, true)]
    [InlineData("true", false, true)]
    [InlineData(null, false, false)]
    [InlineData("off", false, false)]
    public void ViewportPathSelection_HonoursTheOverrideInBothDefaults(
        string? value, bool legacyByDefault, bool expected)
    {
        Assert.Equal(expected, ViewportPathSelection.LegacyEnabled(value, legacyByDefault));
    }

    [Fact]
    public void FrameCopier_CopiesRowsAndLeavesPaddingAlone()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 99, 99, 7, 8, 9, 10, 11, 12, 99, 99];
        var destination = new byte[20];

        // Two 6-byte rows, source stride 8, destination stride 10.
        FrameCopier.CopyRows(source, 8, 6, 2, destination, 10);

        Assert.Equal([1, 2, 3, 4, 5, 6, 0, 0, 0, 0], destination[..10]);
        Assert.Equal([7, 8, 9, 10, 11, 12, 0, 0, 0, 0], destination[10..]);
    }

    [Fact]
    public void FrameCopier_UsesOneBlockCopyWhenTheStridesMatch()
    {
        var source = new byte[4 * 3 * 2];
        for (var index = 0; index < source.Length; index++)
        {
            source[index] = (byte)(index + 1);
        }

        var destination = new byte[source.Length];
        FrameCopier.CopyRows(source, 12, 12, 2, destination, 12);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void FrameCopier_BlockCopyAcceptsAShortFinalRow()
    {
        // Two 6-byte rows at stride 10; the file ends after the last row's six
        // data bytes, without the four padding bytes.
        byte[] source = [1, 2, 3, 4, 5, 6, 99, 99, 99, 99, 7, 8, 9, 10, 11, 12];
        var destination = new byte[20];

        FrameCopier.CopyRows(source, 10, 6, 2, destination, 10);

        Assert.Equal([1, 2, 3, 4, 5, 6], destination[..6]);
        Assert.Equal([7, 8, 9, 10, 11, 12], destination[10..16]);
        Assert.All(destination[16..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void FrameCopier_RejectsShortBuffersAndNarrowStrides()
    {
        var destination = new byte[16];

        Assert.Throws<ArgumentException>(() =>
            FrameCopier.CopyRows(new byte[11], 8, 6, 2, destination, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameCopier.CopyRows(new byte[16], 5, 6, 2, destination, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameCopier.CopyRows(new byte[16], 8, 6, 2, destination, 5));
        Assert.Throws<ArgumentException>(() =>
            FrameCopier.CopyRows(new byte[16], 8, 6, 2, new byte[11], 8));
    }

    [Fact]
    public void SurfacePolicy_ReusesASurfaceOfTheSameSizeAndNativeFormat()
    {
        var held = ViewportSurfacePolicy.For(540, 1200, "Rgb24");

        Assert.Equal(ViewportSurfaceAction.Reuse,
            ViewportSurfacePolicy.Decide(held, ViewportSurfacePolicy.For(540, 1200, "Rgb24")));
        Assert.Equal(ViewportSurfaceAction.Reallocate,
            ViewportSurfacePolicy.Decide(held, ViewportSurfacePolicy.For(540, 1200, "Bgra32")));
    }
}
