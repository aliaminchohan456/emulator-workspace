namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// UX-17: the one place that decides whether a Windows 11 only behaviour may
/// run. Every P/Invoke in the desktop app that has a Windows 10 story is gated
/// on one of these predicates, and each predicate has an int overload so the
/// thresholds can be unit-tested without a second machine.
///
/// Windows 10 (any supported build, 19041 and later) keeps a complete UI: the
/// custom title bar, the client-area maximize button with its hover/press
/// states, the DWM caption colour and the rounded shell corners drawn by WPF.
/// Only the native extras are skipped.
/// </summary>
public static class PlatformSupport
{
    /// <summary>Windows 11 first release: Snap Layouts flyout, DWM rounded corners.</summary>
    public const int Windows11Build = 22000;

    /// <summary>Windows 10 1809: DWMWA_USE_IMMERSIVE_DARK_MODE exists.</summary>
    public const int ImmersiveDarkModeBuild = 17763;

    /// <summary>Windows 10 2004: the current attribute id (20) replaced 19.</summary>
    public const int ImmersiveDarkModeCurrentBuild = 19041;

    /// <summary>
    /// Windows 11 recognises HTMAXBUTTON in a custom caption and draws the Snap
    /// Layouts flyout over it. Windows 10 has no flyout, so the hook stays off
    /// and the WPF button keeps ordinary client-area mouse input.
    /// </summary>
    public static bool SnapLayoutsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11Build);

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE is ignored before Windows 11.</summary>
    public static bool RoundedCornersSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11Build);

    /// <summary>DWM immersive dark title bar (the custom strip follows the theme either way).</summary>
    public static bool ImmersiveDarkModeSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, ImmersiveDarkModeBuild);

    /// <summary>The build-only form of <see cref="SnapLayoutsSupported"/>.</summary>
    public static bool SupportsSnapLayouts(int build) => build >= Windows11Build;

    /// <summary>The build-only form of <see cref="RoundedCornersSupported"/>.</summary>
    public static bool SupportsRoundedCorners(int build) => build >= Windows11Build;

    /// <summary>The build-only form of <see cref="ImmersiveDarkModeSupported"/>.</summary>
    public static bool SupportsImmersiveDarkMode(int build) => build >= ImmersiveDarkModeBuild;

    /// <summary>
    /// Which DWMWA_USE_IMMERSIVE_DARK_MODE id to send: 20 from Windows 10 2004,
    /// 19 on 1809-1909. Zero means no supported attribute on this build.
    /// </summary>
    public static int ImmersiveDarkModeAttribute(int build) => build switch
    {
        >= ImmersiveDarkModeCurrentBuild => 20,
        >= ImmersiveDarkModeBuild => 19,
        _ => 0,
    };
}
