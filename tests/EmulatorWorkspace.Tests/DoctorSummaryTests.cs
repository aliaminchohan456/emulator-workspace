using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-07 fix pass: the Settings doctor block prints friendly readings for two
/// raw supervisor fields (the engine's acceleration probe and the pinned image
/// package id). These tests pin the mapping, including the honest fallbacks.
/// </summary>
public sealed class DoctorSummaryTests
{
    private const string WhpxProbe =
        "accel: | 0 | WHPX(10.0.26300) is installed and usable.";

    [Fact]
    public void UsableWhpxProbeMapsToVersionAndReady()
    {
        var (label, ready) = DoctorSummary.Acceleration(WhpxProbe, whpxUsable: true);

        Assert.True(ready);
        Assert.Equal("WHPX 10.0.26300", label);
    }

    [Fact]
    public void UnusableProbeFallsBackToTheRawVerdict()
    {
        var (label, ready) = DoctorSummary.Acceleration(
            "accel: | 1 | WHPX(10.0.26300) is not installed and usable.", whpxUsable: false);

        Assert.False(ready);
        Assert.Equal("accel: | 1 | WHPX(10.0.26300) is not installed and usable.", label);
    }

    [Fact]
    public void UsableProbeWithoutAWHPXTokenNeverInventsAFriendlyName()
    {
        var (label, ready) = DoctorSummary.Acceleration("emulator-check unavailable: timed out", whpxUsable: true);

        Assert.False(ready);
        Assert.Equal("emulator-check unavailable: timed out", label);
    }

    [Fact]
    public void MissingProbeSaysNotReported()
    {
        Assert.Equal(("Not reported", false), DoctorSummary.Acceleration(null, whpxUsable: false));
        Assert.Equal(("Not reported", false), DoctorSummary.Acceleration("  ", whpxUsable: false));
    }

    [Fact]
    public void WhpxVersionReadsTheParenthesisedVersion()
    {
        Assert.Equal("10.0.26300", DoctorSummary.WhpxVersion(WhpxProbe));
        Assert.Equal("2.0.1", DoctorSummary.WhpxVersion("0 | whpx(2.0.1) usable"));
        Assert.Equal("", DoctorSummary.WhpxVersion("accel: | 0 | HAXM"));
        Assert.Equal("", DoctorSummary.WhpxVersion(null));
    }

    [Fact]
    public void PinnedImageReadsLikeTheInspectorWithRevisionAndSize()
    {
        var label = DoctorSummary.Image(
            "system-images;android-35;google_apis;x86_64", "2", " (6.52 GiB system.img)");

        Assert.Equal("Android 15 · Google APIs · x86_64 · rev 2 (6.52 GiB system.img)", label);
    }

    [Fact]
    public void UnknownImageTagKeepsItsRawSegment()
    {
        var label = DoctorSummary.Image("system-images;android-34;aosp_atd_v2;x86_64", "3", null);

        Assert.Equal("Android 14 · aosp_atd_v2 · x86_64 · rev 3", label);
    }

    [Fact]
    public void MissingImageIsNamedHonestly()
    {
        Assert.Equal("Not reported", DoctorSummary.Image(null, "2", null));
        Assert.Equal("Not reported", DoctorSummary.Image("  ", "missing", null));
        Assert.Contains("rev missing", DoctorSummary.Image("system-images;android-35;default;x86_64", "missing", null));
    }

    [Fact]
    public void GpuJoinsNameAndDriverAndDropsTheMissingHalf()
    {
        Assert.Equal("NVIDIA GeForce RTX 3080 · driver 566.36",
            DoctorSummary.Gpu("NVIDIA GeForce RTX 3080", "566.36"));
        Assert.Equal("NVIDIA GeForce RTX 3080", DoctorSummary.Gpu("NVIDIA GeForce RTX 3080", ""));
        Assert.Equal("driver 566.36", DoctorSummary.Gpu("", "566.36"));
        Assert.Equal("Not reported", DoctorSummary.Gpu(null, null));
    }

    [Fact]
    public void ImageNamingDescribeMatchesTheInspectorReading()
    {
        Assert.Equal("Android 15 · AOSP · x86_64", ImageNaming.Describe("system-images;android-35;default;x86_64"));
        Assert.Equal("Android 15 · Play Store · arm64-v8a",
            ImageNaming.Describe("system-images;android-35;google_apis_playstore;arm64-v8a"));
        Assert.Equal("", ImageNaming.Describe(null));
        Assert.Equal(35, ImageNaming.ParseImage("system-images;android-35;default;x86_64").Api);
        Assert.Equal("Android 12L", ImageNaming.AndroidVersion(32));
        Assert.Equal("API 27", ImageNaming.AndroidVersion(27));
    }
}
