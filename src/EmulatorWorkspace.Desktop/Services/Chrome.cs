using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// Applies the native dark title-bar treatment (DWM immersive dark mode) with
/// the documented pre-20H1 attribute fallback. High Contrast keeps the system
/// title-bar colors, and a failed DWM call degrades to the default chrome so
/// window controls, Snap and the system menu remain untouched.
/// </summary>
public static class Chrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModePre2004 = 19;

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
            if (SetAttribute(handle, DwmwaUseImmersiveDarkMode, value) != 0)
            {
                SetAttribute(handle, DwmwaUseImmersiveDarkModePre2004, value);
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
