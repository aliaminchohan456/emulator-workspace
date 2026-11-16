using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// UX-12: the Graphite title bar both shell windows share. It owns the window
/// controls (through <see cref="SystemCommands"/>, so minimise/maximise keep the
/// system animations and Snap), the live breadcrumbs and the Ctrl+K pill.
///
/// The strip itself is the window's caption - <c>WindowChrome.CaptionHeight</c>
/// is the same 44 DIP - so dragging, double-click maximise, Aero Snap, Alt+Space
/// and the system menu stay with the window manager. Only the pill and the three
/// caption buttons opt back into hit testing, which is why the breadcrumbs are a
/// drag surface rather than text you cannot grab.
///
/// UX-14: the maximize button is the one exception. Its rectangle answers
/// HTMAXBUTTON from WM_NCHITTEST so Windows 11 recognises it as the real maximize
/// button and shows the Snap Layouts flyout on hover; the window manager then
/// owns the mouse there, so the hot and pressed visuals are driven from
/// WM_NCMOUSEMOVE / WM_NCMOUSELEAVE / WM_NCLBUTTONDOWN / WM_NCLBUTTONUP and the
/// toggle runs on the non-client button up. The button is still a WPF button:
/// Tab, Enter and Space, its focus ring and its automation name are untouched.
/// The hook lives on this control, so every window that hosts the shared strip
/// (the Studio and the Devices window) gets the same behaviour.
/// </summary>
public partial class TitleBar : UserControl
{
    /// <summary>The visible height of the strip; also the window's caption height.</summary>
    public const double BarHeight = 44;

    private const int WmMove = 0x0003;
    private const int WmSize = 0x0005;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int WmDpiChanged = 0x02E0;

    private const int HtMaxButton = 9;

    private const int TmeLeave = 0x0002;
    private const int TmeNonClient = 0x0010;

    /// <summary>
    /// The HTMAXBUTTON contract and its flyout are Windows 11 features. Older
    /// hosts keep the UX-13 client-area button instead of a caption button
    /// nothing would draw a flyout for.
    /// </summary>
    private static readonly bool SnapLayoutsAvailable = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private HwndSource? _source;
    private Rect _maxButtonBounds;
    private bool _maxButtonBoundsValid;
    private bool _maxHot;
    private bool _maxPressed;
    private bool _trackingLeave;

    /// <summary>The current page or destination, e.g. "Studio", "Logcat", "Devices".</summary>
    public static readonly DependencyProperty PageNameProperty = DependencyProperty.Register(
        nameof(PageName), typeof(string), typeof(TitleBar),
        new PropertyMetadata("Studio", OnCrumbChanged));

    /// <summary>
    /// The real device the page is acting on. Empty when nothing is selected, in
    /// which case the whole device crumb disappears instead of printing a
    /// placeholder name.
    /// </summary>
    public static readonly DependencyProperty ContextNameProperty = DependencyProperty.Register(
        nameof(ContextName), typeof(string), typeof(TitleBar),
        new PropertyMetadata("", OnCrumbChanged));

    /// <summary>The device's API level, e.g. "API 35". Hidden when unknown.</summary>
    public static readonly DependencyProperty ContextDetailProperty = DependencyProperty.Register(
        nameof(ContextDetail), typeof(string), typeof(TitleBar),
        new PropertyMetadata("", OnCrumbChanged));

    /// <summary>
    /// Raised when the operator asks for the command palette from the pill. The
    /// host owns the palette (it is built on that window's view model), so the
    /// control only forwards the intent.
    /// </summary>
    public event EventHandler? CommandRequested;

    public TitleBar()
    {
        InitializeComponent();
        SyncCrumbs();

        Loaded += (_, _) => AttachWindowProc();
        Unloaded += (_, _) => DetachWindowProc();
    }

    /// <summary>
    /// UX-16 item A: while a floating surface covers the shell, the crumbs and
    /// the Ctrl+K pill take no pointer input, no tab stop and no control-view
    /// UI Automation node, exactly like the body behind the scrim. The three
    /// caption buttons stay live, so the window can still be minimised and
    /// closed while the drawer or a dialog is open.
    /// </summary>
    public void SetContentInert(bool inert)
    {
        SetInert(CrumbBar, ref _crumbState, inert);
        SetInert(CommandPill, ref _pillState, inert);
    }

