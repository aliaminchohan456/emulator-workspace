using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmulatorWorkspace.Desktop.Services;
using EmulatorWorkspace.Desktop.ViewModels;

namespace EmulatorWorkspace.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Point? _dragStart;
    private Point? _dragCurrent;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Initialize();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            UpdateViewportSize();
            Services.DesktopLog.Write(
                $"layout window={ActualWidth:N0} body={BodyGrid.ActualWidth:N0} viewport={ViewportScroll.ActualWidth:N0} inspector={InspectorPanel.ActualWidth:N0} dpi={VisualTreeHelper.GetDpi(this).DpiScaleX}");
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.Frame)
            or nameof(MainViewModel.ZoomMode)
            or nameof(MainViewModel.ZoomFactor))
        {
            UpdateViewportSize();
        }
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewportSize();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateViewportSize();
    }

    /// <summary>
    /// Sizes the guest image independently of the window: Fit scales into the
    /// viewport, 1:1 maps one guest pixel to one physical pixel, and zoom
    /// multiplies that. The guest resolution itself is never changed here.
    /// </summary>
    private void UpdateViewportSize()
    {
        var frame = _viewModel.Frame;
        if (frame is null)
        {
            return;
        }

        var viewport = ViewportScroll;
        var availableWidth = Math.Max(80, viewport.ActualWidth - 32);
        var availableHeight = Math.Max(80, viewport.ActualHeight - 32);
        var dpi = VisualTreeHelper.GetDpi(this);

        double scale = _viewModel.ZoomMode switch
        {
            "Fit" => Math.Min(availableWidth / frame.PixelWidth, availableHeight / frame.PixelHeight),
            "Actual" => 1.0 / dpi.DpiScaleX,
            _ => _viewModel.ZoomFactor / dpi.DpiScaleX,
        };

        scale = Math.Max(0.05, scale);
        ViewportImage.Width = Math.Max(1, frame.PixelWidth * scale);
        ViewportImage.Height = Math.Max(1, frame.PixelHeight * scale);
        ViewportCanvas.Width = ViewportImage.Width;
        ViewportCanvas.Height = ViewportImage.Height;
    }

    private bool TryMapToGuest(Point point, out int x, out int y)
    {
        x = 0;
        y = 0;
        var frame = _viewModel.Frame;
        var guestWidth = _viewModel.GuestWidth > 0 ? _viewModel.GuestWidth : frame?.PixelWidth ?? 0;
        var guestHeight = _viewModel.GuestHeight > 0 ? _viewModel.GuestHeight : frame?.PixelHeight ?? 0;
        if (frame is null || guestWidth <= 0 || guestHeight <= 0 || ViewportImage.ActualWidth <= 0 || ViewportImage.ActualHeight <= 0)
        {
            return false;
        }

        if (point.X < 0 || point.Y < 0 || point.X > ViewportImage.ActualWidth || point.Y > ViewportImage.ActualHeight)
        {
            return false;
        }

        x = (int)Math.Round(point.X / ViewportImage.ActualWidth * guestWidth);
        y = (int)Math.Round(point.Y / ViewportImage.ActualHeight * guestHeight);
        x = Math.Clamp(x, 0, guestWidth - 1);
        y = Math.Clamp(y, 0, guestHeight - 1);
        return true;
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryMapToGuest(e.GetPosition(ViewportImage), out _, out _))
        {
            return;
        }

        _dragStart = e.GetPosition(ViewportImage);
        _dragCurrent = _dragStart;
        ViewportImage.CaptureMouse();
        e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            _dragCurrent = e.GetPosition(ViewportImage);
        }
    }

    private async void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start)
        {
            return;
        }

        var end = e.GetPosition(ViewportImage);
        _dragStart = null;
        _dragCurrent = null;
        ViewportImage.ReleaseMouseCapture();

        if (!TryMapToGuest(start, out var x1, out var y1) || !TryMapToGuest(end, out var x2, out var y2))
        {
            return;
        }

        var distance = (end - start).Length;
        if (distance < 12)
        {
            await _viewModel.SendTapAsync(x1, y1);
        }
        else
        {
            await _viewModel.SendSwipeAsync(x1, y1, x2, y2);
        }
    }

    private void OnFit(object sender, RoutedEventArgs e)
    {
        _viewModel.ZoomMode = "Fit";
        UpdateViewportSize();
    }

    private void OnActualSize(object sender, RoutedEventArgs e)
    {
        _viewModel.ZoomMode = "Actual";
        UpdateViewportSize();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(_viewModel.ZoomFactor * 1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(_viewModel.ZoomFactor / 1.25);

    private void SetZoom(double value)
    {
        _viewModel.ZoomMode = "Zoom";
        _viewModel.ZoomFactor = Math.Clamp(value, 0.25, 4.0);
        UpdateViewportSize();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetZoom(e.Delta > 0 ? _viewModel.ZoomFactor * 1.1 : _viewModel.ZoomFactor / 1.1);
            e.Handled = true;
            return;
        }

        base.OnMouseWheel(e);
    }

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(MainViewModel.Frame))
        {
            Source = _viewModel,
        });

        var window = new Window
        {
            Title = "Emulator Workspace - pop-out viewport",
            Width = 520,
            Height = 900,
            Owner = this,
            Content = image,
            Background = (Brush)FindResource("LogBg"),
        };
        window.Show();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var apk = files.FirstOrDefault(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        if (apk is null)
        {
            return;
        }

        await _viewModel.InstallApkAsync(Path.GetFullPath(apk));
    }

    private void OnFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private bool _fullscreen;

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _fullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _ = _viewModel.DisposeAsync();
    }
}
