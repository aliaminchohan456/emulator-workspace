using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// Applies the native dark title-bar treatment (DWM immersive dark mode) with
/// the build-appropriate attribute id, and asks DWM for the rounded window
/// silhouette the custom title bar is designed around (Windows 11 only; Windows
/// 10 skips that call and keeps its square frame, which the 12 DIP shell radius
/// follows). High Contrast keeps the system title-bar colors, and a failed DWM
/// call degrades to the default chrome so window controls, Snap and the system
/// menu remain untouched.
/// </summary>
public static class Chrome
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ThemeManager.Changed += OnThemeChanged;
    }

    /// <summary>
    /// Registers one window so its title bar follows the active theme. The
    /// attribute is set once the window handle exists and again on every
    /// theme change.
    /// </summary>
    public static void Apply(Window window)
    {
        if (window is null)
        {
            return;
        }

        window.SourceInitialized += (_, _) => ApplyNow(window);
        if (window.IsLoaded)
        {
            ApplyNow(window);
        }
    }

    private static void OnThemeChanged()
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            ApplyNow(window);
        }
    }

    private static void ApplyNow(Window window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            // High Contrast uses system chrome; forcing an immersive dark
            // title bar there would fight the system palette.
            var dark = ThemeManager.IsDark && !SystemParameters.HighContrast;
            var value = dark ? 1 : 0;

            // UX-17: Windows 10 1809-1909 only knows the pre-20H1 attribute id
            // (19); 2004 and later, including Windows 11, use 20. Windows 10 RTM
            // has neither, and the custom strip is drawn by WPF either way, so
            // the call is simply skipped there instead of failing.
            if (PlatformSupport.ImmersiveDarkModeSupported)
            {
                var attribute = PlatformSupport.ImmersiveDarkModeAttribute(Environment.OSVersion.Version.Build);
                if (attribute != 0)
                {
                    SetAttribute(handle, attribute, value);
                }
            }

            // UX-12: the window is a real top-level window with a custom caption,
            // so DWM still owns the outer silhouette, the shadow and the Snap
            // preview. Rounding it keeps the 12 DIP shell radius (ChromeShell)
            // from sitting inside a square frame. UX-17: DWMWA_WINDOW_CORNER_
            // PREFERENCE is a Windows 11 attribute; Windows 10 returns
            // E_INVALIDARG, so the call is gated rather than relying on that.
            if (PlatformSupport.RoundedCornersSupported)
            {
                SetAttribute(handle, DwmwaWindowCornerPreference, DwmWindowCornerRound);
            }
        }
        catch (Exception)
        {
            // Chrome styling is cosmetic; never break window creation on it.
        }
    }

    private static int SetAttribute(IntPtr handle, int attribute, int value)
    {
        return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