    private (bool HitTest, bool Enabled, KeyboardNavigationMode Tab)? _crumbState;
    private (bool HitTest, bool Enabled, KeyboardNavigationMode Tab)? _pillState;

    private static void SetInert(
        UIElement element,
        ref (bool HitTest, bool Enabled, KeyboardNavigationMode Tab)? state,
        bool inert)
    {
        if (inert)
        {
            state ??= (element.IsHitTestVisible,
                element.IsEnabled,
                KeyboardNavigation.GetTabNavigation(element));
            element.IsHitTestVisible = false;
            element.IsEnabled = false;
            KeyboardNavigation.SetTabNavigation(element, KeyboardNavigationMode.None);
            return;
        }

        if (state is { } restore)
        {
            state = null;
            element.IsHitTestVisible = restore.HitTest;
            element.IsEnabled = restore.Enabled;
            KeyboardNavigation.SetTabNavigation(element, restore.Tab);
        }
    }

    public string PageName
    {
        get => (string)GetValue(PageNameProperty);
        set => SetValue(PageNameProperty, value);
    }

    public string ContextName
    {
        get => (string)GetValue(ContextNameProperty);
        set => SetValue(ContextNameProperty, value);
    }

    public string ContextDetail
    {
        get => (string)GetValue(ContextDetailProperty);
        set => SetValue(ContextDetailProperty, value);
    }

    /// <summary>
    /// Hides the strip and takes its caption height with it, so fullscreen is a
    /// real fullscreen instead of an invisible 44 DIP drag band.
    /// </summary>
    public void SetFullscreen(bool fullscreen)
    {
        Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        if (Window.GetWindow(this) is { } window
            && WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.CaptionHeight = fullscreen ? 0 : BarHeight;
        }
    }

