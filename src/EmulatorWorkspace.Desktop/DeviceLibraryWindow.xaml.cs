using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EmulatorWorkspace.Desktop.Services;
using EmulatorWorkspace.Desktop.ViewModels;

namespace EmulatorWorkspace.Desktop;

/// <summary>
/// UX-04 device library window: searchable/sortable cards, the create and clone
/// flows and compatibility-aware recovery points. Every call goes through the
/// same supervisor pipe and policy as the Studio window, the CLI and the MCP
/// client, and the window owns its own connection so a failed browse never
/// disturbs the running guest.
/// </summary>
public partial class DeviceLibraryWindow : Window
{
    /// <summary>The design's drawer width, and the margin a squeezed window keeps.</summary>
    private const double DrawerMaxWidth = 460;
    private const double DrawerPageInset = 48;

    private static readonly TimeSpan DrawerSlide = TimeSpan.FromMilliseconds(220);

    private readonly DeviceLibraryViewModel _viewModel;

    /// <summary>Element that had focus when a drawer opened; focused again on close.</summary>
    private IInputElement? _drawerFocusReturn;

    public DeviceLibraryWindow()
    {
        InitializeComponent();
        ThemeManager.Initialize();
        Chrome.Initialize();
        Chrome.Apply(this);

        _viewModel = new DeviceLibraryViewModel();
        DataContext = _viewModel;
        _viewModel.OpenInStudioRequested += OnOpenInStudio;
        _viewModel.CloseRequested += OnCloseRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        SizeChanged += (_, _) => UpdateDrawerWidth();

        Loaded += async (_, _) =>
        {
            UpdateDrawerWidth();
            await _viewModel.InitializeAsync();
            DesktopLog.Write($"library loaded cards={_viewModel.Tiles.Count}");
        };
    }

    public DeviceLibraryViewModel ViewModel => _viewModel;

    /// <summary>Raised when the operator chose "Open in Studio" for a guest.</summary>
    public event Action<string>? DeviceChosen;

    /// <summary>
    /// UX-04 evidence hook. It opens one of the library's own dialogs through
    /// the same view-model entry point the card menu calls, so a covered host
    /// that cannot deliver pointer input or a context menu can still be
    /// captured. It never creates, clones, deletes or restores anything by
    /// itself; the dialog it opens does the real work only when the operator
    /// confirms. Spec: <c>create</c>, <c>clone:&lt;device&gt;</c>,
    /// <c>recovery:&lt;device&gt;</c>, <c>reset:&lt;device&gt;</c>,
    /// <c>delete:&lt;device&gt;</c>.
    /// </summary>
    public async Task RunEvidenceHookAsync(string spec)
    {
        var parts = spec.Split(':', 2);
        var action = parts[0].Trim().ToLowerInvariant();
        var device = parts.Length > 1 ? parts[1].Trim() : "";

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && _viewModel.IsLoading)
        {
            await Task.Delay(300);
        }

        LibraryCard? card = null;
        if (device.Length > 0)
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                card = _viewModel.Tiles.FirstOrDefault(tile => !tile.IsPlaceholder && tile.Name == device);
                if (card is not null)
                {
                    break;
                }

