using System.Windows;
using System.Windows.Controls;
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
/// </summary>
public partial class TitleBar : UserControl
{
    /// <summary>The visible height of the strip; also the window's caption height.</summary>
    public const double BarHeight = 44;

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

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            SystemCommands.MinimizeWindow(window);
        }
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
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
}
