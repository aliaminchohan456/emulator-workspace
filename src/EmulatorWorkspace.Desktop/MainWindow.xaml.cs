using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Desktop.Services;
using EmulatorWorkspace.Desktop.ViewModels;

namespace EmulatorWorkspace.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Point? _dragStart;
    private (string? Subscription, int GuestWidth, int GuestHeight, double Width, double Height) _dragViewport;

    /// <summary>The view model behind the window, for the automation modes.</summary>
    public MainViewModel ViewModel => _viewModel;

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

    public async Task<int> RunLatencyProbeAsync(int count, string outPath, int frameTimeoutSeconds = 45)
    {
        Topmost = true;
        WindowState = WindowState.Normal;
        Activate();
        var frameDeadline = DateTimeOffset.UtcNow.AddSeconds(frameTimeoutSeconds);
        while (DateTimeOffset.UtcNow < frameDeadline
               && (!_viewModel.IsConnected || !_viewModel.HasFrame))
        {
            await Task.Delay(250);
        }

        if (!_viewModel.HasFrame)
        {
            Services.DesktopLog.Write("Latency probe timed out waiting for the guest frame.");
            return 2;
        }

        var samples = new JsonArray();
        var values = new List<double>();
        var timeouts = 0;
        for (var trial = 1; trial <= count; trial++)
        {
            var latency = await _viewModel.ProbeTapAsync();
            Services.DesktopLog.Write($"probe trial {trial} latency={(latency?.CompositionMs.ToString("N1") ?? "timeout")}");
            if (latency is null)
            {
                timeouts++;
                samples.Add(new JsonObject { ["trial"] = trial, ["timeout"] = true });
            }
            else
            {
                values.Add(latency.CompositionMs);
                samples.Add(new JsonObject
                {
                    ["trial"] = trial,
                    ["ms"] = Math.Round(latency.CompositionMs, 2),
                    ["frameAvailableMs"] = Math.Round(latency.FrameAvailableMs, 2),
                    ["tapAcknowledgedMs"] = Math.Round(latency.TapAcknowledgedMs, 2),
                    ["controllerSetupMs"] = latency.ControllerSetupMs,
                    ["controllerTapMs"] = latency.ControllerTapMs,
                    ["supervisorFrameWriteMs"] = latency.SupervisorFrameWriteMs,
                    ["changedFrameReceivedMs"] = latency.ChangedFrameReceivedMs,
                    ["frameDecodeAndDispatchMs"] = latency.FrameDecodeAndDispatchMs,
                    ["timeout"] = false,
                });
            }

            await Task.Delay(220);

            var partial = new JsonObject
            {
                ["trials"] = trial,
                ["valid"] = values.Count,
                ["timeouts"] = timeouts,
                ["samples"] = samples.DeepClone(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            await File.WriteAllTextAsync(outPath + ".partial",
                partial.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        values.Sort();
        double Percentile(double fraction)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            var index = Math.Clamp((int)Math.Ceiling(fraction * values.Count) - 1, 0, values.Count - 1);
            return Math.Round(values[index], 2);
        }

        var trace = await _viewModel.GetFrameTraceAsync();
        var summary = new JsonObject
        {
            ["mode"] = _viewModel.GpuMode,
            ["compositionProxy"] = true,
            ["trials"] = count,
            ["valid"] = values.Count,
            ["timeouts"] = timeouts,
            ["p50Ms"] = Percentile(0.50),
            ["p95Ms"] = Percentile(0.95),
            ["minMs"] = values.Count == 0 ? 0 : Math.Round(values[0], 2),
            ["maxMs"] = values.Count == 0 ? 0 : Math.Round(values[^1], 2),
            ["deliveredWidth"] = _viewModel.DeliveredFrameWidth,
            ["deliveredHeight"] = _viewModel.DeliveredFrameHeight,
            ["deliveredFps"] = Math.Round(_viewModel.DeliveredFps, 2),
            ["dropped"] = _viewModel.DroppedFrames,
            ["guestWidth"] = _viewModel.GuestWidth,
            ["guestHeight"] = _viewModel.GuestHeight,
            ["frameTrace"] = trace,
            ["samples"] = samples,
            ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, summary.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.FrameWidth)
            or nameof(MainViewModel.FrameHeight)
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
        _dragViewport = CurrentViewport;
        ViewportImage.CaptureMouse();
        e.Handled = true;
    }

    private (string? Subscription, int GuestWidth, int GuestHeight, double Width, double Height) CurrentViewport =>
        (_viewModel.ViewportSubscription, _viewModel.GuestWidth, _viewModel.GuestHeight,
            ViewportImage.ActualWidth, ViewportImage.ActualHeight);

    private async void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start)
        {
            return;
        }

        var end = e.GetPosition(ViewportImage);
        _dragStart = null;
        ViewportImage.ReleaseMouseCapture();

        if (_dragViewport != CurrentViewport || !TryMapToGuest(start, out var x1, out var y1)
            || !TryMapToGuest(end, out var x2, out var y2))
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

    private void OnAgents(object sender, RoutedEventArgs e)
    {
        var window = new AgentSessionsWindow { Owner = this };
        window.ShowDialog();
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

    /// <summary>
    /// Handles a file that was dropped on the window. The drop event and this
    /// entry point share the same code, so a deployment through the shell and
    /// a deployment through the automation entry point behave identically.
    /// </summary>
    public async Task DeliverFileDropAsync(IEnumerable<string> files)
    {
        var apk = files.FirstOrDefault(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        if (apk is null)
        {
            Services.DesktopLog.Write("drop with no APK: " + string.Join(", ", files));
            return;
        }

        Services.DesktopLog.Write("drop accepted: " + apk);
        await _viewModel.InstallApkAsync(Path.GetFullPath(apk)).ConfigureAwait(true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepted = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (accepted)
        {
            Services.DesktopLog.Write("drag over the desktop window");
        }
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            Services.DesktopLog.Write("drop without a file list");
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            Services.DesktopLog.Write("drop with an unreadable file list");
            return;
        }

        await DeliverFileDropAsync(files).ConfigureAwait(true);
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
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        {
            var palette = new CommandPaletteWindow(_viewModel) { Owner = this };
            palette.ShowDialog();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _ = _viewModel.DisposeAsync();
    }
}