                await Task.Delay(300);
            }
        }
        switch (action)
        {
            case "create":
                _viewModel.NewDeviceCommand.Execute(null);
                break;
            case "clone" when card is not null:
                await _viewModel.OpenCloneForAsync(card);
                break;
            case "recovery" when card is not null:
                await _viewModel.OpenRecoveryForAsync(card);
                break;
            case "reset" when card is not null:
                _viewModel.ConfirmResetFor(card);
                break;
            case "delete" when card is not null:
                _viewModel.ConfirmDeleteFor(card);
                break;
            default:
                DesktopLog.Write("library hook: nothing to do for " + spec);
                break;
        }

        DesktopLog.Write("library hook: " + spec);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.OpenInStudioRequested -= OnOpenInStudio;
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _ = _viewModel.DisposeAsync().AsTask();
        base.OnClosed(e);
    }

    private void OnOpenInStudio(string name)
    {
        DeviceChosen?.Invoke(name);
        Close();
    }

    private void OnCloseRequested() => Close();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DeviceLibraryViewModel.IsCreateOpen))
        {
            if (_viewModel.IsCreateOpen)
            {
                OpenDrawer(CreateDrawerSlide, CreateScrim, CreateNameBox);
            }
            else
            {
                CloseDrawer(CreateDrawerSlide, CreateScrim);
            }
        }
        else if (args.PropertyName == nameof(DeviceLibraryViewModel.IsCloneOpen))
        {
            if (_viewModel.IsCloneOpen)
            {
                OpenDrawer(CloneDrawerSlide, CloneScrim, CloneNameBox);
            }
            else
            {
                CloseDrawer(CloneDrawerSlide, CloneScrim);
            }
        }
    }

    /// <summary>
    /// Width of a slide-over drawer: the design's 460 DIP, or window width minus
    /// 48 DIP when the window is narrower, so the panel never fills the page.
    /// </summary>
    private void UpdateDrawerWidth()
    {
        var width = Math.Min(DrawerMaxWidth, Math.Max(320, ActualWidth - DrawerPageInset));
        CreateDrawerShell.Width = width;
        CloneDrawerShell.Width = width;
    }

    /// <summary>
    /// Slide the drawer in from the right over 220 ms on
    /// cubic-bezier(0.22,1,0.36,1) with the scrim fading in alongside, then put
    /// the caret in the name field. With Windows reduced motion on, the drawer
    /// appears in place and no storyboard is created.
    /// </summary>
    private void OpenDrawer(TranslateTransform slide, Border scrim, FrameworkElement initialFocus)
    {
        _drawerFocusReturn = Keyboard.FocusedElement;
        SetPageInert(true);
        UpdateDrawerWidth();

        slide.BeginAnimation(TranslateTransform.XProperty, null);
        scrim.BeginAnimation(OpacityProperty, null);

        if (SystemParameters.ClientAreaAnimation)
        {
            slide.X = CreateDrawerShell.Width;
            scrim.Opacity = 0;
            slide.BeginAnimation(TranslateTransform.XProperty, DrawerTimeline(0));
            scrim.BeginAnimation(OpacityProperty, DrawerTimeline(1));
        }
        else
        {
            slide.X = 0;
            scrim.Opacity = 1;
        }

        FocusLater(initialFocus);
    }

    /// <summary>
    /// Reset the drawer transform for the next open, hand the page back and
    /// return focus to the control that invoked the drawer.
    /// </summary>
    private void CloseDrawer(TranslateTransform slide, Border scrim)
    {
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        scrim.BeginAnimation(OpacityProperty, null);
        slide.X = 0;
        scrim.Opacity = 1;

        SetPageInert(_viewModel.IsCreateOpen || _viewModel.IsCloneOpen);
        RestoreDrawerFocus();
    }

    private static DoubleAnimationUsingKeyFrames DrawerTimeline(double to)
    {
        var timeline = new DoubleAnimationUsingKeyFrames { Duration = new Duration(DrawerSlide) };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(DrawerSlide),
            new KeySpline(0.22, 1, 0.36, 1)));
        return timeline;
    }

    /// <summary>
    /// The page and rail behind the scrim take no pointer input and no tab stop
    /// while a drawer is open; the drawer host cycles Tab inside itself.
    /// </summary>
    private void SetPageInert(bool inert)
    {
        var mode = inert ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue;
        NavRail.IsHitTestVisible = !inert;
        LibraryPage.IsHitTestVisible = !inert;
        KeyboardNavigation.SetTabNavigation(NavRail, mode);
        KeyboardNavigation.SetTabNavigation(LibraryPage, mode);
    }

    private void RestoreDrawerFocus()
    {
        var target = _drawerFocusReturn;
        _drawerFocusReturn = null;
        if (target is UIElement element && element.IsVisible && element.Focus())
        {
            return;
        }

        if (target is FrameworkContentElement content)
        {
            content.Focus();
            return;
        }

        NewDeviceButton.Focus();
    }

    private void FocusLater(FrameworkElement element) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            element.Focus();
            if (element is TextBox textBox)
            {
                textBox.SelectAll();
            }
        }));

    /// <summary>A click on the scrim closes the drawer, like Escape.</summary>
    private void OnCreateScrimClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.CloseCreateCommand.Execute(null);
    }

    private void OnCloneScrimClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.CloseCloneCommand.Execute(null);
    }

    /// <summary>
    /// A left click on the "more" button opens the same context menu that
    /// Shift+F10 opens for a focused control, so the menu is reachable by mouse
    /// and keyboard without a second menu implementation.
    /// </summary>
    private void OnCardMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Escape closes the topmost overlay without touching the guest.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel.IsConfirmOpen)
        {
            _viewModel.CancelConfirmCommand.Execute(null);
        }
        else if (_viewModel.IsCreateOpen)
        {
            _viewModel.CloseCreateCommand.Execute(null);
        }
        else if (_viewModel.IsCloneOpen)
        {
            _viewModel.CloseCloneCommand.Execute(null);
        }
        else if (_viewModel.IsRecoveryOpen)
        {
            _viewModel.CloseRecoveryCommand.Execute(null);
        }
        else
        {
            Close();
        }

        e.Handled = true;
    }
}
