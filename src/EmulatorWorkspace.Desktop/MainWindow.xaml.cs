using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Desktop.Services;
using EmulatorWorkspace.Desktop.ViewModels;

namespace EmulatorWorkspace.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Point? _dragStart;
    private (string? Subscription, int GuestWidth, int GuestHeight, double Width, double Height) _dragViewport;

    // UX-08 dock sizing: 180 DIP by default, dragged between 120 and half the
    // window, remembered for this session only (the field dies with the window).
    private const double LogcatMinHeight = 120;
    private const double LogcatDefaultHeight = 180;
    private double _logcatHeight = LogcatDefaultHeight;
    private bool _logcatDragging;
    private double _logcatDragStartY;
    private double _logcatDragStartHeight;

    // UX-08 tail following: the dock follows the newest line while it is pinned
    // to the bottom; scrolling up unpins it and counts what arrived since.
    private bool _logDockPinned = true;
    private int _logNewSinceScroll;
    private ScrollViewer? _logDockScroll;

    /// <summary>
    /// UX-05: SystemParameters.ClientAreaAnimation, exposed as a dependency
    /// property so the boot stepper's pulse ring can be suppressed declaratively
    /// in XAML when Windows reports reduced motion.
    /// </summary>
    public static readonly DependencyProperty MotionAllowedProperty = DependencyProperty.Register(
        nameof(MotionAllowed), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    public bool MotionAllowed
    {
        get => (bool)GetValue(MotionAllowedProperty);
        set => SetValue(MotionAllowedProperty, value);
    }

    /// <summary>
    /// UX-05 fix pass (Q3 answer): below 1120 DIP the telemetry pill collapses to
    /// a bare "FPS n" instead of stepping aside. The command bar binds this flag,
    /// so the CPU segment and the sparkline hide while the frame rate stays.
    /// </summary>
    public static readonly DependencyProperty CompactTelemetryProperty = DependencyProperty.Register(
        nameof(CompactTelemetry), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool CompactTelemetry
    {
        get => (bool)GetValue(CompactTelemetryProperty);
        set => SetValue(CompactTelemetryProperty, value);
    }

    /// <summary>
    /// UX-06: the same 1120 DIP breakpoint, for the two stage chips that overlap
    /// the device frame once the stage is narrow. Grok Bot's UX-05 review decision
    /// was to hide them rather than leave them on top of the frame.
    /// </summary>
    public static readonly DependencyProperty CompactStageProperty = DependencyProperty.Register(
        nameof(CompactStage), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool CompactStage
    {
        get => (bool)GetValue(CompactStageProperty);
        set => SetValue(CompactStageProperty, value);
    }

    /// <summary>
    /// UX-11: the 1024 DIP layout. Below 1120 DIP the shell collapses its
    /// inspector panel (the viewport is the hero there, as shots 12/13 draw it)
    /// and condenses the command bar to icon-only groups. The flag is bound by
    /// XAML only for presentation; no command, binding or handler moves.
    /// </summary>
    public static readonly DependencyProperty NarrowLayoutProperty = DependencyProperty.Register(
        nameof(NarrowLayout), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool NarrowLayout
    {
        get => (bool)GetValue(NarrowLayoutProperty);
        set => SetValue(NarrowLayoutProperty, value);
    }

    /// <summary>The command bar's breakpoint: below it the bar overflows.</summary>
    private const double NarrowWidthLimit = 1120;

    /// <summary>
    /// The inspector's own breakpoint. igr-feedback-ux11 asks for the 48 DIP
    /// strip below 1180 DIP, which is a wider trigger than the command bar's
    /// 1120, so the panel leaves room for the stage a step earlier than the bar
    /// condenses.
    /// </summary>
    private const double InspectorNarrowLimit = 1180;

    /// <summary>
    /// UX-11 reviewer refinement: below 1180 DIP the collapsed inspector leaves a
    /// 48 DIP icon strip at the window edge that reopens it on the tab the
    /// operator picked. The strip is visible only while the panel is collapsed and
    /// the focus view is off, so it never competes with the panel's own tabs.
    /// </summary>
    public static readonly DependencyProperty ShowInspectorStripProperty = DependencyProperty.Register(
        nameof(ShowInspectorStrip), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool ShowInspectorStrip
    {
        get => (bool)GetValue(ShowInspectorStripProperty);
        set => SetValue(ShowInspectorStripProperty, value);
    }

    /// <summary>
    /// Whether the window is narrow enough for the inspector's strip behaviour
    /// (1180 DIP), independent of the command bar's own 1120 DIP breakpoint.
    /// </summary>
    public static readonly DependencyProperty InspectorNarrowProperty = DependencyProperty.Register(
        nameof(InspectorNarrow), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool InspectorNarrow
    {
        get => (bool)GetValue(InspectorNarrowProperty);
        set => SetValue(InspectorNarrowProperty, value);
    }

    /// <summary>
    /// True while the inspector is open as an overlay: the narrow layout lets the
    /// stage span the panel's columns, so the panel floats over it behind a 30 %
    /// scrim instead of narrowing the viewport. Presentation only - the panel, its
    /// tabs and every value in it are the same objects.
    /// </summary>
    public static readonly DependencyProperty InspectorOverlayProperty = DependencyProperty.Register(
        nameof(InspectorOverlay), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool InspectorOverlay
    {
        get => (bool)GetValue(InspectorOverlayProperty);
        set => SetValue(InspectorOverlayProperty, value);
    }

    /// <summary>
    /// UX-06: whether the settings slide-over is open. The rail item binds it to
    /// mark itself as the active destination; the drawer's own open/close lives
    /// in code-behind so the 220 ms slide and the focus hand-off stay together.
    /// </summary>
    public static readonly DependencyProperty IsSettingsOpenProperty = DependencyProperty.Register(
        nameof(IsSettingsOpen), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool IsSettingsOpen
    {
        get => (bool)GetValue(IsSettingsOpenProperty);
        set => SetValue(IsSettingsOpenProperty, value);
    }

    /// <summary>The view model behind the window, for the automation modes.</summary>
    public MainViewModel ViewModel => _viewModel;

    public MainWindow(bool halfCapture = false)
    {
        InitializeComponent();
        ThemeManager.Initialize();
        Chrome.Initialize();
        Chrome.Apply(this);
        MotionAllowed = SystemParameters.ClientAreaAnimation;
        _viewModel = new MainViewModel(halfCapture);
        DataContext = _viewModel;

        _pillIdle.Tick += (_, _) => CheckPillIdle();
        _pillClipTimer.Tick += (_, _) =>
        {
            _pillClipTimer.Stop();
            ToolbarPill.ClipToBounds = false;
        };

        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            UpdateViewportSize();
            UpdateStageOverlays();
            OnWindowSizeChanged(this, null!);
            _lastStagePointer = DateTimeOffset.UtcNow;
            _pillIdle.Start();
            Services.DesktopLog.Write(
                $"layout window={ActualWidth:N0} body={BodyGrid.ActualWidth:N0} viewport={ViewportScroll.ActualWidth:N0} inspector={InspectorPanel.ActualWidth:N0} dpi={VisualTreeHelper.GetDpi(this).DpiScaleX}");
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.OpenLibraryRequested += OnOpenLibraryRequested;
        _viewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;
        LogDockList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnLogDockScrollChanged));
        LogDockList.Loaded += (_, _) => _logDockScroll = FindDescendant<ScrollViewer>(LogDockList);
    }

    private void OnOpenLibraryRequested() => OpenDeviceLibrary();

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
        else if (args.PropertyName == nameof(MainViewModel.IsLogcatOpen))
        {
            AnimateLogcatDrawer();
            if (!_viewModel.IsLogcatOpen)
            {
                _logDockPinned = true;
                _logNewSinceScroll = 0;
                UpdateJumpPill();
            }
        }
        else if (args.PropertyName == nameof(MainViewModel.DropVisible))
        {
            UpdateDropPulse();
        }
    }

    // ---- UX-05 stage: device frame, pill geometry, auto-hide, telemetry -----

    private readonly DispatcherTimer _pillIdle = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(400),
    };

    /// <summary>
    /// Drops the pill's animation-only clip once the collapse/expand spline has
    /// landed, so the resting pill's shadow keeps its rounded corner
    /// (igr-feedback-ux05 item 3).
    /// </summary>
    private readonly DispatcherTimer _pillClipTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(240),
    };

    private DateTimeOffset _lastStagePointer = DateTimeOffset.MinValue;
    private bool _pillCollapsed;

    /// <summary>
    /// The left margin the pill held while a device was framed. igr-feedback-ux05
    /// item 2: while Starting (no frame) the pill keeps this slot instead of
    /// jumping to the stage edge.
    /// </summary>
    private double? _lastPillFramedLeft;

    /// <summary>
    /// Sizes the decorative device frame around the delivered frame and keeps
    /// the toolbar pill beside the device. Both are overlays: nothing here
    /// touches ViewportScroll, ViewportCanvas, ViewportImage or the input map.
    /// </summary>
    private void UpdateStageOverlays()
    {
        var canvasWidth = ViewportCanvas.Width;
        var canvasHeight = ViewportCanvas.Height;
        if (double.IsNaN(canvasWidth) || canvasWidth <= 0 || double.IsNaN(canvasHeight) || canvasHeight <= 0)
        {
            DeviceFrame.Width = 0;
            DeviceFrame.Height = 0;
        }
        else
        {
            // 10 DIP bezel on each side: the outer 28 DIP radius then contains the
            // frame's square corners, so the bezel reads as a device frame.
            DeviceFrame.Width = canvasWidth + 20;
            DeviceFrame.Height = canvasHeight + 20;
        }

        UpdatePillPosition();
    }

    /// <summary>
    /// Places the pill 16 DIP to the right of the device frame's right edge,
    /// vertically centred by the host grid. It never touches the window edge:
    /// when the frame leaves no room (a landscape guest at 1024, for example)
    /// the pill docks 12 DIP inside the stage instead of overflowing. The
    /// horizontal margin is the pill's left edge - which is also where the
    /// collapsed 3 DIP handle stays.
    ///
    /// igr-feedback-ux05 item 2: while a guest is Starting there is no frame, and
    /// this used to park the pill at the stage's right edge (x ~= 700) and then
    /// snap it back beside the bezel. It now stays in the bezel slot it will
    /// occupy: the slot the last framed guest used, else the slot the reported
    /// guest display would fit into, else a portrait-proportioned slot beside the
    /// centred boot card. Only the floating overlay moves — the viewport, the
    /// canvas, the image and the input map are untouched.
    /// </summary>
    private void UpdatePillPosition()
    {
        var stage = StageRoot.ActualWidth;
        if (stage <= 0)
        {
            return;
        }

        const double pillWidth = 44;   // 36 DIP buttons inside 4 DIP of padding
        const double frameGap = 16;    // fix pass item 10: 16 DIP off the frame
        const double edgeInset = 12;   // the pill never touches the stage edge
        const double bezel = 10;       // DeviceFrame is canvas + 10 DIP each side
        // The canvas keeps its last explicit size after a frame is cleared, so
        // ask the view model whether a device is actually framed instead of
        // trusting the stale width - otherwise the pill floats over the stage
        // card when a guest has no picture.
        var framed = _viewModel.Frame is not null;
        var canvasWidth = framed
            ? !double.IsNaN(ViewportCanvas.Width) && ViewportCanvas.Width > 0
                ? ViewportCanvas.Width
                : ViewportCanvas.ActualWidth
            : 0;
        if (double.IsNaN(canvasWidth) || canvasWidth <= 0)
        {
            canvasWidth = 0;
        }

        var maxLeft = Math.Max(edgeInset, stage - pillWidth - edgeInset);
        double left;
        if (canvasWidth <= 0)
        {
            // No device framed: hold the bezel slot instead of jumping to the
            // stage edge, so the pill does not move when the first frame lands.
            left = Math.Clamp(_lastPillFramedLeft ?? PredictedFramedLeft(stage), edgeInset, maxLeft);
        }
        else
        {
            var frameRight = (stage + canvasWidth) / 2 + bezel;
            left = Math.Clamp(frameRight + frameGap, edgeInset, maxLeft);
            _lastPillFramedLeft = left;
        }

        ToolbarPillHost.Margin = new Thickness(left, 0, 0, 0);
        // Keep the stage cards (boot stepper, first-run setup) out of the pill's
        // lane while the guest has no frame; with a frame the cards are hidden
        // and the lane collapses to 0 so nothing else moves.
        var lane = canvasWidth <= 0 ? Math.Max(0, stage - left + 12) : 0;
        if (StageCardLane is not null && StageCardLane.Margin.Right != lane)
        {
            StageCardLane.Margin = new Thickness(0, 0, lane, 0);
        }
    }

    /// <summary>
    /// Where the pill will sit once pixels arrive, for the Starting case: the
    /// fitted width of the guest display the supervisor reported, or - before the
    /// first frame reports one - the portrait aspect the phase-1 image ships
    /// (1080 x 2400), applied to the height the stage can fit. This positions a
    /// floating overlay only; it never sizes or scales the guest frame.
    /// </summary>
    private double PredictedFramedLeft(double stage)
    {
        const double bezel = 10;
        const double frameGap = 16;
        const double portraitAspect = 1080d / 2400d;
        var availableHeight = Math.Max(80, ViewportScroll.ActualHeight - 32);
        var fittedWidth = _viewModel.GuestWidth > 0 && _viewModel.GuestHeight > 0
            ? availableHeight * (_viewModel.GuestWidth / (double)_viewModel.GuestHeight)
            : availableHeight * portraitAspect;
        return (stage + Math.Max(80, fittedWidth)) / 2 + bezel + frameGap;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewportSize();

    /// <summary>
    /// Responsive chrome: below 1120 DIP the command bar cannot hold the full
    /// telemetry pill next to the identity and every tool group without
    /// squeezing the state pill, so the pill collapses to a bare "FPS n" (no CPU
    /// segment, no sparkline) instead of disappearing (fix pass, Q3).
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TelemetryPill is null)
        {
            return;
        }

        var narrow = ActualWidth < NarrowWidthLimit;
        CompactTelemetry = narrow;
        // UX-06: the two bottom stage chips step aside at the same breakpoint;
        // below it the stage is narrower than the frame plus its chips, and the
        // drop hint sat on the frame's lower-left corner at 1024.
        CompactStage = narrow;
        NarrowLayout = narrow;

        // UX-11: the inspector has its own, wider breakpoint (1180 DIP). Entering
        // it collapses the panel so the stage keeps the hero width both responsive
        // shots draw; leaving it restores the panel only when this pass was the one
        // that hid it, so an operator who closed the panel themselves stays closed
        // and one who opened it at 1024 keeps it. Focus view keeps ownership.
        var inspectorNarrow = ActualWidth < InspectorNarrowLimit;
        InspectorNarrow = inspectorNarrow;
        if (!_focusView && inspectorNarrow != _isInspectorNarrow)
        {
            if (inspectorNarrow && !_detailsCollapsed)
            {
                _detailsAutoCollapsed = true;
                SetDetailsCollapsed(true);
            }
            else if (!inspectorNarrow && _detailsCollapsed && _detailsAutoCollapsed)
            {
                _detailsAutoCollapsed = false;
                SetDetailsCollapsed(false);
            }
        }

        _isInspectorNarrow = inspectorNarrow;
        UpdateInspectorStrip();
        // UX-06: the drawer keeps its 460 DIP width until the window drops below
        // 1024 DIP, where it clamps to 45 % (see UpdateSettingsDrawerWidth).
        if (IsSettingsOpen)
        {
            UpdateSettingsDrawerWidth();
        }

        // UX-08: the dock may never take more than half the window, so a resize
        // clamps the operator's session height down to the new ceiling.
        if (_viewModel.IsLogcatOpen)
        {
            var clamped = ClampLogcatHeight(LogcatHost.Height > 0 ? LogcatHost.Height : _logcatHeight);
            _logcatHeight = clamped;
            if (!_logcatDragging)
            {
                LogcatHost.BeginAnimation(FrameworkElement.HeightProperty, null);
                LogcatHost.Height = clamped;
            }
        }
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        _lastStagePointer = DateTimeOffset.UtcNow;
        if (!_pillCollapsed)
        {
            return;
        }

        try
        {
            var host = ToolbarPillHost.TransformToAncestor(StageRoot)
                .TransformBounds(new Rect(0, 0, ToolbarPillHost.ActualWidth, ToolbarPillHost.ActualHeight));
            var point = e.GetPosition(StageRoot);
            if (point.X >= host.Left - 24 && point.X <= host.Right + 24
                && point.Y >= host.Top - 24 && point.Y <= host.Bottom + 24)
            {
                ExpandPill();
            }
        }
        catch (InvalidOperationException)
        {
            // The pill is mid-template; the next move retries.
        }
    }

    private void OnPillEnter(object sender, MouseEventArgs e)
    {
        _lastStagePointer = DateTimeOffset.UtcNow;
        if (_pillCollapsed)
        {
            ExpandPill();
        }
    }

    private void OnPillLeave(object sender, MouseEventArgs e) => _lastStagePointer = DateTimeOffset.UtcNow;

    private void OnPillZoneEnter(object sender, MouseEventArgs e) => ExpandPill();

    private void OnPillFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_pillCollapsed)
        {
            ExpandPill();
        }
    }

    /// <summary>
    /// Addendum 5: in fullscreen, 2.5 s without pointer movement over the stage
    /// collapses the pill to a 3x48 handle at 35 %. Nothing collapses while the
    /// pointer is on the pill (which is also what keeps a tooltip readable), and
    /// reduced motion collapses instantly.
    /// </summary>
    private void CheckPillIdle()
    {
        if (!_fullscreen)
        {
            if (_pillCollapsed)
            {
                ExpandPill();
            }

            return;
        }

        if (_pillCollapsed || ToolbarPillHost.IsMouseOver)
        {
            return;
        }

        if ((DateTimeOffset.UtcNow - _lastStagePointer).TotalMilliseconds < 2500)
        {
            return;
        }

        CollapsePill();
    }

    private void CollapsePill()
    {
        _pillCollapsed = true;
        PillButtons.IsHitTestVisible = false;
        PillHandle.Visibility = Visibility.Visible;
        PillHoverZone.Visibility = Visibility.Visible;
        // igr-feedback-ux05 item 3: the pill's shadow is not clipped at rest (a
        // ClipToBounds host cut its rounded corners into a square in the light
        // theme), so clipping is switched on only for the 220 ms width animation
        // and switched off again when it lands. The resting pill keeps its
        // rounded shadow; the animating one keeps its content inside.
        ClipPillDuringAnimation();

        if (!MotionAllowed)
        {
            ToolbarPill.BeginAnimation(FrameworkElement.WidthProperty, null);
            ToolbarPill.Width = 3;
            ToolbarPill.Opacity = 0.35;
            PillHandle.Opacity = 0.35;
            PillButtons.Visibility = Visibility.Collapsed;
            ToolbarPill.ClipToBounds = false;
            return;
        }

        var from = ToolbarPill.ActualWidth > 0 ? ToolbarPill.ActualWidth : 44;
        ToolbarPill.Width = from;
        var duration = TimeSpan.FromMilliseconds(220);
        var width = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
        width.KeyFrames.Add(new SplineDoubleKeyFrame(3, KeyTime.FromTimeSpan(duration),
            new KeySpline(0.22, 1, 0.36, 1)));
        ToolbarPill.BeginAnimation(FrameworkElement.WidthProperty, width);

        var fade = new DoubleAnimation(0.35, duration) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };
        ToolbarPill.BeginAnimation(UIElement.OpacityProperty, fade);
        PillHandle.BeginAnimation(UIElement.OpacityProperty, fade);

        // The content is hidden once the surface has finished shrinking, so the
        // buttons never hang outside the 3 DIP handle.
        var hideTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();
            if (_pillCollapsed)
            {
                PillButtons.Visibility = Visibility.Collapsed;
            }
        };
        hideTimer.Start();
    }

    private void ExpandPill()
    {
        if (!_pillCollapsed)
        {
            return;
        }

        _pillCollapsed = false;
        PillButtons.Visibility = Visibility.Visible;
        PillButtons.IsHitTestVisible = true;
        PillHandle.Visibility = Visibility.Collapsed;
        PillHoverZone.Visibility = Visibility.Collapsed;
        _lastStagePointer = DateTimeOffset.UtcNow;
        ClipPillDuringAnimation();

        if (!MotionAllowed)
        {
            ToolbarPill.BeginAnimation(FrameworkElement.WidthProperty, null);
            ToolbarPill.Width = double.NaN;
            ToolbarPill.Opacity = 1;
            PillHandle.Opacity = 0;
            ToolbarPill.ClipToBounds = false;
            return;
        }

        var from = ToolbarPill.ActualWidth > 0 ? ToolbarPill.ActualWidth : 3;
        ToolbarPill.Width = from;
        var duration = TimeSpan.FromMilliseconds(220);
        var width = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
        width.KeyFrames.Add(new SplineDoubleKeyFrame(44, KeyTime.FromTimeSpan(duration),
            new KeySpline(0.22, 1, 0.36, 1)));
        ToolbarPill.BeginAnimation(FrameworkElement.WidthProperty, width);

        var fade = new DoubleAnimation(1, duration) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };
        ToolbarPill.BeginAnimation(UIElement.OpacityProperty, fade);
        PillHandle.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// Clips the pill for the duration of a collapse/expand animation and drops
    /// the clip afterwards. The resting pill must stay unclipped: a ClipToBounds
    /// host cuts the two-layer float shadow's rounded corner square (the light
    /// theme showed it plainly at the pill's bottom-right).
    /// </summary>
    private void ClipPillDuringAnimation()
    {
        ToolbarPill.ClipToBounds = true;
        _pillClipTimer.Stop();
        _pillClipTimer.Start();
    }

    /// <summary>Addendum 7: the pill opens the fuller real metrics in a floating surface.</summary>
    private void OnTelemetryClick(object sender, RoutedEventArgs e)
    {
        TelemetryPopover.IsOpen = !TelemetryPopover.IsOpen;
    }

    /// <summary>
    /// Addendum 9: the dropzone's dashed edge pulses 1.2 s while a drag is over
    /// the stage, and is static under reduced motion. Only the dash opacity is
    /// animated, so the veil itself never flickers.
    /// </summary>
    private void UpdateDropPulse()
    {
        DropDash.BeginAnimation(UIElement.OpacityProperty, null);
        DropDash.Opacity = 1;
        if (!_viewModel.DropVisible || !MotionAllowed)
        {
            return;
        }

        var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        DropDash.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    /// <summary>
    /// UX-04b: the dock expands over 220 ms on cubic-bezier(0.22,1,0.36,1) and the
    /// caret turns to point down. With reduced motion the drawer is simply there;
    /// collapsing is immediate in both cases because the tab bar, not the drawer,
    /// is the persistent surface. UX-08 sizes it to the operator's session height
    /// (180 DIP by default, 120 minimum, half the window at most).
    /// </summary>
    private void AnimateLogcatDrawer()
    {
        var open = _viewModel.IsLogcatOpen;
        var openHeight = ClampLogcatHeight(_logcatHeight);

        LogcatHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        DrawerChevron.Angle = open ? 180 : 0;

        if (!open || !SystemParameters.ClientAreaAnimation)
        {
            LogcatHost.Height = open ? openHeight : 0;
            if (open)
            {
                PinDockToLatest();
            }

            return;
        }

        LogcatHost.Height = 0;
        var duration = TimeSpan.FromMilliseconds(220);
        var timeline = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            openHeight,
            KeyTime.FromTimeSpan(duration),
            new KeySpline(0.22, 1, 0.36, 1)));
        LogcatHost.BeginAnimation(FrameworkElement.HeightProperty, timeline);
        PinDockToLatest();
    }

    /// <summary>
    /// A freshly opened dock starts at the newest line, and the tail follows from
    /// there, so the retained offset from a previous look never reappears.
    /// </summary>
    private void PinDockToLatest()
    {
        _logDockPinned = true;
        _logNewSinceScroll = 0;
        UpdateJumpPill();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            (_logDockScroll ??= FindDescendant<ScrollViewer>(LogDockList))?.ScrollToEnd();
        });
    }

    /// <summary>Half the window, but never below the 120 DIP minimum.</summary>
    private double MaxLogcatHeight => Math.Max(LogcatMinHeight, RootGrid.ActualHeight * 0.5);

    private double ClampLogcatHeight(double height) => Math.Clamp(height, LogcatMinHeight, MaxLogcatHeight);

    private void OnLogcatSplitterDown(object sender, MouseButtonEventArgs e)
    {
        _logcatDragging = true;
        _logcatDragStartY = e.GetPosition(this).Y;
        _logcatDragStartHeight = LogcatHost.ActualHeight > 0 ? LogcatHost.ActualHeight : _logcatHeight;
        LogcatSplitter.CaptureMouse();
        e.Handled = true;
    }

    private void OnLogcatSplitterMove(object sender, MouseEventArgs e)
    {
        if (!_logcatDragging)
        {
            return;
        }

        // Dragging up grows the dock: the splitter sits above it.
        var delta = _logcatDragStartY - e.GetPosition(this).Y;
        var height = ClampLogcatHeight(_logcatDragStartHeight + delta);
        LogcatHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        LogcatHost.Height = height;
        _logcatHeight = height;
        e.Handled = true;
    }

    private void OnLogcatSplitterUp(object sender, MouseButtonEventArgs e)
    {
        if (!_logcatDragging)
        {
            return;
        }

        _logcatDragging = false;
        LogcatSplitter.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnLogcatSplitterLostCapture(object sender, MouseEventArgs e) => _logcatDragging = false;

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_viewModel.IsLogcatOpen)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _logNewSinceScroll = 0;
            UpdateJumpPill();
            return;
        }

        var added = e.NewItems?.Count ?? 0;
        if (_logDockPinned)
        {
            // Follow the tail after the container exists for the new item. ScrollToEnd
            // (not ScrollIntoView) pins the offset to the true bottom, so the last
            // rows fill the viewport instead of sitting at its top.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_logDockPinned)
                {
                    (_logDockScroll ??= FindDescendant<ScrollViewer>(LogDockList))?.ScrollToEnd();
                }
            });
        }
        else if (added > 0)
        {
            _logNewSinceScroll += added;
            UpdateJumpPill();
        }
    }

    private void OnLogDockScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 && e.VerticalChange == 0)
        {
            // Content grew, not the operator: a pinned dock is about to scroll.
            return;
        }

        var scroll = e.OriginalSource as ScrollViewer ?? _logDockScroll;
        if (scroll is null)
        {
            return;
        }

        var atBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 2;
        if (atBottom)
        {
            _logDockPinned = true;
            _logNewSinceScroll = 0;
        }
        else if (e.VerticalChange < 0 || _logDockPinned)
        {
            _logDockPinned = false;
        }

        UpdateJumpPill();
    }

    private void UpdateJumpPill()
    {
        if (LogJumpPill is null)
        {
            return;
        }

        if (_logDockPinned || _logNewSinceScroll <= 0)
        {
            LogJumpPill.Visibility = Visibility.Collapsed;
            LogJumpPill.Content = "Jump to latest";
            return;
        }

        LogJumpPill.Content = "Jump to latest · " + _logNewSinceScroll + " new";
        LogJumpPill.Visibility = Visibility.Visible;
    }

    private void OnJumpToLatest(object sender, RoutedEventArgs e)
    {
        _logDockPinned = true;
        _logNewSinceScroll = 0;
        UpdateJumpPill();
        (_logDockScroll ??= FindDescendant<ScrollViewer>(LogDockList))?.ScrollToEnd();
    }

    /// <summary>
    /// UX-08 fix pass: the dock's search icon expands into the 200 DIP field and
    /// collapses again when the field is empty, so the dock stays a header plus
    /// lines. An active query keeps the field open.
    /// </summary>
    private void OnToggleDockSearch(object sender, RoutedEventArgs e)
    {
        var open = DockSearchHost.Visibility != Visibility.Visible;
        if (!open && !string.IsNullOrWhiteSpace(_viewModel.LogSearch))
        {
            // A click on the icon while a query is active clears it and closes.
            _viewModel.LogSearch = "";
        }

        DockSearchHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        DockSearchButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (open)
        {
            LogSearchField.Focus();
            LogSearchField.SelectAll();
        }
    }

    private void OnDockSearchLostFocus(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.LogSearch) || DockSearchHost.IsKeyboardFocusWithin)
        {
            return;
        }

        DockSearchHost.Visibility = Visibility.Collapsed;
        DockSearchButton.Visibility = Visibility.Visible;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ---- UX-06 settings slide-over -----------------------------------------
    //
    // The drawer is the UX-04 Create/Clone drawer language moved into the Studio
    // window. The reviewer's spec fixes the motion: 460 DIP on the right (45 % of
    // the window below 1024 DIP), 220 ms translateX 24 -> 0 plus opacity on
    // cubic-bezier(0.22,1,0.36,1), 160 ms exit, an instant fade under Windows
    // reduced motion, Esc and a scrim click to close, focus cycling inside and
    // returning to the rail Settings item. It never creates a second OS window,
    // and it reads no state of its own: the tab contents bind to values the app
    // already has.

    private const double SettingsDrawerMaxWidth = 460;
    private const double SettingsDrawerInset = 48;
    private const double SettingsDrawerNarrowLimit = 1024;
    private const double SettingsDrawerNarrowShare = 0.45;
    private const double SettingsDrawerNudge = 24;

    private static readonly TimeSpan SettingsDrawerEnterDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan SettingsDrawerExitDuration = TimeSpan.FromMilliseconds(160);

    private IInputElement? _settingsFocusReturn;
    private int _settingsDrawerEpoch;
    private readonly List<(UIElement Element, bool HitTest, KeyboardNavigationMode Tab)> _inertChrome = new();
    private DispatcherTimer? _themeSavedTimer;
    private bool _suppressThemeSaved;

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnCloseSettings(object sender, RoutedEventArgs e) => CloseSettings();

    /// <summary>A click on the scrim closes the drawer, like Escape.</summary>
    private void OnSettingsScrimClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseSettings();
    }

    private void OpenSettings()
    {
        if (IsSettingsOpen)
        {
            return;
        }

        _settingsFocusReturn = Keyboard.FocusedElement;
        IsSettingsOpen = true;
        // The rail item paints the accent-soft active surface from its Tag, the
        // same flag the selection triggers in the shared rail style use.
        SettingsRailButton.Tag = true;
        SetChromeInert(true);
        // Theme, motion and high-contrast facts are read from Windows and the
        // theme manager, so they are refreshed on open rather than polled.
        _viewModel.RefreshSettingsFacts();
        UpdateSettingsDrawerWidth();
        HideThemeSaved();

        _settingsDrawerEpoch++;
        SettingsDrawerSlide.BeginAnimation(TranslateTransform.XProperty, null);
        SettingsDrawerShell.BeginAnimation(OpacityProperty, null);
        SettingsScrim.BeginAnimation(UIElement.OpacityProperty, null);
        SettingsDrawerHost.Visibility = Visibility.Visible;

        if (SystemParameters.ClientAreaAnimation)
        {
            // Enter from 24 DIP to the right and fade in together; the shell is
            // already inside the window, so the slide never exposes the studio.
            SettingsDrawerSlide.X = SettingsDrawerNudge;
            SettingsDrawerShell.Opacity = 0;
            SettingsScrim.Opacity = 0;
            SettingsDrawerSlide.BeginAnimation(TranslateTransform.XProperty,
                SettingsDrawerTimeline(0, SettingsDrawerEnterDuration));
            SettingsDrawerShell.BeginAnimation(OpacityProperty,
                SettingsDrawerTimeline(1, SettingsDrawerEnterDuration));
            SettingsScrim.BeginAnimation(UIElement.OpacityProperty,
                SettingsDrawerTimeline(1, SettingsDrawerEnterDuration));
        }
        else
        {
            // Reduced motion: an instant fade, no translation at all.
            SettingsDrawerSlide.X = 0;
            SettingsDrawerShell.Opacity = 1;
            SettingsScrim.Opacity = 1;
        }

        FocusSettingsDrawer();

        // The theme radios are checked while the tab content is first built.
        // That is not a user change, so the confirmation tick stays off until
        // the layout pass that follows has finished (Background runs after the
        // render pass that creates and binds the tab).
        _suppressThemeSaved = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _suppressThemeSaved = false));
    }

    private void CloseSettings()
    {
        if (!IsSettingsOpen)
        {
            return;
        }

        IsSettingsOpen = false;
        SettingsRailButton.Tag = false;
        HideThemeSaved();
        var epoch = ++_settingsDrawerEpoch;

        // Read what the open animation is showing, then make it the base value,
        // so clearing the animation does not snap the panel back to its start.
        var fromX = SettingsDrawerSlide.X;
        var fromShellOpacity = SettingsDrawerShell.Opacity;
        var fromScrimOpacity = SettingsScrim.Opacity;
        SettingsDrawerSlide.BeginAnimation(TranslateTransform.XProperty, null);
        SettingsDrawerShell.BeginAnimation(OpacityProperty, null);
        SettingsScrim.BeginAnimation(UIElement.OpacityProperty, null);
        SettingsDrawerSlide.X = fromX;
        SettingsDrawerShell.Opacity = fromShellOpacity;
        SettingsScrim.Opacity = fromScrimOpacity;

        if (!SystemParameters.ClientAreaAnimation)
        {
            Finish();
            return;
        }

        var exit = SettingsDrawerTimeline(SettingsDrawerNudge, SettingsDrawerExitDuration);
        exit.Completed += (_, _) =>
        {
            if (epoch == _settingsDrawerEpoch)
            {
                Finish();
            }
        };
        SettingsDrawerSlide.BeginAnimation(TranslateTransform.XProperty, exit);
        SettingsDrawerShell.BeginAnimation(OpacityProperty,
            SettingsDrawerTimeline(0, SettingsDrawerExitDuration));
        SettingsScrim.BeginAnimation(UIElement.OpacityProperty,
            SettingsDrawerTimeline(0, SettingsDrawerExitDuration));

        void Finish()
        {
            SettingsDrawerSlide.BeginAnimation(TranslateTransform.XProperty, null);
            SettingsDrawerShell.BeginAnimation(OpacityProperty, null);
            SettingsScrim.BeginAnimation(UIElement.OpacityProperty, null);
            SettingsDrawerSlide.X = 0;
            SettingsDrawerShell.Opacity = 1;
            SettingsScrim.Opacity = 1;
            SettingsDrawerHost.Visibility = Visibility.Collapsed;
            SetChromeInert(false);
            RestoreSettingsFocus();
        }
    }

    private static DoubleAnimationUsingKeyFrames SettingsDrawerTimeline(double to, TimeSpan duration)
    {
        var timeline = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(duration),
            new KeySpline(0.22, 1, 0.36, 1)));
        return timeline;
    }

    /// <summary>
    /// 460 DIP, or 45 % of the window below 1024 DIP so the scrim still shows a
    /// usable strip of the studio; never narrower than 320 DIP even at the
    /// window's 980 DIP minimum.
    /// </summary>
    private void UpdateSettingsDrawerWidth()
    {
        var width = ActualWidth < SettingsDrawerNarrowLimit
            ? ActualWidth * SettingsDrawerNarrowShare
            : ActualWidth - SettingsDrawerInset;
        SettingsDrawerShell.Width = Math.Min(SettingsDrawerMaxWidth, Math.Max(320, width));
    }

    /// <summary>
    /// While the drawer is open, every other child of the window grid takes no
    /// pointer input and no tab stop, so nothing behind the scrim can be clicked
    /// or focused. The previous values are restored exactly on close; the drawer
    /// host keeps its own TabNavigation="Cycle" as the second half of the trap.
    /// </summary>
    private void SetChromeInert(bool inert)
    {
        if (!inert)
        {
            foreach (var (element, hitTest, tab) in _inertChrome)
            {
                element.IsHitTestVisible = hitTest;
                KeyboardNavigation.SetTabNavigation(element, tab);
            }

            _inertChrome.Clear();
            return;
        }

        foreach (var child in RootGrid.Children.OfType<UIElement>())
        {
            if (ReferenceEquals(child, SettingsDrawerHost))
            {
                continue;
            }

            _inertChrome.Add((child, child.IsHitTestVisible, KeyboardNavigation.GetTabNavigation(child)));
            child.IsHitTestVisible = false;
            KeyboardNavigation.SetTabNavigation(child, KeyboardNavigationMode.None);
        }
    }

    /// <summary>Puts the caret on the first tab once the drawer is on screen.</summary>
    private void FocusSettingsDrawer() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SettingsTabs.SelectedIndex = 0;
            if (SettingsTabs.SelectedItem is TabItem tab)
            {
                tab.Focus();
            }
            else
            {
                SettingsTabs.Focus();
            }
        }));

    private void RestoreSettingsFocus()
    {
        var target = _settingsFocusReturn;
        _settingsFocusReturn = null;
        if (target is UIElement element && element.IsVisible && element.Focus())
        {
            return;
        }

        if (target is FrameworkContentElement content)
        {
            content.Focus();
            return;
        }

        SettingsRailButton.Focus();
    }

    /// <summary>
    /// The drawer's theme control names a mode instead of flipping it. The
    /// change itself is the radio's two-way binding into the view model (the
    /// same ThemeManager call the rail toggle makes), so a click, the keyboard
    /// and assistive technology all apply it the same way; this handler adds the
    /// quiet ok-soft "Saved" tick on the row for 1.5 s. The tick is suppressed
    /// while the tab is first built, because the initial check is not a change.
    /// </summary>
    private void OnThemeChoice(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode })
        {
            return;
        }

        _viewModel.SetTheme(string.Equals(mode, "Dark", StringComparison.Ordinal));
        if (!_suppressThemeSaved)
        {
            ShowThemeSaved();
        }
    }

    private void ShowThemeSaved()
    {
        ThemeSavedTick.Visibility = Visibility.Visible;
        _themeSavedTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _themeSavedTimer.Stop();
        _themeSavedTimer.Tick -= OnThemeSavedElapsed;
        _themeSavedTimer.Tick += OnThemeSavedElapsed;
        _themeSavedTimer.Start();
    }

    private void OnThemeSavedElapsed(object? sender, EventArgs e) => HideThemeSaved();

    private void HideThemeSaved()
    {
        _themeSavedTimer?.Stop();
        ThemeSavedTick.Visibility = Visibility.Collapsed;
    }

    /// <summary>Copies a path from a path row (the row carries it in Tag).</summary>
    private void OnCopySettingsPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && path.Length > 0)
        {
            _viewModel.CopyPath(path);
        }
    }

    /// <summary>Opens a path row's folder in Explorer.</summary>
    private void OnOpenSettingsPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && path.Length > 0)
        {
            _viewModel.OpenFolder(path);
        }
    }

    /// <summary>
    /// UX-07 fix pass: the doctor card's 28 DIP copy action hands over the raw
    /// report exactly as the rows were built from the supervisor's fields.
    /// </summary>
    private void OnCopyDoctor(object sender, RoutedEventArgs e) => _viewModel.CopyDoctorReport();

    // ---- UX-04 device library ---------------------------------------------

    private DeviceLibraryWindow? _library;

    private void OnDevices(object sender, RoutedEventArgs e) => OpenDeviceLibrary();

    /// <summary>Opens (or re-activates) the device library window.</summary>
    public DeviceLibraryWindow OpenDeviceLibrary()
    {
        if (_library is { IsLoaded: true })
        {
            _library.Activate();
            return _library;
        }

        var library = new DeviceLibraryWindow { Owner = this };
        library.DeviceChosen += name =>
        {
            if (_viewModel.SelectDevice(name))
            {
                Activate();
            }
        };
        library.Closed += (_, _) => _library = null;
        _library = library;
        library.Show();
        return library;
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
            // UX-05: without a frame there is no device to frame, so the
            // decorative bezel collapses instead of framing empty space.
            DeviceFrame.Width = 0;
            DeviceFrame.Height = 0;
            UpdatePillPosition();
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
        UpdateStageOverlays();
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

    /// <summary>Rail: Search opens the same command palette as Ctrl+K.</summary>
    private void OnOpenPalette(object sender, RoutedEventArgs e) => OpenPalette();

    /// <summary>
    /// True while the palette's modal loop is running. WPF disables the owner of
    /// a ShowDialog window, but a second Ctrl+K (or a rail click delivered
    /// through UI Automation) can still arrive before that takes effect, and two
    /// palettes over one studio is never intended. The flag makes the second
    /// request a no-op; the palette itself closes on Esc, Enter or a scrim click.
    /// </summary>
    private bool _paletteOpen;

    private void OpenPalette()
    {
        if (_paletteOpen)
        {
            return;
        }

        _paletteOpen = true;
        try
        {
            // UX-07 fix pass: the palette hands focus back to whatever opened it.
            var invoker = Keyboard.FocusedElement;
            var palette = new CommandPaletteWindow(_viewModel, invoker) { Owner = this };
            palette.ShowDialog();
        }
        finally
        {
            _paletteOpen = false;
        }
    }

    /// <summary>
    /// Rail: Root Lab is the lab section of the inspector's Diagnostics tab, so
    /// the item selects that tab instead of pretending to be a separate view.
    /// </summary>
    private void OnRootLab(object sender, RoutedEventArgs e)
    {
        SetDetailsCollapsed(false);
        var diagnostics = InspectorTabs.Items.OfType<TabItem>().FirstOrDefault(
            tab => string.Equals(tab.Header as string, "Diagnostics", StringComparison.Ordinal));
        if (diagnostics is not null)
        {
            InspectorTabs.SelectedItem = diagnostics;
        }
    }

    /// <summary>
    /// UX-02 shell: the inspector is collapsible. Collapsing zeroes its column
    /// and splitter and drops the column minimum, because the 250 DIP floor
    /// would otherwise keep the panel visible. The width the user last had is
    /// remembered for the next expand. The viewport column and its geometry
    /// are untouched.
    /// </summary>
    private const int DetailsSplitterColumn = 3;
    private const int DetailsColumn = 4;
    private const int SplitterWidth = 6;
    private const double DetailsDefaultWidth = 318;
    private const double DetailsMinWidth = 250;

    private bool _detailsCollapsed;
    private double _detailsWidth = DetailsDefaultWidth;

    // UX-11: whether the inspector breakpoint, not the operator, collapsed the
    // panel.
    private bool _detailsAutoCollapsed;
    private bool _isInspectorNarrow;

    private void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        // An explicit toggle owns the panel from here: a later resize must not
        // undo it.
        _detailsAutoCollapsed = false;
        SetDetailsCollapsed(!_detailsCollapsed);
    }

    private void SetDetailsCollapsed(bool collapsed)
    {
        _detailsCollapsed = collapsed;
        var columns = BodyGrid.ColumnDefinitions;
        if (collapsed)
        {
            var current = Math.Max(0, columns[DetailsColumn].Width.Value);
            if (current > 0)
            {
                _detailsWidth = current;
            }

            columns[DetailsColumn].MinWidth = 0;
            columns[DetailsColumn].Width = new GridLength(0);
            columns[DetailsSplitterColumn].Width = new GridLength(0);
        }
        else
        {
            columns[DetailsColumn].MinWidth = DetailsMinWidth;
            columns[DetailsColumn].Width = new GridLength(
                _detailsWidth >= DetailsMinWidth ? _detailsWidth : DetailsDefaultWidth);
            columns[DetailsSplitterColumn].Width = new GridLength(SplitterWidth);
        }

        UpdateInspectorStrip();
    }

    /// <summary>
    /// The strip and the overlay are narrow-layout affordances only: the strip
    /// appears when the panel is collapsed, the overlay flag when it is open, and
    /// neither exists while the focus view owns the panes.
    /// </summary>
    private void UpdateInspectorStrip()
    {
        var narrow = InspectorNarrow && !_focusView;
        ShowInspectorStrip = narrow && _detailsCollapsed;
        InspectorOverlay = narrow && !_detailsCollapsed;
    }

    /// <summary>
    /// One strip button per inspector tab. The panel opens on the chosen tab; this
    /// only calls the existing collapse helper and selects a tab, so no data,
    /// command or handler is duplicated. Below the inspector breakpoint the stage
    /// spans the panel's column, so it opens as an overlay over the stage.
    /// </summary>
    private void OnInspectorStripOpen(object sender, RoutedEventArgs e)
    {
        var tab = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "Capture" => 1,
            "Diagnostics" => 2,
            _ => 0,
        };

        _detailsAutoCollapsed = false;
        SetDetailsCollapsed(false);
        InspectorTabs.SelectedIndex = tab;
        InspectorTabs.Focus();
    }

    /// <summary>Opens the command bar's narrow-layout overflow menu under its button.</summary>
    private void OnOverflowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// UX-02 shell: the focus view collapses navigation, details and the
    /// Logcat drawer while keeping device identity, the lifecycle action, the
    /// tool rail and an obvious exit (this toggle). Pane sizes and the Logcat
    /// state are restored when it is left.
    /// </summary>
    private bool _focusView;

    private void OnToggleFocusView(object sender, RoutedEventArgs e)
    {
        var columns = BodyGrid.ColumnDefinitions;
        if (!_focusView)
        {
            _focusView = true;
            NavRail.Visibility = Visibility.Collapsed;
            _focusDetailsWidth = _detailsCollapsed
                ? 0
                : Math.Max(columns[DetailsColumn].Width.Value, DetailsMinWidth);
            SetDetailsCollapsed(true);
            _focusLogcatWasOpen = _viewModel.IsLogcatOpen;
            _viewModel.IsLogcatOpen = false;
        }
        else
        {
            _focusView = false;
            NavRail.Visibility = Visibility.Visible;
            if (_focusDetailsWidth >= DetailsMinWidth)
            {
                SetDetailsCollapsed(false);
            }

            _viewModel.IsLogcatOpen = _focusLogcatWasOpen;
        }

        UpdateInspectorStrip();
    }

    private double _focusDetailsWidth;
    private bool _focusLogcatWasOpen;

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
        _viewModel.HideDropTarget();
        _dropTargetActive = false;
        var apk = files.FirstOrDefault(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        if (apk is null)
        {
            _viewModel.StatusMessage = "Only .apk files install from a drop; build an .aab bundle with Build APK set.";
            Services.DesktopLog.Write("drop with no APK: " + string.Join(", ", files));
            return;
        }

        var device = _viewModel.SelectedDevice;
        if (device is null || device.State != "Running")
        {
            _viewModel.StatusMessage = device is null
                ? "Select a device before dropping an APK."
                : $"{device.Name} must be running before an APK can be installed.";
            Services.DesktopLog.Write("drop refused: no running target guest");
            return;
        }

        Services.DesktopLog.Write("drop accepted: " + apk);
        await _viewModel.InstallApkAsync(Path.GetFullPath(apk)).ConfigureAwait(true);
    }

    private bool _dropTargetActive;

    // UX-03: the drag affordance is computed once per drag-entered state and
    // logged once, instead of writing a log line for every DragOver event.
    private void OnDragEnter(object sender, DragEventArgs e) => UpdateDropTarget(e);

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDropTarget(e);

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dropTargetActive = false;
        _viewModel.HideDropTarget();
    }

    private void UpdateDropTarget(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (_dropTargetActive)
            {
                _dropTargetActive = false;
                _viewModel.HideDropTarget();
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        string[]? files = null;
        try
        {
            files = e.Data.GetData(DataFormats.FileDrop) as string[];
        }
        catch (Exception)
        {
            // Shell drags can refuse the data object while the drag is moving;
            // the affordance still names the target guest.
        }

        _viewModel.ShowDropTarget(files);
        e.Effects = _viewModel.DropAccepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (!_dropTargetActive)
        {
            _dropTargetActive = true;
            Services.DesktopLog.Write("drag entered with "
                + (files is null ? "an unreadable file list" : $"{files.Length} file(s)"));
        }
    }

    private void OnToastEnter(object sender, MouseEventArgs e) => _viewModel.ToastHovered = true;

    private void OnToastLeave(object sender, MouseEventArgs e) => _viewModel.ToastHovered = false;

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        _viewModel.HideDropTarget();
        _dropTargetActive = false;
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
        // UX-06: Escape closes the settings drawer first, so the overlay is
        // always the topmost thing Escape can dismiss.
        if (IsSettingsOpen && e.Key == Key.Escape)
        {
            CloseSettings();
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
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
            OpenPalette();
            e.Handled = true;
        }
        // UX-08: the Logcat chords. They are scoped to a Logcat surface being
        // visible (the dock or the full page), and each one runs exactly the
        // command its button runs, so the tooltip's chord and the button can
        // never disagree.
        else if (LogcatVisible && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            var field = _viewModel.IsLogcatPage ? LogPageSearchField : LogSearchField;
            if (!_viewModel.IsLogcatPage)
            {
                // UX-08 fix pass: the dock's search lives behind a 28 DIP icon, so
                // the chord opens the field before focusing it.
                DockSearchHost.Visibility = Visibility.Visible;
                DockSearchButton.Visibility = Visibility.Collapsed;
            }

            field.Focus();
            field.SelectAll();
            e.Handled = true;
        }
        else if (LogcatVisible && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P)
        {
            _viewModel.LogPaused = !_viewModel.LogPaused;
            e.Handled = true;
        }
        else if (LogcatVisible
                 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                 && e.Key == Key.K)
        {
            if (_viewModel.ClearLogCommand.CanExecute(null))
            {
                _viewModel.ClearLogCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (LogcatVisible && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            // The page copies the selected rows; the dock has no selection-driven
            // copy, so it keeps copying what the filter shows.
            if (_viewModel.IsLogcatPage && LogPageList.SelectedItems.Count > 0)
            {
                _viewModel.CopyLogSelection(LogPageList.SelectedItems.Cast<LogEntryViewModel>());
            }
            else if (_viewModel.CopyLogCommand.CanExecute(null))
            {
                _viewModel.CopyLogCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (LogcatVisible
                 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                 && e.Key == Key.C)
        {
            if (_viewModel.CopyLogCommand.CanExecute(null))
            {
                _viewModel.CopyLogCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (LogcatVisible && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
        {
            if (_viewModel.ExportLogCommand.CanExecute(null))
            {
                _viewModel.ExportLogCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    /// <summary>A Logcat surface is on screen: the dock, the page, or both.</summary>
    private bool LogcatVisible => _viewModel.IsLogcatOpen || _viewModel.IsLogcatPage;

    private void OnCopySelectedLog(object sender, RoutedEventArgs e)
    {
        if (LogPageList.SelectedItems.Count == 0)
        {
            _viewModel.CopyLogCommand.Execute(null);
            return;
        }

        _viewModel.CopyLogSelection(LogPageList.SelectedItems.Cast<LogEntryViewModel>());
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _pillIdle.Stop();
        _ = _viewModel.DisposeAsync();
    }
}
