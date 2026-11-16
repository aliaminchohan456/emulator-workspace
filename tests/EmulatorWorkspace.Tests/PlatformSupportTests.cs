using EmulatorWorkspace.Desktop.Services;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-17 part C: every Windows 11 only call in the desktop app is gated on
/// these thresholds, and Windows 10 must take the documented fallback instead of
/// hitting an API it does not have. The int overloads are what the gates use, so
/// the boundaries are provable here without a second machine.
/// </summary>
public class PlatformSupportTests
{
    [Theory]
    [InlineData(10240, false)] // Windows 10 RTM
    [InlineData(17763, false)] // 1809: immersive dark mode, no Snap Layouts
    [InlineData(19041, false)] // 2004
    [InlineData(19045, false)] // 22H2
    [InlineData(21999, false)] // last build before Windows 11
    [InlineData(22000, true)]  // Windows 11 21H2
    [InlineData(22631, true)]  // Windows 11 23H2
    [InlineData(26300, true)]  // the Windows 11 host this was verified on
    public void SnapLayouts_AreWindows11Only(int build, bool expected)
    {
        Assert.Equal(expected, PlatformSupport.SupportsSnapLayouts(build));
        Assert.Equal(expected, PlatformSupport.SupportsRoundedCorners(build));
    }

    [Theory]
    [InlineData(10240, false, 0)]  // no immersive dark mode at all
    [InlineData(17762, false, 0)]  // one build below 1809
    [InlineData(17763, true, 19)]  // 1809-1909 use the pre-20H1 id
    [InlineData(18363, true, 19)]  // 1909
    [InlineData(19041, true, 20)]  // 2004 switches to the current id
    [InlineData(22631, true, 20)]
    public void ImmersiveDarkMode_UsesTheBuildAppropriateAttribute(int build, bool supported, int attribute)
    {
        Assert.Equal(supported, PlatformSupport.SupportsImmersiveDarkMode(build));
        Assert.Equal(attribute, PlatformSupport.ImmersiveDarkModeAttribute(build));
    }

    [Fact]
    public void LivePredicates_MatchTheOperatingSystem()
    {
        // The same expressions the title bar and Chrome read at runtime, checked
        // against the framework's own answer so a typo in a threshold is caught.
        Assert.Equal(
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
            PlatformSupport.SnapLayoutsSupported);
        Assert.Equal(
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
            PlatformSupport.RoundedCornersSupported);
        Assert.Equal(
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763),
            PlatformSupport.ImmersiveDarkModeSupported);

        // The constants themselves are the contract other code reads.
        Assert.Equal(22000, PlatformSupport.Windows11Build);
        Assert.Equal(17763, PlatformSupport.ImmersiveDarkModeBuild);
        Assert.Equal(19041, PlatformSupport.ImmersiveDarkModeCurrentBuild);
    }

    [Fact]
    public void AncientBuilds_TakeEveryFallback()
    {
        Assert.False(PlatformSupport.SupportsSnapLayouts(0));
        Assert.False(PlatformSupport.SupportsRoundedCorners(0));
        Assert.False(PlatformSupport.SupportsImmersiveDarkMode(0));
        Assert.Equal(0, PlatformSupport.ImmersiveDarkModeAttribute(0));
    }
}
