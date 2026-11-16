using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// UX-12 keeps the custom frame honest across window states. A WPF Border only
/// rounds its own paint, never its children, so the 12 DIP shell radius is also
/// clipped into the content; the radius becomes 0 while the window is maximized,
/// where Windows squares the frame it has pushed to the monitor edges.
///
/// The actual HWND corners are rounded by DWM (see <c>Services.Chrome</c>), so the
/// window keeps a real system silhouette, shadow and Snap behaviour instead of a
/// layered transparent window that would cost the guest its hardware surface.
///
/// Maximized geometry: Windows parks a maximized window's resize border off screen
/// (measured here: 1938x1038 at -9,-9 for a 1920x1020 work area), and WPF hands
/// that whole rectangle to the client, so 7 DIP of the title bar and of the status
/// bar would be outside the monitor. The shell is therefore inset by exactly the
/// measured overhang - which is itself off screen, so no ring is drawn inside the
/// visible window and the shell still lands on the monitor edges.
/// </summary>
public static class ChromeShell
{
    /// <summary>The design guide's 12 DIP window radius.</summary>
    public const double Radius = 12;

    private const int MonitorDefaultToNearest = 2;

    public static void Attach(Window window, Border frame, FrameworkElement content)
    {
        void Sync()
        {
            var maximized = window.WindowState == WindowState.Maximized;
            var radius = maximized ? 0 : Radius;
            frame.CornerRadius = new CornerRadius(radius);
            Clip(content, radius);

            // The maximized window rect is only final once the window manager has
            // processed the state change, so the inset is measured after it.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Inset(window, frame)));
        }

        window.StateChanged += (_, _) => Sync();
        content.SizeChanged += (_, _) =>
        {
            var radius = window.WindowState == WindowState.Maximized ? 0 : Radius;
            Clip(content, radius);
        };

        Sync();
    }

    /// <summary>
    /// Moves the shell in from the parts of the maximized client that sit outside
    /// the monitor's work area. All four edges are measured, so a taskbar on any
    /// side and a multi-DPI desktop land on the same visible rectangle.
    /// </summary>
    private static void Inset(Window window, Border frame)
    {
        if (window.WindowState != WindowState.Maximized)
        {
            frame.Margin = new Thickness(0);
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        if (scale <= 0)
        {
            scale = 1;
        }

        double Pixels(int value) => Math.Max(0, value) / scale;

        frame.Margin = new Thickness(
            Pixels(info.Work.Left - rect.Left),
            Pixels(info.Work.Top - rect.Top),
            Pixels(rect.Right - info.Work.Right),
            Pixels(rect.Bottom - info.Work.Bottom));
    }

    private static void Clip(FrameworkElement content, double radius)
    {
        if (radius <= 0 || content.ActualWidth <= 0 || content.ActualHeight <= 0)
        {
            content.Clip = null;
            return;
        }

        content.Clip = new RectangleGeometry(
            new Rect(0, 0, content.ActualWidth, content.ActualHeight), radius, radius);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);
}