    private static void OnCrumbChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TitleBar)d).SyncCrumbs();

    /// <summary>
    /// An empty crumb is not drawn at all: the separators, the device name, the
    /// interpunct and the API label appear only for real state, so an empty
    /// workspace shows "Studio" and never "Studio > > API ".
    /// </summary>
    private void SyncCrumbs()
    {
        var page = (PageName ?? "").Trim();
        var context = (ContextName ?? "").Trim();
        var detail = (ContextDetail ?? "").Trim();

        PageCrumb.Text = page;
        PageCrumb.Visibility = page.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        ContextGroup.Visibility = context.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContextNameText.Text = context;
        ContextNameText.ToolTip = context.Length > 0 ? context : null;

        ContextDot.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContextDetailText.Text = detail;
        ContextDetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// UX-14: the non-client hook is per window, so both shell windows get their
    /// own copy of the same hit test and state machine.
    /// </summary>
    private void AttachWindowProc()
    {
        if (!SnapLayoutsAvailable || _source is not null)
        {
            return;
        }

        if (Window.GetWindow(this) is not { } window)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnWindowMessage);
    }

    private void DetachWindowProc()
    {
        if (_source is null)
        {
            return;
        }

        _source.RemoveHook(OnWindowMessage);
        _source = null;
        _trackingLeave = false;
        SetMaxHot(false);
        SetMaxPressed(false);
    }

    /// <summary>
    /// WM_NCHITTEST answers HTMAXBUTTON for the maximize button's rectangle in
    /// physical screen pixels, which is what makes Windows 11 draw the Snap
    /// Layouts flyout over this custom caption. Everything else falls through to
    /// WindowChrome and the window manager, so drag, double-click, Snap and the
    /// system menu keep working.
    ///
    /// Because the mouse is then the window manager's, WPF raises neither
    /// IsMouseOver nor IsPressed here: the four non-client mouse messages stand in
    /// for them, and the button up over the rectangle is the click.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest:
                if (IsOverMaxButton(lParam))
                {
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }

                break;

            case WmMove:
            case WmSize:
            case WmDpiChanged:
                // The strip moves with the frame, the DPI and the maximize inset.
                _maxButtonBoundsValid = false;
                break;

            case WmNcMouseMove:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    TrackLeave(hwnd);
                    SetMaxHot(true);
                }
                else
                {
                    SetMaxHot(false);
                    SetMaxPressed(false);
                }

                break;

            case WmNcMouseLeave:
                _trackingLeave = false;
                SetMaxHot(false);
                SetMaxPressed(false);
                break;

            case WmNcLButtonDown:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    // Swallowing the caption button's own press also swallows the
                    // activation it would have done, so do that here.
                    if (Window.GetWindow(this) is { IsActive: false } inactive)
                    {
                        inactive.Activate();
                    }

                    SetMaxHot(true);
                    SetMaxPressed(true);
                    handled = true;
                }

                break;

            case WmNcLButtonUp:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    var wasPressed = _maxPressed;
                    SetMaxPressed(false);
                    handled = true;
                    if (wasPressed)
                    {
                        ToggleMaximize();
                    }
                }
                else
                {
                    SetMaxPressed(false);
                }

                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The maximize button's rectangle in physical screen pixels. PointToScreen
    /// already answers in device pixels; only the DIP size needs the window's own
    /// transform, so a 125 % or 200 % host still hit-tests the real rectangle.
    /// </summary>
    private bool IsOverMaxButton(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible || MaximizeButton.ActualWidth <= 0 || MaximizeButton.ActualHeight <= 0)
        {
            return false;
        }

        var packed = lParam.ToInt64();
        var x = unchecked((short)(packed & 0xFFFF));
        var y = unchecked((short)((packed >> 16) & 0xFFFF));

        return MaxButtonBounds().Contains(x, y);
    }

    private Rect MaxButtonBounds()
    {
        if (_maxButtonBoundsValid)
        {
            return _maxButtonBounds;
        }

        _maxButtonBounds = Rect.Empty;
        _maxButtonBoundsValid = true;

        if (PresentationSource.FromVisual(MaximizeButton) is not { CompositionTarget: { } target })
        {
            _maxButtonBoundsValid = false;
            return Rect.Empty;
        }

        var origin = MaximizeButton.PointToScreen(new Point(0, 0));
        var size = target.TransformToDevice.Transform(
            new Point(MaximizeButton.ActualWidth, MaximizeButton.ActualHeight));

        if (size.X <= 0 || size.Y <= 0)
        {
            return Rect.Empty;
        }

        _maxButtonBounds = new Rect(origin.X, origin.Y, size.X, size.Y);
        return _maxButtonBounds;
    }

    private void TrackLeave(IntPtr hwnd)
    {
        if (_trackingLeave)
        {
            return;
        }

        var track = new TrackMouseEventData
        {
            Size = Marshal.SizeOf<TrackMouseEventData>(),
            Flags = TmeLeave | TmeNonClient,
            HwndTrack = hwnd,
        };

        _trackingLeave = TrackMouseEvent(ref track);
    }

    private void SetMaxHot(bool hot)
    {
        if (_maxHot == hot)
        {
            return;
        }

        _maxHot = hot;
        Motion.SetCaptionHot(MaximizeButton, hot);
    }

    private void SetMaxPressed(bool pressed)
    {
        if (_maxPressed == pressed)
        {
            return;
        }

        _maxPressed = pressed;
        Motion.SetCaptionPressed(MaximizeButton, pressed);
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            SystemCommands.MinimizeWindow(window);
        }
    }

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>
    /// The one maximize/restore path: the keyboard's Click and the non-client
    /// button up over the HTMAXBUTTON rectangle both land here, so a click can
    /// never toggle twice.
    /// </summary>
    private void ToggleMaximize()
    {
        if (Window.GetWindow(this) is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(window);
        }
        else
        {
            SystemCommands.MaximizeWindow(window);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            SystemCommands.CloseWindow(window);
        }
    }

    private void OnCommand(object sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(this, EventArgs.Empty);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public int Size;
        public int Flags;
        public IntPtr HwndTrack;
        public int HoverTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData data);
}
