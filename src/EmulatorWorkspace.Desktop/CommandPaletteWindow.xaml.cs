using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EmulatorWorkspace.Desktop.ViewModels;

namespace EmulatorWorkspace.Desktop;

/// <summary>
/// Keyboard command palette (Ctrl+K and the rail search icon). It lists the
/// operator actions of the desktop and runs them through the same commands the
/// toolbar uses, so the palette cannot reach anything the UI does not already
/// expose. UX-07 rebuilt the surface around mockup 04: a dimmed studio, one
/// 640 DIP floating panel, grouped rows that flatten to a ranked list while
/// searching, a leading search field and a footer of the keys that really work.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private const int EnterMilliseconds = 160;
    private const int ExitMilliseconds = 100;

    /// <summary>96 DIP above the panel plus 24 DIP below it, inside the owner.</summary>
    private const double PanelMaxHeightAllowance = 120;

    private const double PanelMaxWidth = 640;
    private const double PanelMinWidth = 380;

    /// <summary>The panel never comes closer than 32 DIP to either side.</summary>
    private const double PanelSideMargin = 64;

    private readonly List<PaletteEntryViewModel> _all;
    private readonly IInputElement? _invoker;
    private bool _closing;

    /// <param name="viewModel">The studio view model the commands live on.</param>
    /// <param name="invoker">
    /// The element that had keyboard focus when the palette was opened. Esc, the
    /// scrim and every other close hand focus back to it, so a keyboard operator
    /// lands where they left off instead of at the top of the window.
    /// </param>
    public CommandPaletteWindow(MainViewModel viewModel, IInputElement? invoker = null)
    {
        InitializeComponent();
        Services.ThemeManager.Initialize();
        Services.Chrome.Initialize();
        Services.Chrome.Apply(this);
        DataContext = viewModel;
        _all = viewModel.Palette.ToList();
        _invoker = invoker;
        Filter.TextChanged += (_, _) => Apply();
        // PreviewKeyDown, not KeyDown: the TextBox's own class handler consumes
        // Up/Down/Home/End for caret movement before an instance KeyDown handler
        // would ever see them, so the list navigation has to tunnel first.
        Filter.PreviewKeyDown += OnFilterKeyDown;
        PreviewKeyDown += OnPreviewKeyDown;
        Results.PreviewMouseMove += OnResultsMouseMove;
        Results.PreviewMouseLeftButtonUp += OnResultsClick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// UX-07: the window is sized to the owner's client area so the scrim covers
    /// the studio and nothing else (never the title bar), then the first result
    /// is selected and the panel animates in.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceOverOwner();
        if (Owner is { } owner)
        {
            owner.LocationChanged += OnOwnerChanged;
            owner.SizeChanged += OnOwnerSizeChanged;
        }

        SizePanel();
        Apply();
        Filter.Focus();
        AnimateOpen();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Owner is { } owner)
        {
            owner.LocationChanged -= OnOwnerChanged;
            owner.SizeChanged -= OnOwnerSizeChanged;
        }

        // Focus returns to whatever opened the palette (the rail search button or
        // the control that had focus when Ctrl+K was pressed).
        if (_invoker is UIElement { IsVisible: true, IsEnabled: true } invoker)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(invoker)));
        }
    }

    private void OnOwnerChanged(object? sender, EventArgs e) => PlaceOverOwner();

    private void OnOwnerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PlaceOverOwner();
        SizePanel();
    }

    /// <summary>
    /// Puts the overlay exactly over the owner's client area, in device pixels
    /// converted back to DIPs so a 125 % desktop lands on the same rectangle.
    /// The owner's content element is the client area: using the Window instead
    /// would include the title bar and push the panel's bottom inset off screen.
    /// Without an owner (never the case from the studio) it stays where the
    /// window manager put it.
    /// </summary>
    private void PlaceOverOwner()
    {
        if (Owner is not { IsLoaded: true } owner)
        {
            return;
        }

        var anchor = owner.Content as FrameworkElement ?? owner;
        var origin = anchor.PointToScreen(new Point(0, 0));
        if (PresentationSource.FromVisual(anchor)?.CompositionTarget is { } target)
        {
            origin = target.TransformFromDevice.Transform(origin);
        }

        Left = origin.X;
        Top = origin.Y;
        Width = anchor.ActualWidth;
        Height = anchor.ActualHeight;
    }

    /// <summary>
    /// The panel is 640 DIP wide and never wider than the window minus 64 DIP, so
    /// the scrim keeps a visible strip on both sides at 1024. Its height leaves
    /// the mockup's 96 DIP top offset and 24 DIP bottom inset free, and the list
    /// itself is capped at 420 by the XAML.
    /// </summary>
    private void SizePanel()
    {
        PaletteCard.Width = Math.Clamp(Width - PanelSideMargin, PanelMinWidth, PanelMaxWidth);
        PaletteCard.MaxHeight = Math.Max(320, Height - PanelMaxHeightAllowance);
    }

    /// <summary>
    /// 160 ms: the panel fades in and settles from .98 scale while the scrim
    /// fades in with it. With Windows reduced motion on there is no storyboard at
    /// all and the palette is simply there - an instant fade, no movement.
    /// </summary>
    private void AnimateOpen()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            PaletteCard.Opacity = 1;
            ScrimLayer.Opacity = 1;
            PaletteScale.ScaleX = 1;
            PaletteScale.ScaleY = 1;
            return;
        }

        PaletteCard.BeginAnimation(UIElement.OpacityProperty, Spline(1, EnterMilliseconds));
        ScrimLayer.BeginAnimation(UIElement.OpacityProperty, Spline(1, EnterMilliseconds));
        PaletteScale.BeginAnimation(ScaleTransform.ScaleXProperty, Spline(1, EnterMilliseconds));
        PaletteScale.BeginAnimation(ScaleTransform.ScaleYProperty, Spline(1, EnterMilliseconds));
    }

    /// <summary>100 ms fade back out, then the window closes.</summary>
    private void CloseAnimated()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var fade = Spline(0, ExitMilliseconds);
        fade.Completed += (_, _) => Close();
        PaletteCard.BeginAnimation(UIElement.OpacityProperty, fade);
        ScrimLayer.BeginAnimation(UIElement.OpacityProperty, Spline(0, ExitMilliseconds));
    }

    /// <summary>
    /// The palette's one motion curve. A key-frame animation that starts at the
    /// element's current value and lands on <paramref name="to"/> at the end of
    /// the duration, eased with the design guide's dialog spline.
    /// </summary>
    private static DoubleAnimationUsingKeyFrames Spline(double to, int milliseconds)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var timeline = new DoubleAnimationUsingKeyFrames { Duration = duration };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(duration.TimeSpan),
            new KeySpline(0.22, 1, 0.36, 1)));
        return timeline;
    }

    /// <summary>
    /// Rebuilds the list. Every entry re-reads its command's predicate and, when
    /// it is false, the reason chip. No query keeps the mockup's groups; a query
    /// collapses them into one flat ranked list with the matched characters
    /// marked for the accent colour.
    /// </summary>
    private void Apply()
    {
        foreach (var entry in _all)
        {
            entry.Refresh();
            entry.LabelHits = Array.Empty<int>();
            entry.HintHits = Array.Empty<int>();
        }

        var query = PaletteQuery.Normalize(Filter.Text);
        List<PaletteEntryViewModel> items;

        if (query.Length == 0)
        {
            var sections = PaletteQuery.Group(_all, entry => entry.Group);
            items = sections.SelectMany(section => section.Items).ToList();
            var view = new ListCollectionView(items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PaletteEntryViewModel.Group)));
            Results.ItemsSource = view;
        }
        else
        {
            var hits = PaletteQuery.Rank(_all, query, entry => entry.Label, entry => entry.Hint);
            items = new List<PaletteEntryViewModel>(hits.Count);
            foreach (var hit in hits)
            {
                hit.Item.LabelHits = hit.LabelHits;
                hit.Item.HintHits = hit.HintHits;
                items.Add(hit.Item);
            }

            Results.ItemsSource = items;
        }

        var empty = items.Count == 0;
        Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        PaletteEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            PaletteEmptyText.Text = query.Length == 0
                ? "No commands available"
                : $"No commands match \u201c{query}\u201d";
            return;
        }

        // The first command that can actually run takes the highlight; when the
        // query leaves only rows their own command refuses, the first row is
        // still highlighted so the reason chip next to it can explain why.
        Results.SelectedItem = items.FirstOrDefault(entry => entry.CanRun) ?? items[0];
        ScrollSelectionIntoView();
    }

    private void ScrollSelectionIntoView()
    {
        if (Results.SelectedItem is { } selected)
        {
            Results.ScrollIntoView(selected);
        }
    }

    /// <summary>
    /// Every key the footer advertises, handled from the search field: Up/Down
    /// wrap through the rows, Home/End jump, PageUp/PageDown move by a screen,
    /// Enter runs and Esc closes. Keys that do not move the selection are left
    /// alone so typing, IME composition and text editing keep working.
    /// </summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Home:
                SelectIndex(0);
                e.Handled = true;
                break;
            case Key.End:
                SelectIndex(Results.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-5);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(5);
                e.Handled = true;
                break;
            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseAnimated();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Escape works from anywhere in the palette, not only the field.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        CloseAnimated();
        e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        var count = Results.Items.Count;
        if (count == 0)
        {
            return;
        }

        var current = Results.SelectedIndex < 0 ? 0 : Results.SelectedIndex;
        var next = ((current + delta) % count + count) % count;
        SelectIndex(next);
    }

    private void SelectIndex(int index)
    {
        if (Results.Items.Count == 0)
        {
            return;
        }

        Results.SelectedIndex = Math.Clamp(index, 0, Results.Items.Count - 1);
        ScrollSelectionIntoView();
    }

    /// <summary>The pointer moves the one highlight, like the mockup's rows.</summary>
    private void OnResultsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(Results, source) is ListBoxItem { IsSelected: false } item)
        {
            Results.SelectedItem = item.DataContext;
        }
    }

    /// <summary>
    /// A single click runs the row under the pointer. A disabled row only takes
    /// the highlight (its reason chip is the answer); a click on the scrollbar or
    /// on a group header has no container and does nothing.
    /// </summary>
    private void OnResultsClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(Results, source) is ListBoxItem item)
        {
            Results.SelectedItem = item.DataContext;
            RunSelected();
            e.Handled = true;
        }
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        CloseAnimated();
        e.Handled = true;
    }

    /// <summary>
    /// Runs the highlighted command and closes. A row whose own predicate is
    /// false does nothing but keep the highlight, so the reason chip stays
    /// readable. The command is dispatched after the modal loop unwinds, so a
    /// command that opens its own dialog (Install APK, Build APK set) owns that
    /// dialog from the studio, not from a palette window that is already gone.
    /// </summary>
    private void RunSelected()
    {
        if (Results.SelectedItem is not PaletteEntryViewModel entry || !entry.CanRun)
        {
            return;
        }

        Close();
        Dispatcher.InvokeAsync(entry.Invoke, DispatcherPriority.Background);
    }
}
