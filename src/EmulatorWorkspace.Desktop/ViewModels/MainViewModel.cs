using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
using EmulatorWorkspace.Desktop.Services;
using Microsoft.Win32;

namespace EmulatorWorkspace.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly WorkspacePaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _logTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly ConcurrentQueue<LogEntryViewModel> _logInbox = new();
    // UX-08: lines that arrive while the stream is paused. They are held here,
    // counted for the page's "Paused · N buffered" chip, and flushed on resume, so
    // a pause never silently discards what the guest wrote.
    private readonly ConcurrentQueue<LogEntryViewModel> _logPausedBuffer = new();
    private int _logBufferedCount;
    private int _logBufferedNotified;
    private readonly List<double> _frameIntervals = new();
    private readonly List<double> _inputLatencies = new();
    // UX-05: a 24-sample ring buffer of the REAL frame rate for the telemetry
    // sparkline. It is fed only when the supervisor delivers a frame, so it can
    // never draw a value the stream did not produce.
    private readonly List<double> _fpsSamples = new();
    private PointCollection _sparkPoints = new();
    private PointCollection _sparkAreaPoints = new();
    private readonly Channel<PendingFrame> _frames = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly SemaphoreSlim _subscribeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private SupervisorConnection? _connection;

    // UX-16 item C: the bounded recovery rule for a supervisor that went away.
    // The view model owns the start/connect/log; the rule owns the budget.
    private readonly SupervisorWatchdog _watchdog = new();
    private bool _watchdogRecovering;
    private int _frameSequence;
    private string? _frameSubscription;
    private bool _frameSubscribePending;
    private string? _logcatSubscription;
    private bool _frameLoopRunning;
    private PendingInput? _pendingInput;
    private ProbeState? _probe;
    private DateTimeOffset _lastFramePresented;
    private DateTimeOffset _lastFrameInfoUpdated;
    private int _droppedFrames;
    private bool _initialized;

    // UX-03 state evidence. Everything below is derived from a supervisor
    // response or frame event; nothing here decides that a guest is ready.
    private const int StaleFrameSeconds = 5;
    private DateTimeOffset? _frameWaitSince;
    private DateTimeOffset? _launchedUtc;
    private bool _captureRestricted;
    private string _captureRestrictedReason = "";
    private bool _labRecoveryRequired;
    private string _labRecoveryDetail = "";
    private bool _labInstallInProgress;
    private bool _statusPollInFlight;
    private bool _frameStale;
    private FrameStreamState _frameState = FrameStreamState.Live;

    /// <summary>
    /// UX-18: whether the supervisor says the capture is running. Null until the
    /// first status read answers, which is not the same as "not running".
    /// </summary>
    private bool? _frameStreamRunning;

    /// <summary>
    /// UX-18: whether the guest answered the last status read (its ADB
    /// transport was usable). A guest that stopped answering is the one case
    /// where an old frame really can be out of date.
    /// </summary>
    private bool _guestResponsive = true;
    private string _frameHold = "";
    private bool _dropVisible;
    private bool _dropAccepted;
    private string _dropTitle = "";
    private string _dropDetail = "";
    private bool _toastVisible;
    private bool _toastHovered;
    private string _toastTitle = "";
    private string _toastText = "";
    private string? _toastPath;
    private bool _hasErrorBar;
    private bool _showErrorRetry;
    private string _errorBarTitle = "";
    private string _errorBarText = "";
    private string _errorBarAction = "";
    private string _errorBarDetails = "";
    private bool _stageVisible;
    private string _stageTone = "info";
    private string _stageTitle = "";
    private string _stageDetail = "";
    private string _stageHint = "";
    private bool _showBootStages;
    // igr-feedback-ux05 item 2: readiness as the supervisor last reported it
    // (readiness.interactiveUtc), kept so the pill's IsEnabled binding has a
    // value to read between status polls.
    private bool _guestInteractive;

    private readonly bool _halfCapture;

    public MainViewModel(bool halfCapture = false)
    {
        _halfCapture = halfCapture;
        _paths = WorkspacePaths.Resolve();
        _dispatcher = Application.Current.Dispatcher;

        // UX-18: every device command is enabled from the one lifecycle, so a
        // control is offered exactly when the supervisor would accept the
        // action. "Start" is refused while the guest runs, "Stop" is offered
        // while it boots, and input needs a guest that is really up.
        PrimaryActionCommand = new AsyncRelayCommand(PrimaryActionAsync, () => !IsBusy && PrimaryActionEnabled);
        CreateDeviceCommand = new AsyncRelayCommand(CreateAndStartAsync, () => !IsBusy && !HasBlockingIssue);
        StartCommand = new AsyncRelayCommand(
            () => StartDeviceAsync(),
            () => SelectedDevice is not null && !IsBusy && SelectedDevice.Lifecycle.CanStart);
        StopCommand = new AsyncRelayCommand(
            () => StopDeviceAsync(),
            () => SelectedDevice?.Lifecycle.CanStop == true && !IsBusy);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => SelectedDevice is not null && !IsBusy);
        ColdBootCommand = new AsyncRelayCommand(ColdBootAsync, () => SelectedDevice is not null && !IsBusy);
        BackCommand = new AsyncRelayCommand(() => KeyAsync("BACK"), () => CanSendInput);
        HomeCommand = new AsyncRelayCommand(() => KeyAsync("HOME"), () => CanSendInput);
        RecentsCommand = new AsyncRelayCommand(() => KeyAsync("RECENTS"), () => CanSendInput);
        ScreenshotCommand = new AsyncRelayCommand(ScreenshotAsync, () => SelectedDevice?.Lifecycle.EngineAlive == true);
        InstallApkCommand = new AsyncRelayCommand(
            InstallApkDialogAsync,
            () => SelectedDevice?.Lifecycle.CanInstall == true && !IsBusy);
        BuildApkSetCommand = new AsyncRelayCommand(BuildApkSetDialogAsync, () => IsConnected && !IsBusy);
        LaunchSampleCommand = new AsyncRelayCommand(
            () => LaunchAsync("dev.emulatorworkspace.sample"),
            () => SelectedDevice?.Lifecycle.CanInstall == true);
        RotateCommand = new AsyncRelayCommand(ToggleRotationAsync, () => CanSendInput);
        RefreshCommand = new AsyncRelayCommand(InitializeAsync);
        ToggleLogcatCommand = new RelayCommand(() => IsLogcatOpen = !IsLogcatOpen);
        ClearLogCommand = new RelayCommand(ClearLog);
        ExportLogCommand = new AsyncRelayCommand(ExportLogAsync);
        // UX-08: the drawer's own actions. Copy writes the lines the filter
        // currently shows; Retry re-attaches only the log subscription (the
        // window-wide Retry re-reads the whole workspace); Clear filters restores
        // the default view; Resume releases the pause.
        CopyLogCommand = new RelayCommand(CopyLog);
        RetryLogcatCommand = new AsyncRelayCommand(RetryLogcatAsync);
        ClearLogFiltersCommand = new RelayCommand(ClearLogFilters);
        ResumeLogCommand = new RelayCommand(ResumeLog);
        OpenLogcatPageCommand = new RelayCommand(OpenLogcatPage);
        CloseLogcatPageCommand = new RelayCommand(CloseLogcatPage);
        ToggleLogPauseCommand = new RelayCommand(() => LogPaused = !LogPaused);
        CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        AdbRootCommand = new AsyncRelayCommand(() => SetAdbRootAsync(true), () => SelectedDevice is not null && !IsBusy);
        AdbUnrootCommand = new AsyncRelayCommand(() => SetAdbRootAsync(false), () => SelectedDevice is not null && !IsBusy);
        RefreshAdbStateCommand = new AsyncRelayCommand(RefreshAdbStateAsync, () => SelectedDevice is not null && !IsBusy);
        MagiskInstallCommand = new AsyncRelayCommand(InstallMagiskAsync, () => SelectedDevice is not null && !IsBusy);
        MagiskRecoverCommand = new AsyncRelayCommand(RecoverMagiskAsync, () => SelectedDevice is not null && !IsBusy);

        // UX-03 presentation state: the error bar, the drag affordance and the
        // capture toast. All three are driven by the outcome of a real
        // supervisor operation, never by a timer that decides success.
        DismissErrorCommand = new RelayCommand(ClearError);
        CopyErrorDetailsCommand = new RelayCommand(CopyErrorDetails);
        DismissToastCommand = new RelayCommand(HideToast);
        CopyToastCommand = new RelayCommand(CopyToastPath);
        OpenToastCommand = new RelayCommand(OpenToastArtifact);
        // UX-04: the palette and the command bar open the device library window.
        OpenLibraryCommand = new RelayCommand(() => OpenLibraryRequested?.Invoke());
        BootStages.Add(new BootStageViewModel("Engine"));
        BootStages.Add(new BootStageViewModel("Android boot"));
        BootStages.Add(new BootStageViewModel("Interactive"));

        LogView = CollectionViewSource.GetDefaultView(LogEntries);
        LogView.Filter = FilterLog;

        // UX-07: the same fifteen commands, now carrying the section header and
        // the line icon the palette prints. The reason delegate is only read when
        // the command's own predicate says it cannot run, so it explains a
        // disabled row in the operator's words instead of dimming it silently.
        Palette.Add(new PaletteEntryViewModel(
            "Create device", "Create a new managed guest", CreateDeviceCommand,
            PaletteGroups.Device, "IconPlus",
            () => HasBlockingIssue ? "Setup needs attention" : "Busy"));
        Palette.Add(new PaletteEntryViewModel(
            "Device library", "Browse, search and manage the device library", OpenLibraryCommand,
            PaletteGroups.Device, "IconDevice"));
        Palette.Add(new PaletteEntryViewModel(
            "Start device", "Start the selected guest", StartCommand,
            PaletteGroups.Device, "IconPlay",
            () => SelectedDevice is null ? "No device selected"
                : IsBusy ? "Busy"
                : "Already running"));
        Palette.Add(new PaletteEntryViewModel(
            "Stop device", "Stop the selected guest", StopCommand,
            PaletteGroups.Device, "IconStop",
            () => SelectedDevice is null ? "No device selected"
                : IsBusy ? "Busy"
                : "Not running"));
        Palette.Add(new PaletteEntryViewModel(
            "Restart device", "Restart the selected guest", RestartCommand,
            PaletteGroups.Device, "IconRestart",
            PaletteDisabledReason));
        Palette.Add(new PaletteEntryViewModel(
            "Cold boot", "Cold boot the selected guest", ColdBootCommand,
            PaletteGroups.Device, "IconBolt",
            PaletteDisabledReason));
        Palette.Add(new PaletteEntryViewModel(
            "Install APK", "Install an APK on the selected guest", InstallApkCommand,
            PaletteGroups.AppsAndCapture, "IconPackage",
            () => SelectedDevice is null ? "No device selected"
                : IsBusy ? "Busy"
                : "Guest not running"));
        Palette.Add(new PaletteEntryViewModel(
            "Build APK set", "Convert an Android App Bundle using test signing", BuildApkSetCommand,
            PaletteGroups.AppsAndCapture, "IconStack",
            () => IsConnected ? "Busy" : "Supervisor not connected"));
        Palette.Add(new PaletteEntryViewModel(
            "Launch sample", "Launch the sample application", LaunchSampleCommand,
            PaletteGroups.AppsAndCapture, "IconLaunch",
            () => SelectedDevice is null ? "No device selected"
                : IsBusy ? "Busy"
                : "Guest not running"));
        Palette.Add(new PaletteEntryViewModel(
            "Screenshot", "Capture a PNG of the selected guest", ScreenshotCommand,
            PaletteGroups.AppsAndCapture, "IconScreenshot",
            () => SelectedDevice is null ? "No device selected" : "Guest not running"));
        Palette.Add(new PaletteEntryViewModel(
            "Toggle Logcat", "Show or hide the Logcat drawer", ToggleLogcatCommand,
            PaletteGroups.Diagnostics, "IconLogs"));
        Palette.Add(new PaletteEntryViewModel(
            "Refresh ADB state", "Re-read the ADB daemon identity", RefreshAdbStateCommand,
            PaletteGroups.Diagnostics, "IconRefresh",
            PaletteDisabledReason));
        Palette.Add(new PaletteEntryViewModel(
            "Export diagnostics", "Write the diagnostic bundle", CopyDiagnosticsCommand,
            PaletteGroups.Diagnostics, "IconDownload"));
        Palette.Add(new PaletteEntryViewModel(
            "ADB root", "Run the guest ADB daemon as root", AdbRootCommand,
            PaletteGroups.Privileged, "IconKey",
            PaletteDisabledReason));
        Palette.Add(new PaletteEntryViewModel(
            "ADB unroot", "Return the guest ADB daemon to the shell user", AdbUnrootCommand,
            PaletteGroups.Privileged, "IconUndo",
            PaletteDisabledReason));

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshStatusAsync(), _dispatcher);
        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => DrainLog(), _dispatcher);
        _logTimer.Start();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) =>
        {
            if (!ToastHovered)
            {
                HideToast();
            }
        }, _dispatcher);

        // UX-03: the elapsed and stale sentences carry a clock. The clock never
        // changes a state word; it only re-renders the age of the evidence that
        // the supervisor already reported.
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickClock(), _dispatcher);
        _clockTimer.Start();
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    public ObservableCollection<string> SetupIssues { get; } = new();

    /// <summary>Commands offered by the Ctrl+K palette.</summary>
    public ObservableCollection<PaletteEntryViewModel> Palette { get; } = new();

    public ICollectionView LogView { get; }

    public AsyncRelayCommand PrimaryActionCommand { get; }

    public AsyncRelayCommand CreateDeviceCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public AsyncRelayCommand ColdBootCommand { get; }

    public AsyncRelayCommand BackCommand { get; }

    public AsyncRelayCommand HomeCommand { get; }

    public AsyncRelayCommand RecentsCommand { get; }

    public AsyncRelayCommand ScreenshotCommand { get; }

    public AsyncRelayCommand InstallApkCommand { get; }

    public AsyncRelayCommand BuildApkSetCommand { get; }

    public AsyncRelayCommand LaunchSampleCommand { get; }

    public AsyncRelayCommand RotateCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ToggleLogcatCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public AsyncRelayCommand ExportLogCommand { get; }

    /// <summary>UX-08: copies the Logcat lines the filter currently shows.</summary>
    public RelayCommand CopyLogCommand { get; }

    /// <summary>UX-08: re-attaches the log subscription after a refusal.</summary>
    public AsyncRelayCommand RetryLogcatCommand { get; }

    /// <summary>UX-08: clears the search, the tag and the five level chips.</summary>
    public RelayCommand ClearLogFiltersCommand { get; }

    /// <summary>UX-08: releases the pause from the drawer's empty state.</summary>
    public RelayCommand ResumeLogCommand { get; }

    /// <summary>UX-08: opens the full Logcat page from the dock's pop-out.</summary>
    public RelayCommand OpenLogcatPageCommand { get; }

    /// <summary>UX-08: leaves the Logcat page and shows the Studio again.</summary>
    public RelayCommand CloseLogcatPageCommand { get; }

    /// <summary>UX-08: the page's Pause/Resume button, the same flag as Ctrl+P.</summary>
    public RelayCommand ToggleLogPauseCommand { get; }

    public AsyncRelayCommand CopyDiagnosticsCommand { get; }

    public RelayCommand ToggleThemeCommand { get; }

    public AsyncRelayCommand AdbRootCommand { get; }

    public AsyncRelayCommand AdbUnrootCommand { get; }

    public AsyncRelayCommand RefreshAdbStateCommand { get; }
    public AsyncRelayCommand MagiskInstallCommand { get; }
    public AsyncRelayCommand MagiskRecoverCommand { get; }

    // ---- UX-03 honest state surfaces -------------------------------------

    /// <summary>Dismisses the persistent error bar without touching the guest.</summary>
    public RelayCommand DismissErrorCommand { get; }

    /// <summary>Copies the typed error (code, operation, device, message) for a report.</summary>
    public RelayCommand CopyErrorDetailsCommand { get; }

    public RelayCommand DismissToastCommand { get; }
    public RelayCommand CopyToastCommand { get; }
    public RelayCommand OpenToastCommand { get; }

    /// <summary>UX-04: opens the device library window; the window itself is created by the shell.</summary>
    public RelayCommand OpenLibraryCommand { get; }

    /// <summary>Raised when the operator asks for the device library.</summary>
    public event Action? OpenLibraryRequested;

    /// <summary>Boot stages in supervisor order, each an evidence-backed word.</summary>
    public ObservableCollection<BootStageViewModel> BootStages { get; } = new();

    public bool HasErrorBar
    {
        get => _hasErrorBar;
        private set
        {
            if (SetProperty(ref _hasErrorBar, value))
            {
                OnPropertyChanged(nameof(ShowStatusError));
            }
        }
    }

    public bool ShowErrorRetry
    {
        get => _showErrorRetry;
        private set => SetProperty(ref _showErrorRetry, value);
    }

    public string ErrorBarTitle
    {
        get => _errorBarTitle;
        private set => SetProperty(ref _errorBarTitle, value);
    }

    public string ErrorBarText
    {
        get => _errorBarText;
        private set => SetProperty(ref _errorBarText, value);
    }

    public string ErrorBarAction
    {
        get => _errorBarAction;
        private set => SetProperty(ref _errorBarAction, value);
    }

    public string ErrorBarDetails
    {
        get => _errorBarDetails;
        private set => SetProperty(ref _errorBarDetails, value);
    }

    /// <summary>True while the viewport shows a stage card instead of a live frame.</summary>
    public bool StageVisible
    {
        get => _stageVisible;
        private set => SetProperty(ref _stageVisible, value);
    }

    /// <summary>"info" | "warning" | "error" | "success" — styling only; the words carry the state.</summary>
    public string StageTone
    {
        get => _stageTone;
        private set => SetProperty(ref _stageTone, value);
    }

    public string StageTitle
    {
        get => _stageTitle;
        private set => SetProperty(ref _stageTitle, value);
    }

    public string StageDetail
    {
        get => _stageDetail;
        private set => SetProperty(ref _stageDetail, value);
    }

    public string StageHint
    {
        get => _stageHint;
        private set => SetProperty(ref _stageHint, value);
    }

    public bool ShowBootStages
    {
        get => _showBootStages;
        private set => SetProperty(ref _showBootStages, value);
    }

    /// <summary>
    /// igr-feedback-ux05 item 2: the nav cluster (back, home, recents) and the
    /// screenshot action stay disabled until the guest is actually interactive.
    /// This is a display-only gate over state the supervisor already reports or
    /// the frame stream already proved: the command bindings are untouched, WPF
    /// simply refuses to invoke a command from a disabled button.
    /// </summary>
    public bool IsGuestInteractive => _guestInteractive || Frame is not null;

    /// <summary>True when the last delivered frame is older than the stale budget.</summary>
    public bool IsFrameStale
    {
        get => _frameStale;
        private set
        {
            if (SetProperty(ref _frameStale, value))
            {
                OnPropertyChanged(nameof(StaleFrameTitle));
                OnPropertyChanged(nameof(StaleFrameDetail));
                OnPropertyChanged(nameof(StaleFrameTone));
            }
        }
    }

    /// <summary>
    /// UX-18: the input waits while the stream is stalled (the picture may be
    /// out of date) and while a supervised operation holds the guest's ADB
    /// channel. An idle guest - a screen that simply did not change - keeps
    /// its input, because its picture is current.
    /// </summary>
    public bool IsInputPaused => FrameStreamHealth.PausesInput(_frameState);

    /// <summary>
    /// "warning" when the stream stalled on its own, "info" when a supervised
    /// operation that owns the guest's ADB channel is running. The tone only
    /// styles the chip; both sentences say what happened and what is blocked.
    /// </summary>
    public string StaleFrameTone => FrameStreamHealth.Tone(_frameState);

    public string StaleFrameTitle
    {
        get
        {
            if (_frameState == FrameStreamState.Live)
            {
                return "";
            }

            var age = _lastFramePresented == default
                ? 0
                : Math.Max(0, (DateTimeOffset.UtcNow - _lastFramePresented).TotalSeconds);
            return FrameStreamHealth.Title(_frameState, _frameHold, age);
        }
    }

    /// <summary>
    /// Kept short and split from the title so the chip wraps instead of clipping
    /// at the narrow layouts (1024 px) the design matrix requires.
    /// </summary>
    public string StaleFrameDetail => FrameStreamHealth.Detail(_frameState);

    /// <summary>
    /// Marks the start of a supervised operation that holds the guest's ADB
    /// channel (install, launch). The stale chip then reads as information, not
    /// as a warning about a broken stream.
    /// </summary>
    public void BeginFrameHold(string label)
    {
        _frameHold = label;
        OnPropertyChanged(nameof(StaleFrameTone));
        OnPropertyChanged(nameof(StaleFrameTitle));
        UpdateFrameHealth();
    }

    public void EndFrameHold()
    {
        _frameHold = "";
        OnPropertyChanged(nameof(StaleFrameTone));
        OnPropertyChanged(nameof(StaleFrameTitle));
        UpdateFrameHealth();
    }

    // ---- UX-05 stage, telemetry and status surfaces ------------------------
    // Everything below is a DERIVED string or collection over state that already
    // exists (the selected device, the delivered frames, the launch timestamp,
    // the supervisor's uid/gpu fields). Nothing here polls, and a value the
    // supervisor does not report prints an em dash instead of a guess.

    /// <summary>The stage's top-left chip: the guest's own display size.</summary>
    public string StageGuestChip => $"Guest display · {GuestDisplayText}";

    public string StageGuestChipTooltip =>
        "The guest display as the frame stream reports it. The window scales the frame; the guest resolution itself is never changed here.";

    /// <summary>
    /// igr-feedback-ux13 item 3: the stall state is a warn suffix on the guest
    /// display chip ("Guest display · 1080 × 2400 · Stalled 11s") instead of a
    /// second chip over the stage. The age is the real age of the last delivered
    /// frame; frames resuming drop the suffix.
    /// </summary>
    public string StageGuestChipLive
    {
        get
        {
            // UX-18: a held guest says "Paused" and a broken stream says
            // "Stalled" with the real age. An idle guest says nothing: its
            // picture is current.
            if (_frameState == FrameStreamState.Held)
            {
                return $"{StageGuestChip} · Paused";
            }

            if (!_frameStale)
            {
                return StageGuestChip;
            }

            var age = _lastFramePresented == default
                ? 0
                : Math.Max(0, (DateTimeOffset.UtcNow - _lastFramePresented).TotalSeconds);
            return $"{StageGuestChip} · Stalled {age:N0}s";
        }
    }

    public string StageGuestChipTooltipLive => _frameState switch
    {
        FrameStreamState.Held =>
            $"Input paused while {_frameHold} runs. The supervisor holds the guest's ADB channel.",
        FrameStreamState.Stalled => "Input paused until frames resume.",
        _ => StageGuestChipTooltip,
    };

    /// <summary>Top-right chip: orientation from the guest display, then the real zoom mode.</summary>
    public string StageOrientationChip
    {
        get
        {
            var orientation = GuestWidth > 0 && GuestHeight > 0
                ? GuestWidth > GuestHeight ? "Landscape" : "Portrait"
                : "Display";
            return $"{orientation} · {ZoomStatus}";
        }
    }

    public string StageOrientationChipTooltip =>
        "Orientation from the guest display, followed by the zoom mode the viewport is currently applying (Fit, 1:1 or a zoom factor).";

    /// <summary>Bottom-left chip: the drop hint names the exact target guest.</summary>
    public string StageDropHint => SelectedDevice is { } device
        ? $"Drop an .apk here to install on {device.Name}"
        : "Drop an .apk here to install on the selected guest";

    /// <summary>Bottom-right chip: the input capability that actually exists.</summary>
    public string InputCapabilityChip => "Single-touch · tap & drag";

    public string InputCapabilityDetail =>
        "Single-touch tap and drag only. Multi-touch is not enabled in Phase 1, and the viewport never overlays the guest frame.";

    /// <summary>
    /// "Elapsed 53s" from the supervisor's own launch timestamp. Empty when the
    /// stepper is not showing or the launch time was never reported.
    /// </summary>
    public string BootElapsedText
    {
        get
        {
            if (!StageVisible || !ShowBootStages || _launchedUtc is not { } launched)
            {
                return "";
            }

            var seconds = Math.Max(0, (DateTimeOffset.UtcNow - launched).TotalSeconds);
            return $"Elapsed {seconds:N0}s";
        }
    }

    /// <summary>Telemetry pill: the live frame rate, or an em dash before the first frame.</summary>
    public string TelemetryFpsValue => Frame is null ? "—" : FramesPerSecond.ToString("N0");

    // igr-feedback-ux05 item 1: there is no CPU segment at any width. The
    // supervisor reports no CPU sample for a running guest, so "FPS 9 · CPU —"
    // was an em dash pretending to be a metric (and, behind a flat underline, it
    // read like an empty text input). Nothing replaced it: the pill shows what
    // the frame stream actually measures.

    public string TelemetryTooltip => Frame is null
        ? "Frame rate: no frame delivered yet. Click for the frame pipeline."
        : $"Frame rate {FramesPerSecond:N1} fps measured from the delivered frames. Click for the frame pipeline.";

    public string TelemetrySamplingNote =>
        "Frame rate, delivered size and dropped frames come from the live frame stream. CPU, memory and disk are not reported for a running guest, so they are not shown.";

    public PointCollection SparkPoints => _sparkPoints;

    /// <summary>
    /// The same series closed to the bottom of the 40 x 14 box, so the sparkline
    /// reads as a chart (a filled area under the stroke) rather than as a lone
    /// flat rule, which was exactly what made the old pill look like an empty
    /// text input. Built from the same real samples, never a synthetic shape.
    /// </summary>
    public PointCollection SparkAreaPoints => _sparkAreaPoints;

    /// <summary>
    /// True only once there are two or more real samples: a one-point polyline
    /// draws nothing, so the pill reserves no sparkline space until the stream
    /// has actually produced a shape.
    /// </summary>
    public bool HasSpark => _sparkPoints.Count > 1;

    public string StatusSerial => SelectedDevice is { Serial.Length: > 0 } device ? device.Serial : "—";

    public string StatusSerialTooltip => SelectedDevice is { Serial.Length: > 0 } device
        ? $"ADB serial {device.Serial}"
        : "No ADB serial: no device is selected, or the supervisor has not reported one.";

    public string StatusUid => AdbUid is "" or "n/a" ? "uid —" : $"uid {AdbUid}";

    /// <summary>
    /// Fix pass item 8: the status bar renders the uid segment only when the
    /// supervisor has already reported a daemon uid. Nothing extra is polled.
    /// </summary>
    public bool HasAdbUid => AdbUid is not ("" or "n/a");

    // ---- UX-05 fix pass: the Logcat dock's count line (item 7) -------------
    // Counts come from the retained lines themselves, never from a running
    // tally that a clear or a filter would leave stale.

    public bool HasLogEntries => LogEntries.Count > 0;

    public int LogWarningCount => LogEntries.Count(entry => entry.Level == "W");

    public int LogErrorCount => LogEntries.Count(entry => entry.Level is "E" or "F" or "A");

    /// <summary>
    /// "1,284 lines · 3 warnings · 1 error": the mono line beside the dock tab.
    /// Warnings and errors appear only when they exist, and the whole string is
    /// empty for an empty drawer, which shows "No lines yet" instead. UX-08 moved
    /// the formatting into <see cref="LogcatPresentation.CountText"/> so the
    /// drawer, the tab bar and the tests read one implementation.
    /// </summary>
    public string LogCountText => LogcatPresentation.CountText(LogEntries.Count, LogWarningCount, LogErrorCount);

    /// <summary>The dock header's three coloured segments; each is hidden at zero.</summary>
    public string LogLineCountText => LogcatPresentation.LineCount(LogEntries.Count);

    public string LogWarnCountText => LogcatPresentation.WarningCount(LogWarningCount);

    public string LogErrorCountText => LogcatPresentation.ErrorCount(LogErrorCount);

    public bool HasLogWarnings => LogWarningCount > 0;

    public bool HasLogErrors => LogErrorCount > 0;

    /// <summary>
    /// The page's "Paused · N buffered" chip. The count is the real number of
    /// lines the stream held back while paused, not a sample.
    /// </summary>
    public string LogPausedText => LogcatPresentation.PausedBuffered(LogBufferedCount);

    public int LogBufferedCount => Volatile.Read(ref _logBufferedCount);

    public bool ShowLogPausedChip => LogPaused;

    private void NotifyLogCounts()
    {
        OnPropertyChanged(nameof(HasLogEntries));
        OnPropertyChanged(nameof(LogWarningCount));
        OnPropertyChanged(nameof(LogErrorCount));
        OnPropertyChanged(nameof(LogCountText));
        OnPropertyChanged(nameof(LogLineCountText));
        OnPropertyChanged(nameof(LogWarnCountText));
        OnPropertyChanged(nameof(LogErrorCountText));
        OnPropertyChanged(nameof(HasLogWarnings));
        OnPropertyChanged(nameof(HasLogErrors));
        // UX-08: the drawer's empty state and its state chip read the same facts.
        NotifyLogFilterState();
    }

    private void NotifyLogBuffered()
    {
        OnPropertyChanged(nameof(LogBufferedCount));
        OnPropertyChanged(nameof(LogPausedText));
        OnPropertyChanged(nameof(ShowLogPausedChip));
    }

    // ---- UX-08 Logcat drawer: derived presentation -------------------------
    //
    // Everything below is derived from state the app already has: the retained
    // lines, the five level chips, and the three stream facts (the guest is
    // running, a subscription exists, the stream is paused). Nothing here polls,
    // calls the supervisor or invents a line.

    private LogEmptyState _logEmpty = LogcatPresentation.Empty(false, 0, false, false, false, false);

    private LogStreamState _logState = LogcatPresentation.Stream(false, false, false, null);

    private LogEmptyState _logPageEmpty = LogcatPresentation.PageEmpty(false, 0, false, false, false, false, null);

    private LogStreamState _logPageState = LogcatPresentation.PageStream(false, false, false, null);

    /// <summary>The drawer's empty state: title, one sentence and at most one action.</summary>
    public LogEmptyState LogEmpty => _logEmpty;

    /// <summary>The "Following / Paused / No guest" chip in the drawer's toolbar.</summary>
    public LogStreamState LogState => _logState;

    /// <summary>The full page's empty state, with the serial it is waiting for.</summary>
    public LogEmptyState LogPageEmpty => _logPageEmpty;

    /// <summary>The full page's "Live / Paused / Not attached / No guest" chip.</summary>
    public LogStreamState LogPageState => _logPageState;

    public bool ShowLogEmpty => LogView.IsEmpty;

    public bool ShowLogPageEmpty => LogView.IsEmpty;

    /// <summary>
    /// The serial the page's subtitle names. It is the supervisor's ADB serial,
    /// not the display name, because that is the value the stream is attached to.
    /// </summary>
    public string LogSerial => SelectedDevice is { } device
        ? string.IsNullOrWhiteSpace(device.Serial) ? device.Name : device.Serial
        : "no guest selected";

    /// <summary>The Logcat page's subtitle: "Live stream from emulator-5554".</summary>
    public string LogPageSubtitle => "Live stream from " + LogSerial;

    /// <summary>A subscription is attached to the selected guest right now.</summary>
    public bool HasLogStream => Volatile.Read(ref _logcatSubscription) is not null;

    /// <summary>
    /// The guest's engine is up, so a log stream can be attached to it. The
    /// stream follows the guest while it boots; it does not wait for Android to
    /// report interactive.
    /// </summary>
    public bool IsGuestUp => SelectedDevice?.Lifecycle.EngineAlive == true;

    /// <summary>
    /// Recomputes the two derived presentations and raises them. Called whenever
    /// a filter, the pause flag, the line set or the subscription changes; the
    /// cost is one pass over the retained lines (bounded at 2500).
    /// </summary>
    private void NotifyLogFilterState()
    {
        var visible = LogView.IsEmpty ? 0 : LogView.Cast<object>().Count();

        _logEmpty = LogcatPresentation.Empty(
            HasLogEntries,
            visible,
            IsGuestUp,
            HasLogStream,
            LogPaused,
            HasLogcatError);

        _logState = LogcatPresentation.Stream(
            IsGuestUp,
            HasLogStream,
            LogPaused,
            SelectedDevice?.Name);

        var logSerial = SelectedDevice?.Serial is { Length: > 0 } serial ? serial : SelectedDevice?.Name;

        _logPageEmpty = LogcatPresentation.PageEmpty(
            HasLogEntries,
            visible,
            IsGuestUp,
            HasLogStream,
            LogPaused,
            HasLogcatError,
            logSerial);

        _logPageState = LogcatPresentation.PageStream(
            IsGuestUp,
            HasLogStream,
            LogPaused,
            logSerial);

        OnPropertyChanged(nameof(LogEmpty));
        OnPropertyChanged(nameof(LogState));
        OnPropertyChanged(nameof(LogPageEmpty));
        OnPropertyChanged(nameof(LogPageState));
        OnPropertyChanged(nameof(ShowLogEmpty));
        OnPropertyChanged(nameof(ShowLogPageEmpty));
        OnPropertyChanged(nameof(HasLogStream));
        OnPropertyChanged(nameof(IsGuestUp));
        OnPropertyChanged(nameof(LogSerial));
        OnPropertyChanged(nameof(LogPageSubtitle));
    }

    private string _logcatError = "";

    /// <summary>
    /// UX-08: a failed logcat subscription reports inside the drawer, next to the
    /// Retry that fixes it, instead of only in the window-wide error bar. The
    /// message is the supervisor's own text.
    /// </summary>
    public string LogcatErrorText => _logcatError;

    public bool HasLogcatError => _logcatError.Length > 0;

    private void SetLogcatError(string message)
    {
        if (string.Equals(_logcatError, message, StringComparison.Ordinal))
        {
            return;
        }

        _logcatError = message;
        OnPropertyChanged(nameof(LogcatErrorText));
        OnPropertyChanged(nameof(HasLogcatError));
        NotifyLogFilterState();
    }

    private void ClearLogFilters()
    {
        LogSearch = "";
        LogTag = "";
        LogShowVerbose = true;
        LogShowDebug = true;
        LogShowInfo = true;
        LogShowWarn = true;
        LogShowError = true;
        LogView.Refresh();
        foreach (var entry in LogEntries)
        {
            entry.ApplyHighlight("");
        }

        NotifyLogFilterState();
    }

    private void CopyLog()
    {
        CopyLogLines(LogView.Cast<LogEntryViewModel>().ToList());
    }

    /// <summary>
    /// UX-08 page: Ctrl+C and the context menu copy the rows the operator selected.
    /// The formatting is the same one the Copy button uses, so a pasted line is
    /// always the guest's raw record when the supervisor supplied one.
    /// </summary>
    public void CopyLogSelection(IEnumerable<LogEntryViewModel> selected)
    {
        var lines = selected.ToList();
        if (lines.Count == 0)
        {
            // No selection: the page's Copy means the same as the dock's Copy.
            CopyLog();
            return;
        }

        CopyLogLines(lines);
    }

    private void CopyLogLines(IReadOnlyCollection<LogEntryViewModel> lines)
    {
        try
        {
            if (lines.Count == 0)
            {
                StatusMessage = "Nothing to copy: the Logcat view has no lines.";
                return;
            }

            var text = string.Join(
                Environment.NewLine,
                lines.Select(entry => entry.Raw.Length > 0
                    ? entry.Raw
                    : $"{entry.Time} {entry.Level} {entry.Tag}: {entry.Message}"));
            Clipboard.SetText(text);
            StatusMessage = "Copied " + lines.Count.ToString(CultureInfo.InvariantCulture)
                + (lines.Count == 1 ? " Logcat line" : " Logcat lines") + " to the clipboard.";
        }
        catch (Exception ex)
        {
            ShowError(ex, "logcat.copy", SelectedDevice?.Name);
        }
    }

    private async Task RetryLogcatAsync()
    {
        SetLogcatError("");
        await EnsureLogcatAsync().ConfigureAwait(true);
        NotifyLogFilterState();
    }

    private void ResumeLog()
    {
        LogPaused = false;
    }

    // ---- UX-08 Logcat page --------------------------------------------------
    //
    // The page is the same retained line set and the same subscription as the
    // dock, shown full-window. Opening it closes the dock without detaching the
    // stream; leaving it detaches only when the dock is closed too. There is no
    // second log source, no sample row and no capability the dock does not have.

    private bool _isLogcatPage;

    public bool IsLogcatPage
    {
        get => _isLogcatPage;
        set
        {
            if (SetProperty(ref _isLogcatPage, value))
            {
                OnPropertyChanged(nameof(IsStudioVisible));
                OnPropertyChanged(nameof(LogcatRailActive));
                UpdateLogcatAttachment();
                NotifyLogFilterState();
            }
        }
    }

    // ---- UX-13 Agents page --------------------------------------------------
    //
    // Agent Sessions is the fifth shell page: the same content the separate
    // window hosted, shown in the body cell under the rail, command bar and
    // status bar. The page and the Logcat page are mutually exclusive, and the
    // Studio is visible only when neither page covers it.

    private bool _isAgentsPage;

    public bool IsAgentsPage
    {
        get => _isAgentsPage;
        set
        {
            if (SetProperty(ref _isAgentsPage, value))
            {
                OnPropertyChanged(nameof(IsStudioVisible));
                OnPropertyChanged(nameof(AgentsRailActive));
            }
        }
    }

    /// <summary>The Studio exercises are shown whenever no full page covers them.</summary>
    public bool IsStudioVisible => !IsLogcatPage && !IsAgentsPage;

    /// <summary>The rail's Logcat destination is active for the dock or the page.</summary>
    public bool LogcatRailActive => IsLogcatOpen || IsLogcatPage;

    /// <summary>The rail's Agents destination is active only while its page is.</summary>
    public bool AgentsRailActive => IsAgentsPage;

    /// <summary>
    /// Rail / Ctrl+4: show the Agents page. Leaving the Logcat page detaches the
    /// stream only when the dock is closed too, exactly like its own close.
    /// </summary>
    public void OpenAgentsPage()
    {
        if (IsLogcatPage)
        {
            IsLogcatPage = false;
        }

        IsAgentsPage = true;
    }

    private void OpenLogcatPage()
    {
        // Page first, then close the dock: the attachment decision below then sees
        // one open log surface and never unsubscribes between the two.
        IsLogcatPage = true;
        if (IsAgentsPage)
        {
            IsAgentsPage = false;
        }

        if (IsLogcatOpen)
        {
            IsLogcatOpen = false;
        }

        _ = EnsureLogcatAsync();
    }

    private void CloseLogcatPage()
    {
        IsLogcatPage = false;
        if (!IsLogcatOpen)
        {
            _ = StopLogcatAsync();
        }
    }

    /// <summary>
    /// Attach while either Logcat surface is visible; detach only when both are
    /// closed. Every other caller keeps the old one-surface behaviour.
    /// </summary>
    private void UpdateLogcatAttachment()
    {
        if (IsLogcatOpen || IsLogcatPage)
        {
            _ = EnsureLogcatAsync();
        }
        else
        {
            _ = StopLogcatAsync();
        }
    }

    public string StatusGpu => string.IsNullOrWhiteSpace(GpuMode) ? "—" : GpuMode;

    public string StatusGpuTooltip => string.IsNullOrWhiteSpace(GpuMode)
        ? "GPU mode is not reported for the selected guest."
        : $"GPU mode {GpuMode}, as reported by the supervisor.";

    // ---- UX-05 Root Lab presentation (Diagnostics tab) ---------------------
    // The consent surfaces stay the OS MessageBox; these strings only present
    // the state the supervisor already reported, with the exact target named.

    /// <summary>"dev · emulator-5554": the exact guest every lab action applies to.</summary>
    public string RootLabTarget => SelectedDevice is { } device
        ? string.IsNullOrWhiteSpace(device.Serial)
            ? device.Name
            : $"{device.Name} · {device.Serial}"
        : "No device selected";

    public string RootLabTargetTooltip => SelectedDevice is { } device
        ? $"ADB root and the Magisk lab apply to this guest only ({device.Name}, transport {device.Serial})."
        : "Select a managed lab guest before any privileged action.";

    /// <summary>The ADB daemon's identity as a word, never colour alone.</summary>
    public string AdbDaemonChip => AdbUid switch
    {
        "0" => "Root",
        "" or "n/a" => "Not reported",
        _ => "Unprivileged",
    };

    public string AdbDaemonChipTone => AdbUid switch
    {
        "0" => "warn",
        "" or "n/a" => "neutral",
        _ => "ok",
    };

    /// <summary>
    /// igr-feedback-ux05 item 6: one text3 line instead of a 24 DIP em-dash
    /// badge — "UID · not reported" until the daemon uid is actually known, then
    /// the real number. The detail sentence stays in the row's tooltip.
    /// </summary>
    public string AdbUidLine => AdbUid is "" or "n/a" ? "UID · not reported" : $"UID {AdbUid}";

    public string AdbDaemonDetail => AdbUid is "" or "n/a"
        ? "The daemon UID was not reported for this guest."
        : string.Join(" · ", new[]
        {
            "shell",
            string.IsNullOrWhiteSpace(AdbBuildType) ? "build not reported" : AdbBuildType,
            string.IsNullOrWhiteSpace(AdbTransportId) ? "transport not reported" : $"transport {AdbTransportId}",
        });

    /// <summary>
    /// Pushes one sample of the real frame rate into the 24-point sparkline and
    /// rebuilds the polyline geometry. Called from the existing 250 ms frame-info
    /// cadence, so it needs no timer of its own.
    /// </summary>
    private void PushTelemetrySample()
    {
        if (Frame is null)
        {
            if (_fpsSamples.Count > 0)
            {
                _fpsSamples.Clear();
                _sparkPoints = new PointCollection();
                _sparkAreaPoints = new PointCollection();
                NotifyLiveSurfaces();
            }

            return;
        }

        _fpsSamples.Add(FramesPerSecond);
        while (_fpsSamples.Count > 24)
        {
            _fpsSamples.RemoveAt(0);
        }

        // Auto-ranged like any sparkline: the tallest of the retained samples sets
        // the top of the box, so a steady rate still fills the 40 x 14 chart
        // (a constant series drawn against a fixed 60 fps ceiling was a flat rule
        // in the middle of the pill, which is what the pill had to stop looking
        // like). The samples themselves are untouched: no value is invented.
        var peak = Math.Max(1.0, _fpsSamples.Max()) * 1.08;
        var points = new PointCollection(_fpsSamples.Count);
        for (var index = 0; index < _fpsSamples.Count; index++)
        {
            // 40 x 14 DIP: igr-feedback-ux05 item 1 asks for a 40 DIP sparkline in
            // the raised pill, so the geometry is built at that width.
            var x = _fpsSamples.Count == 1 ? 0 : index * 40.0 / (_fpsSamples.Count - 1);
            var y = 13 - Math.Clamp(_fpsSamples[index] / peak, 0, 1) * 12;
            points.Add(new Point(x, y));
        }

        _sparkPoints = points;

        // The filled area under the same strokes.
        var area = new PointCollection(points.Count + 2);
        foreach (var point in points)
        {
            area.Add(point);
        }

        area.Add(new Point(points[^1].X, 14));
        area.Add(new Point(points[0].X, 14));
        _sparkAreaPoints = area;

        NotifyLiveSurfaces();
    }

    /// <summary>
    /// Raises every derived UX-05 surface string. It cannot promote a state: it
    /// only re-reads values that other code already decided.
    /// </summary>
    private void NotifyLiveSurfaces()
    {
        OnPropertyChanged(nameof(StageGuestChip));
        OnPropertyChanged(nameof(StageGuestChipLive));
        OnPropertyChanged(nameof(StageGuestChipTooltipLive));
        OnPropertyChanged(nameof(StageOrientationChip));
        OnPropertyChanged(nameof(StageDropHint));
        OnPropertyChanged(nameof(BootElapsedText));
        OnPropertyChanged(nameof(TelemetryFpsValue));
        OnPropertyChanged(nameof(TelemetryTooltip));
        OnPropertyChanged(nameof(SparkPoints));
        OnPropertyChanged(nameof(SparkAreaPoints));
        OnPropertyChanged(nameof(HasSpark));
        OnPropertyChanged(nameof(StatusSerial));
        OnPropertyChanged(nameof(StatusSerialTooltip));
        OnPropertyChanged(nameof(StatusUid));
        OnPropertyChanged(nameof(HasAdbUid));
        OnPropertyChanged(nameof(StatusGpu));
        OnPropertyChanged(nameof(StatusGpuTooltip));
        OnPropertyChanged(nameof(RootLabTarget));
        OnPropertyChanged(nameof(RootLabTargetTooltip));
        OnPropertyChanged(nameof(AdbDaemonChip));
        OnPropertyChanged(nameof(AdbDaemonChipTone));
        OnPropertyChanged(nameof(AdbDaemonDetail));
        OnPropertyChanged(nameof(AdbUidLine));
    }

    /// <summary>The APK drag affordance: visible while a file drag is over the window.</summary>
    public bool DropVisible
    {
        get => _dropVisible;
        private set => SetProperty(ref _dropVisible, value);
    }

    /// <summary>Whether the current drag would be accepted (drives DragDropEffects).</summary>
    public bool DropAccepted
    {
        get => _dropAccepted;
        private set => SetProperty(ref _dropAccepted, value);
    }

    public string DropTitle
    {
        get => _dropTitle;
        private set => SetProperty(ref _dropTitle, value);
    }

    public string DropDetail
    {
        get => _dropDetail;
        private set => SetProperty(ref _dropDetail, value);
    }

    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetProperty(ref _toastVisible, value);
    }

    /// <summary>Set by the toast surface: a hovered toast is not auto-dismissed.</summary>
    public bool ToastHovered
    {
        get => _toastHovered;
        set
        {
            if (SetProperty(ref _toastHovered, value) && value)
            {
                _toastTimer.Stop();
            }
            else if (!value && _toastVisible)
            {
                _toastTimer.Stop();
                _toastTimer.Start();
            }
        }
    }

    public string ToastTitle
    {
        get => _toastTitle;
        private set => SetProperty(ref _toastTitle, value);
    }

    public string ToastText
    {
        get => _toastText;
        private set => SetProperty(ref _toastText, value);
    }

    public bool HasToastPath => _toastPath is { Length: > 0 };
    private string _magiskState = "Magisk lab: not checked";
    public string MagiskState
    {
        get => _magiskState;
        private set
        {
            if (SetProperty(ref _magiskState, value))
            {
                OnPropertyChanged(nameof(LabDotTone));
                OnPropertyChanged(nameof(MagiskLabValue));
                OnPropertyChanged(nameof(MagiskLabTooltip));
            }
        }
    }

    /// <summary>
    /// UX-04b fix pass (B2): the Session row prints the lab state as a short
    /// human value ("Not configured", no repeated label) and keeps the
    /// supervisor's own sentence in the tooltip. Mapping only — every word comes
    /// from <see cref="MagiskState"/>.
    /// </summary>
    public string MagiskLabValue
    {
        get
        {
            var text = (MagiskState ?? "").Trim();
            const string prefix = "Magisk lab:";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
            }

            if (text.Length == 0)
            {
                return "—";
            }

            if (text.Contains("recovery required", StringComparison.OrdinalIgnoreCase))
            {
                return "Stock recovery required";
            }

            if (text.Contains("install in progress", StringComparison.OrdinalIgnoreCase))
            {
                return "Install in progress";
            }

            text = text.Replace('_', ' ');
            return char.ToUpperInvariant(text[0]) + text[1..];
        }
    }

    public string MagiskLabTooltip => MagiskState;

    /// <summary>
    /// UX-04b: the rail's Root Lab dot is painted only from a lab state the
    /// supervisor actually reported — ok when the lab is prepared, warn when
    /// stock recovery is due, nothing while the state is unchecked or absent.
    /// </summary>
    public string LabDotTone
    {
        get
        {
            var text = MagiskState ?? "";
            if (text.Contains("recovery required", StringComparison.OrdinalIgnoreCase)
                || text.Contains("recovery due", StringComparison.OrdinalIgnoreCase))
            {
                return "warn";
            }

            if (text.Contains("install in progress", StringComparison.OrdinalIgnoreCase))
            {
                return "warn";
            }

            if (text.Contains("prepared", StringComparison.OrdinalIgnoreCase)
                || text.Contains("installed", StringComparison.OrdinalIgnoreCase))
            {
                return "ok";
            }

            return "";
        }
    }

    /// <summary>Effective ADB daemon user of the selected guest ("n/a" when unknown).</summary>
    public string AdbUid { get; private set; } = "n/a";

    /// <summary>Whether the image of the selected guest can run adbd as root.</summary>
    public bool AdbRootCapable { get; private set; }

    public string AdbBuildType { get; private set; } = "";

    public string AdbTransportId { get; private set; } = "";

    public string AdbRootState => AdbRootCapable
        ? $"adb daemon uid {AdbUid} (build {AdbBuildType}, transport {AdbTransportId})"
        : $"adb daemon uid {AdbUid} - this image cannot run adbd as root (build {AdbBuildType})";

    /// <summary>
    /// UX-04b fix pass (B2): the Session row prints "uid 2000" or an em dash when
    /// the daemon UID was not reported; the full sentence (build, transport, root
    /// capability) is the tooltip.
    /// </summary>
    public string AdbUidValue => AdbUid is "" or "n/a" ? "-" : $"uid {AdbUid}";

    /// <summary>
    /// igr-feedback-ux12 item 6: the inspector prints "Not checked" for an
    /// unreported daemon UID instead of a dash, in text3.
    /// </summary>
    public string AdbDaemonValue => AdbUid is "" or "n/a" ? "Not checked" : $"uid {AdbUid}";

    public string AdbUidTooltip => AdbUid is "" or "n/a"
        ? "The ADB daemon UID was not reported for the selected guest."
        : AdbRootState;

    private DeviceItemViewModel? _selectedDevice;

    public DeviceItemViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(PrimaryActionText));
                OnPropertyChanged(nameof(CanSendInput));
                OnPropertyChanged(nameof(PortsText));
                OnPropertyChanged(nameof(AbiApiText));
                // UX-05: the stage chips, the status bar metrics and the
                // telemetry pill all name the selected guest.
                NotifyLiveSurfaces();
                // UX-08: the Logcat drawer's stream chip and empty state name the
                // same guest.
                NotifyLogFilterState();
                RaiseCommandStates();
                _ = OnDeviceSelectedAsync(value);
            }
        }
    }

    public bool HasSelection => SelectedDevice is not null;

    /// <summary>
    /// Selects a managed device by name. The device library uses it so "Open in
    /// Studio" lands on exactly the card the operator acted on.
    /// </summary>
    public bool SelectDevice(string name)
    {
        var match = Devices.FirstOrDefault(device => device.Name == name);
        if (match is null)
        {
            return false;
        }

        SelectedDevice = match;
        return true;
    }

    /// <summary>
    /// UX-18: input needs a guest that is really up. A guest that is still
    /// booting or whose engine is gone would only produce a refusal from the
    /// supervisor, so the controls stay disabled until it is.
    /// </summary>
    public bool CanSendInput => SelectedDevice?.Lifecycle.CanSendInput == true;

    private bool _isConnected;

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(PrimaryActionText));
                RaiseCommandStates();
            }
        }
    }

    private string _busyText = "";

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    private string _statusMessage = "Starting desktop...";

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(StatusSentence));
                // UX-08 fix pass: the status bar holds segments only, so a transient
                // sentence (an install result, a copy, a refresh note) appears as one
                // bottom-centre toast for 2.5 s instead of parking in the bar.
                ShowStatusToast(value);
            }
        }
    }

    /// <summary>
    /// UX-05: the status bar carries only segments now, so the housekeeping
    /// sentences the connect path sets are not repeated anywhere and the real
    /// operational ones go to the toast.
    /// </summary>
    public string StatusSentence => "";

    private string _errorMessage = "";

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ShowStatusError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// UX-04: while the typed error bar is on screen it carries the cause, so
    /// the status bar stops repeating the same sentence (which used to collide
    /// with the workspace path at 980 px).
    /// </summary>
    public bool ShowStatusError => HasError && !HasErrorBar;

    private bool _hasBlockingIssue;

    public bool HasBlockingIssue
    {
        get => _hasBlockingIssue;
        private set
        {
            if (SetProperty(ref _hasBlockingIssue, value))
            {
                CreateDeviceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _showSetup = true;

    public bool ShowSetup
    {
        get => _showSetup;
        private set => SetProperty(ref _showSetup, value);
    }

    private string _setupSummary = "";

    public string SetupSummary
    {
        get => _setupSummary;
        private set => SetProperty(ref _setupSummary, value);
    }

    private string _doctorText = "";

    public string DoctorText
    {
        get => _doctorText;
        private set => SetProperty(ref _doctorText, value);
    }

    // UX-07 fix pass: the Settings doctor block reads the report as Inspector
    // label/value rows instead of a wrapped mono paragraph. Each value is a real
    // field of the supervisor's doctor report; the tooltip keeps the raw text.
    private string _doctorSdk = "";

    public string DoctorSdk
    {
        get => _doctorSdk;
        private set => SetProperty(ref _doctorSdk, value);
    }

    private string _doctorEmulator = "";

    public string DoctorEmulator
    {
        get => _doctorEmulator;
        private set => SetProperty(ref _doctorEmulator, value);
    }

    private string _doctorImage = "";

    public string DoctorImage
    {
        get => _doctorImage;
        private set => SetProperty(ref _doctorImage, value);
    }

    private string _doctorImageTooltip = "";

    public string DoctorImageTooltip
    {
        get => _doctorImageTooltip;
        private set => SetProperty(ref _doctorImageTooltip, value);
    }

    private string _doctorAdb = "";

    public string DoctorAdb
    {
        get => _doctorAdb;
        private set => SetProperty(ref _doctorAdb, value);
    }

    private string _doctorAcceleration = "";

    public string DoctorAcceleration
    {
        get => _doctorAcceleration;
        private set => SetProperty(ref _doctorAcceleration, value);
    }

    private string _doctorAccelerationTooltip = "";

    public string DoctorAccelerationTooltip
    {
        get => _doctorAccelerationTooltip;
        private set => SetProperty(ref _doctorAccelerationTooltip, value);
    }

    private bool _doctorAccelerationReady;

    /// <summary>True only when the engine probe proved WHPX is installed and usable.</summary>
    public bool DoctorAccelerationReady
    {
        get => _doctorAccelerationReady;
        private set => SetProperty(ref _doctorAccelerationReady, value);
    }

    private string _doctorGpu = "";

    public string DoctorGpu
    {
        get => _doctorGpu;
        private set => SetProperty(ref _doctorGpu, value);
    }

    private string _doctorGpuTooltip = "";

    public string DoctorGpuTooltip
    {
        get => _doctorGpuTooltip;
        private set => SetProperty(ref _doctorGpuTooltip, value);
    }

    private BitmapSource? _frame;

    public BitmapSource? Frame
    {
        get => _frame;
        private set
        {
            var hadFrame = _frame is not null;
            if (SetProperty(ref _frame, value) && hadFrame != HasFrame)
            {
                OnPropertyChanged(nameof(HasFrame));
                // igr-feedback-ux05 item 2: a delivered or cleared frame changes
                // whether the nav cluster is usable.
                OnPropertyChanged(nameof(IsGuestInteractive));
                if (!HasFrame)
                {
                    OnPropertyChanged(nameof(FrameInfo));
                    // UX-05: a cleared frame also empties the telemetry pill and
                    // the sparkline, so no stale rate survives a device switch.
                    OnPropertyChanged(nameof(FrameRateText));
                    OnPropertyChanged(nameof(DeliveredFrameText));
                    OnPropertyChanged(nameof(DroppedFramesText));
                    PushTelemetrySample();
                }
            }
        }
    }

    public bool HasFrame => Frame is not null;

    public int GuestWidth { get; private set; }

    public int GuestHeight { get; private set; }

    private int _frameWidth;

    public int FrameWidth
    {
        get => _frameWidth;
        private set => SetProperty(ref _frameWidth, value);
    }

    private int _frameHeight;

    public int FrameHeight
    {
        get => _frameHeight;
        private set => SetProperty(ref _frameHeight, value);
    }

    public double FramesPerSecond { get; private set; }

    internal string? ViewportSubscription => _frameSubscription;

    public string FrameInfo => Frame is null
        ? "no frame"
        : $"{FrameWidth}x{FrameHeight}  -  {FramesPerSecond:N1} fps  -  {_droppedFrames} dropped";

    // UX-04b: the inspector's RESOURCES rows need the same live numbers as one
    // value each (tabular mono, fixed width). They are raised on the same 250 ms
    // cadence as FrameInfo, so nothing here runs its own timer and no value is
    // shown before the supervisor has delivered a frame.
    public string GuestDisplayText => GuestWidth > 0 && GuestHeight > 0
        ? $"{GuestWidth} × {GuestHeight}"
        : "not reported";

    public string DeliveredFrameText => Frame is null
        ? "not reported"
        : $"{FrameWidth} × {FrameHeight}";

    public string FrameRateText => Frame is null
        ? "not reported"
        : $"{FramesPerSecond:N1} fps";

    public string DroppedFramesText => Frame is null
        ? "not reported"
        : _droppedFrames.ToString("N0");

    private double _lastInputLatency = double.NaN;

    public string InputLatencyText => double.IsNaN(_lastInputLatency)
        ? "not measured"
        : $"{_lastInputLatency:N0} ms (p95 {Percentile(_inputLatencies, 0.95):N0} ms / {_inputLatencies.Count} taps)";

    public string ReadinessText
    {
        get
        {
            var readiness = SelectedDevice?.Readiness;
            if (readiness is null)
            {
                return "boot not measured this session";
            }

            var boot = readiness["launchedToBootSeconds"];
            var interactive = readiness["launchedToInteractiveSeconds"];
            if (boot is null || interactive is null)
            {
                // UX-03: a half-filled readiness object is "not reported yet",
                // not "boot s - interactive s".
                return "readiness not reported yet";
            }

            return $"boot {boot.GetValue<double>():N1}s  -  interactive {interactive.GetValue<double>():N1}s";
        }
    }

    /// <summary>
    /// UX-04b fix pass (B2): the Boot row is a single tabular number (time to
    /// interactive, the figure the supervisor itself reports), or an em dash when
    /// this session has no readiness measurement. <see cref="ReadinessText"/> is
    /// the explanation behind the tooltip.
    /// </summary>
    public string BootValueText
    {
        get
        {
            var interactive = SelectedDevice?.Readiness?["launchedToInteractiveSeconds"];
            return interactive is null ? "—" : $"{interactive.GetValue<double>():N1} s";
        }
    }

    public string BootTooltip => ReadinessText;

    public string ZoomMode
    {
        get => _zoomMode;
        set
        {
            if (SetProperty(ref _zoomMode, value))
            {
                OnPropertyChanged(nameof(ZoomStatus));
                OnPropertyChanged(nameof(StageOrientationChip));
            }
        }
    }

    private string _zoomMode = "Fit";

    private double _zoomFactor = 1.0;

    public double ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            if (SetProperty(ref _zoomFactor, value))
            {
                OnPropertyChanged(nameof(ZoomStatus));
                OnPropertyChanged(nameof(StageOrientationChip));
            }
        }
    }

    public string PortsText => SelectedDevice is null
        ? ""
        : $"{SelectedDevice.ConsolePort} / {SelectedDevice.GrpcPort}";

    public string AbiApiText => SelectedDevice is null
        ? ""
        : $"{SelectedDevice.Abi} / {SelectedDevice.SdkLevel}";
    public string ZoomStatus => ZoomMode switch
    {
        "Fit" => "Fit",
        "Actual" => "1:1",
        _ => $"{ZoomFactor * 100:N0}%",
    };

    // UX-02: the Logcat drawer starts collapsed (it is resizable once open and
    // the stream starts on open; the command bar's Logcat toggle restores it).
    private bool _isLogcatOpen = false;

    public bool IsLogcatOpen
    {
        get => _isLogcatOpen;
        set
        {
            if (SetProperty(ref _isLogcatOpen, value))
            {
                OnPropertyChanged(nameof(LogcatRailActive));
                UpdateLogcatAttachment();
                NotifyLogFilterState();
            }
        }
    }

    private string _logSearch = "";

    public string LogSearch
    {
        get => _logSearch;
        set
        {
            if (SetProperty(ref _logSearch, value))
            {
                LogView.Refresh();
                foreach (var entry in LogEntries)
                {
                    entry.ApplyHighlight(value);
                }

                NotifyLogFilterState();
            }
        }
    }

    private string _logTag = "";

    public string LogTag
    {
        get => _logTag;
        set
        {
            if (SetProperty(ref _logTag, value))
            {
                LogView.Refresh();
                NotifyLogFilterState();
            }
        }
    }

    // UX-08: the level filter is the five chips the mockup shows - V, D, I, W and
    // E, each an independent toggle, all on by default. That is exactly the set of
    // lines the drawer showed before the chips existed, and it removes the old
    // single "minimum level" combo whose selected chip would have implied that a
    // warning filter also hid errors. Filtering happens in one place,
    // LogcatPresentation.LogLevelFilter.Allows, so a fatal or assert line still
    // reads as an error and an unknown level is never silently hidden.
    private bool _logShowVerbose = true;

    public bool LogShowVerbose
    {
        get => _logShowVerbose;
        set
        {
            if (SetProperty(ref _logShowVerbose, value))
            {
                LogView.Refresh();
                NotifyLogFilterState();
            }
        }
    }

    private bool _logShowDebug = true;

    public bool LogShowDebug
    {
        get => _logShowDebug;
        set
        {
            if (SetProperty(ref _logShowDebug, value))
            {
                LogView.Refresh();
                NotifyLogFilterState();
            }
        }
    }

    private bool _logShowInfo = true;

    public bool LogShowInfo
    {
        get => _logShowInfo;
        set
        {
            if (SetProperty(ref _logShowInfo, value))
            {
                LogView.Refresh();
                NotifyLogFilterState();
            }
        }
    }

    private bool _logShowWarn = true;

    public bool LogShowWarn
    {
        get => _logShowWarn;
        set
        {
            if (SetProperty(ref _logShowWarn, value))
            {
                LogView.Refresh();
                NotifyLogFilterState();
            }
        }
    }

    private bool _logShowError = true;

    public bool LogShowError
    {
        get => _logShowError;
        set
        {
            if (SetProperty(ref _logShowError, value))
            {
                LogView.Refresh();
                NotifyLogFilterState();
            }
        }
    }

    /// <summary>The five chips as one filter; recomputed, never stored twice.</summary>
    public LogLevelFilter LogFilter =>
        LogcatPresentation.Filter(LogShowVerbose, LogShowDebug, LogShowInfo, LogShowWarn, LogShowError);

    private bool _logPaused;

    public bool LogPaused
    {
        get => _logPaused;
        set
        {
            if (SetProperty(ref _logPaused, value))
            {
                if (!value)
                {
                    FlushPausedLog();
                }

                // The state chip and the empty state both name the pause, so they
                // are re-raised with it.
                NotifyLogBuffered();
                NotifyLogFilterState();
            }
        }
    }

    /// <summary>
    /// Releases the lines held during a pause into the normal drain path. They
    /// keep their arrival order and count against the same 2,500-line retention.
    /// </summary>
    private void FlushPausedLog()
    {
        var moved = 0;
        while (_logPausedBuffer.TryDequeue(out var entry))
        {
            _logInbox.Enqueue(entry);
            moved++;
        }

        Volatile.Write(ref _logBufferedCount, 0);
        if (moved > 0)
        {
            StatusMessage = moved.ToString(CultureInfo.InvariantCulture)
                + (moved == 1 ? " Logcat line resumed" : " Logcat lines resumed") + ".";
        }
    }

    private string _screenshotPath = "";

    public string ScreenshotPath
    {
        get => _screenshotPath;
        private set => SetProperty(ref _screenshotPath, value);
    }

    private string _apkPath = "";

    public string ApkPath
    {
        get => _apkPath;
        private set => SetProperty(ref _apkPath, value);
    }

    public string WorkspaceRoot => _paths.Root;

    // ---- UX-06 settings drawer: derived facts only -------------------------
    //
    // Every value below already exists in the app; the drawer displays it. No
    // property decides anything, calls the supervisor or invents a preference.
    // UX-17: the theme choice is the one real preference, and it is written to
    // %LOCALAPPDATA%\EmulatorWorkspace\settings.json by SetTheme, so the drawer
    // can promise that it survives a restart.

    /// <summary>
    /// The two theme modes this build really has. ThemeManager exposes System,
    /// Dark and Light; the system choice resolves to one of the two concrete
    /// dictionaries, and high contrast is a Windows policy rather than a third
    /// selectable mode, so the drawer's segmented control shows these two.
    /// The setters exist so a radio that is selected by assistive technology or
    /// by the keyboard applies the mode exactly like a click does; unchecking
    /// (false) is only ever the other radio in the group and applies nothing.
    /// </summary>
    public bool IsDarkTheme
    {
        get => ThemeManager.IsDark;
        set
        {
            if (value)
            {
                SetTheme(true);
            }
        }
    }

    public bool IsLightTheme
    {
        get => !ThemeManager.IsDark;
        set
        {
            if (value)
            {
                SetTheme(false);
            }
        }
    }

    /// <summary>The name, version and commit the assembly itself carries.</summary>
    public string AppVersion => AppBuild.Version;

    /// <summary>Empty unless the build embedded a commit in its metadata.</summary>
    public string AppCommit => AppBuild.Commit;

    public bool HasAppCommit => AppBuild.Commit.Length > 0;

    // Read once: the assembly cannot change while the process runs. The SDK
    // appends "+&lt;commit&gt;" to InformationalVersion only when a revision id is
    // supplied at build time, so the commit row renders only when that (or an
    // explicit AssemblyMetadata row) is really present - never a guessed hash.
    private static readonly (string Version, string Commit) AppBuild = ReadAppBuild();

    private static (string Version, string Commit) ReadAppBuild()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var commit = "";
        var plus = informational.IndexOf('+');
        if (plus >= 0)
        {
            commit = informational[(plus + 1)..];
            informational = informational[..plus];
        }

        foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (metadata.Key is "Commit" or "SourceRevisionId"
                && !string.IsNullOrWhiteSpace(metadata.Value))
            {
                commit = metadata.Value;
                break;
            }
        }

        if (commit.Length > 12)
        {
            commit = commit[..12];
        }

        return (string.IsNullOrWhiteSpace(informational) ? "unknown" : informational, commit);
    }

    /// <summary>Windows' animation-effects policy, which every animation honours.</summary>
    public string MotionState => SystemParameters.ClientAreaAnimation ? "Full" : "Reduced (Windows)";

    /// <summary>
    /// Real Windows motion preference, so the page's live dot pulses only when
    /// animations are enabled. It is a read, never a setting of our own.
    /// </summary>
    public bool MotionEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Windows' high-contrast policy, which always wins over the theme.</summary>
    public string HighContrastState => SystemParameters.HighContrast ? "On" : "Off";

    /// <summary>Where ScreenshotAsync writes its PNGs (real folder, created on demand).</summary>
    public string CapturesFolder => Path.Combine(_paths.Root, "screenshots");

    /// <summary>Where ExportLogAsync writes its text files (real folder, created on demand).</summary>
    public string LogsFolder => Path.Combine(_paths.Root, "logs");

    /// <summary>
    /// Re-raises the derived settings facts. The drawer calls this when it opens,
    /// because motion and high contrast can change while the window is running.
    /// </summary>
    /// <summary>
    /// The one reason a device command is refused before the guest is even
    /// consulted: nothing selected, or a supervised operation owns the channel.
    /// The palette prints it as the row's reason chip only when the command's own
    /// CanExecute is already false.
    /// </summary>
    private string PaletteDisabledReason() =>
        SelectedDevice is null ? "No device selected" : "Busy";

    public void RefreshSettingsFacts()
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(MotionState));
        OnPropertyChanged(nameof(HighContrastState));
        OnPropertyChanged(nameof(CapturesFolder));
        OnPropertyChanged(nameof(LogsFolder));
    }

    /// <summary>
    /// Applies one of the two real theme modes and saves the choice. This is the
    /// same ThemeManager call the rail's theme toggle makes; the drawer's
    /// segmented control names the mode explicitly instead of flipping it. UX-17:
    /// both entry points persist, so the next launch opens in the same theme. A
    /// failed write is logged by the store and the choice still applies now.
    /// </summary>
    public void SetTheme(bool dark)
    {
        var mode = dark ? Services.ThemeMode.Dark : Services.ThemeMode.Light;
        // UX-17: a Checked that repeats the mode already in force (the drawer's
        // own refresh can raise one) is not a new choice, so it neither repaints
        // nor rewrites the file.
        if (ThemeManager.Mode != mode)
        {
            ThemeManager.Apply(mode);
            Services.ThemeSettingsStore.Save(
                Services.ThemeSettingsStore.DefaultPath, mode, Services.DesktopLog.Write);
        }

        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
    }

    /// <summary>
    /// UX-18: the primary action can do something right now. A guest that is
    /// between two states (booting or stopping) has no honest primary action,
    /// so the button says "Working..." and stays disabled instead of stopping a
    /// boot that the label claims is working.
    /// </summary>
    public bool PrimaryActionEnabled => SelectedDevice is null
        || SelectedDevice.Lifecycle.CanStop
        || SelectedDevice.Lifecycle.CanStart;

    public string PrimaryActionText => SelectedDevice switch
    {
        null => "Create device",
        { } device when device.Lifecycle.CanStop => "Stop",
        { } device when device.Lifecycle.CanStart => "Start",
        { } device when device.Lifecycle.Phase == DevicePhase.Unknown => "Unknown state",
        _ => "Working...",
    };

    public async Task InitializeAsync()
    {
        IsBusy = true;
        BusyText = "Connecting to the local supervisor...";
        ClearError();

        // A deliberate refresh or Retry re-arms the watchdog; its own recovery
        // attempts must not, or the budget would never be spent.
        if (!_watchdogRecovering)
        {
            _watchdog.Reset();
        }

        try
        {
            _connection ??= await SupervisorConnection.ConnectAsync(_paths.Root, TimeSpan.FromSeconds(25))
                .ConfigureAwait(true);
            HookConnection(_connection);

            IsConnected = true;
            DesktopLog.Write("supervisor connected");
            StatusMessage = "Supervisor connected.";

            var doctor = await RequestAsync(OperationNames.Doctor, null, TimeSpan.FromMinutes(2)).ConfigureAwait(true);
            ApplyDoctor(doctor);
            await RefreshDevicesAsync().ConfigureAwait(true);
            DesktopLog.Write($"devices={Devices.Count} blocking={HasBlockingIssue}");

            if (Devices.Count == 0)
            {
                ShowSetup = true;
                SetupSummary = HasBlockingIssue
                    ? "Fix the blocking prerequisite below before creating a device."
                    : "No managed device yet. Create the pinned AOSP API 35 profile to begin.";
            }
            else
            {
                ShowSetup = false;
                SelectedDevice = Devices[0];
            }

            if (_initialized)
            {
                StatusMessage = "Refreshed.";
            }

            _initialized = true;
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            if (IsSupervisorFailure(ex) && _connection is { } dead)
            {
                _connection = null;
                // UX-08: no pipe, no log stream; the drawer's chip must not keep
                // claiming it is following.
                ForgetSubscriptions();

                _ = dead.DisposeAsync();
            }

            ShowError(ex, "desktop.initialize");
            StatusMessage = "Supervisor or SDK unavailable.";
            ShowSetup = true;
            HasBlockingIssue = true;
            SetupIssues.Clear();
            if (IsSupervisorFailure(ex))
            {
                // A broken pipe is not a missing SDK. Keep the two failure
                // classes apart so the setup card does not send the operator to
                // the SDK manager for a supervisor that is simply not running.
                SetupSummary = "The local supervisor did not answer. Start it for this workspace root, then choose Refresh.";
                SetupIssues.Add("Supervisor not reachable: " + ex.Message);
                SetupIssues.Add("The pinned image, emulator and SDK licences are not changed by this failure.");
                SetDoctorUnavailable("No supervisor report available. The pinned image and emulator are never substituted automatically.");
                BeginSupervisorRecovery("initialize: " + ex.Message);
            }
            else
            {
                SetupSummary = "The pinned Android SDK could not be used. Fix the items below, then choose Refresh.";
                SetupIssues.Add("Android SDK incomplete or missing: " + ex.Message);
                SetupIssues.Add("Install cmdline-tools, platform-tools, emulator 37.2.12, and system-images;android-35;default;x86_64 revision 2 from the official SDK Manager.");
                SetupIssues.Add("Accept the SDK licenses, set ANDROID_HOME to that SDK, then restart the workspace supervisor.");
                SetDoctorUnavailable("No SDK report available. The pinned image and emulator are never substituted automatically.");
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            UpdateStage();
        }
    }

    /// <summary>
    /// UX-16 item C: subscribes the one connection to the desktop's handlers.
    /// The pipe-closed callback is the watchdog's early warning; a failed
    /// request is the other, so both end in the same bounded run.
    /// </summary>
    private void HookConnection(SupervisorConnection connection)
    {
        connection.EventReceived -= OnSupervisorEvent;
        connection.EventReceived += OnSupervisorEvent;
        connection.Trace = DesktopLog.Write;
        connection.Closed -= OnSupervisorClosed;
        connection.Closed += OnSupervisorClosed;
    }

    /// <summary>
    /// The pipe is gone. The reader raises this off the UI thread, so the state
    /// change and the recovery run are marshalled first.
    /// </summary>
    private void OnSupervisorClosed()
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            if (_connection is null)
            {
                // A failed request already took the connection down and started
                // the run; the second report would be a duplicate.
                return;
            }

            var dead = _connection;
            _connection = null;
            IsConnected = false;
            ForgetSubscriptions();

            _ = dead.DisposeAsync();
            DesktopLog.Write("supervisor pipe closed");
            BeginSupervisorRecovery("pipe closed");
        }));
    }

    /// <summary>
    /// UX-18: a subscription does not survive the supervisor that owns it.
    ///
    /// The subscription lives in the supervisor's in-memory hub. When the pipe
    /// closes, that supervisor is gone and the id the window holds points at
    /// nothing: the status poll read "already subscribed" and never opened a
    /// new stream, so the viewport stayed dead even after the supervisor came
    /// back and the guest was running again. Both ids are dropped here, so the
    /// next poll re-attaches to the guest that is up.
    /// </summary>
    private void ForgetSubscriptions()
    {
        if (Volatile.Read(ref _frameSubscription) is not null)
        {
            Volatile.Write(ref _frameSubscription, null);
        }

        if (Volatile.Read(ref _logcatSubscription) is not null)
        {
            Volatile.Write(ref _logcatSubscription, null);
            NotifyLogFilterState();
        }
    }

    /// <summary>
    /// UX-16 item C: one bounded run per loss. It reuses the app's own start
    /// path (SupervisorLocator.EnsureRunningAsync attaches to a supervisor that
    /// is already listening and only starts one when nothing answers), waits a
    /// growing delay between attempts, and stops after the budget. The existing
    /// err-soft bar with Retry is what the operator keeps when it stops.
    /// </summary>
    private void BeginSupervisorRecovery(string reason)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (!_watchdog.SupervisorLost())
        {
            DesktopLog.Write($"watchdog: already recovering, ignored ({reason})");
            return;
        }

        DesktopLog.Write($"watchdog: supervisor lost: {reason}; up to {SupervisorWatchdog.MaxAttempts} attempts");
        _ = RecoverSupervisorAsync();
    }

    private async Task RecoverSupervisorAsync()
    {
        while (_watchdog.TryNextDelay(out var delay))
        {
            var attempt = _watchdog.Attempts + 1;
            DesktopLog.Write($"watchdog: attempt {attempt}/{SupervisorWatchdog.MaxAttempts} in {delay.TotalSeconds:0}s");
            try
            {
                await Task.Delay(delay, _lifetime.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _watchdogRecovering = true;
            try
            {
                // The same initialization the app runs on launch and on Retry:
                // locate or start the supervisor, connect, doctor, devices.
                await InitializeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // InitializeAsync reports its own failures; this keeps a
                // cancelled or unexpected throw from escaping the fire-and-forget
                // run as an unobserved task exception.
                DesktopLog.Write("watchdog: attempt threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _watchdogRecovering = false;
            }

            if (IsConnected)
            {
                _watchdog.AttemptSucceeded();
                DesktopLog.Write($"watchdog: supervisor reachable again on attempt {attempt}");
                return;
            }

            _watchdog.AttemptFailed();
        }

        DesktopLog.Write("watchdog: gave up; the error bar keeps Retry");
    }

    private void ApplyDoctor(JsonObject doctor)
    {
        var sdk = doctor["androidSdk"]!.AsObject();
        var gpu = doctor["gpu"]!.AsObject();
        var images = sdk["images"]!.AsArray();
        var pins = doctor["pinned"]!.AsObject();
        var pinned = pins["image"]!.GetValue<string>();
        var requiredImageRevision = pins["requiredImageRevision"]?.GetValue<string>() ?? "2";
        var pinnedImage = images.FirstOrDefault(i => i!["packageId"]!.GetValue<string>() == pinned);

        SetupIssues.Clear();
        var acceleration = doctor["accelerationVerdict"]?.GetValue<string>() ?? "unknown";
        if (doctor["whpxUsable"]?.GetValue<bool>() != true)
        {
            SetupIssues.Add("WHPX is not usable. Enable Windows Hypervisor Platform, then reboot.");
        }

        if (pinnedImage is null)
        {
            SetupIssues.Add($"The pinned image {pinned} is not installed. Install it with sdkmanager.");
        }
        else if (pinnedImage["revision"]?.GetValue<string>() != requiredImageRevision)
        {
            SetupIssues.Add($"Install revision {requiredImageRevision} of the pinned image {pinned}.");
        }

        var emulatorVersion = sdk["emulatorVersion"]?.GetValue<string>();
        var emulatorBuild = sdk["emulatorBuildId"]?.GetValue<string>();
        var requiredVersion = pins["emulator"]?.GetValue<string>();
        var requiredBuild = pins["buildId"]?.GetValue<string>();
        if (emulatorVersion != requiredVersion || emulatorBuild != requiredBuild)
        {
            SetupIssues.Add($"Install Android Emulator {requiredVersion} build {requiredBuild} in this SDK.");
        }
        if (sdk["missingTools"] is JsonArray missingTools)
        {
            foreach (var tool in missingTools)
            {
                SetupIssues.Add($"Install the SDK component that provides {tool?.GetValue<string>()}.");
            }
        }

        var freeGiB = doctor["workspaceDriveFreeGiB"]?.GetValue<double>() ?? 0;
        if (freeGiB < 12)
        {
            SetupIssues.Add($"Only {freeGiB:N1} GiB free on the workspace volume; a guest and one snapshot need about 6 GiB.");
        }

        HasBlockingIssue = SetupIssues.Count > 0;
        var systemImageBytes = pinnedImage?["systemImageBytes"]?.GetValue<long>();
        var systemImageSize = systemImageBytes is long bytes ? $" ({bytes / 1073741824d:N2} GiB system.img)" : "";
        var imageRevision = pinnedImage?["revision"]?.ToString() ?? "missing";
        var adbVersion = sdk["adbVersion"]?.ToString() ?? "";
        var gpuName = gpu["name"]?.ToString() ?? "";
        var gpuDriver = gpu["driverVersion"]?.ToString() ?? "";

        // UX-07 fix pass: the same readings the Inspector prints, mapped from the
        // raw probe and the real package id. The Workspace line is gone: PATHS in
        // this drawer already shows the root, and the doctor block is about the
        // toolchain.
        var accelerationLabel = DoctorSummary.Acceleration(
            acceleration, doctor["whpxUsable"]?.GetValue<bool>() == true);
        DoctorSdk = sdk["root"]?.ToString() ?? "";
        DoctorEmulator = $"{emulatorVersion ?? "not reported"} build {emulatorBuild ?? "unknown"}";
        // The friendly name plus its pinned revision fits the row; the package
        // id and the system.img size stay in the tooltip and the copied report.
        DoctorImage = DoctorSummary.Image(pinned, imageRevision, null);
        DoctorImageTooltip = $"{pinned} rev {imageRevision}{systemImageSize}";
        DoctorAdb = adbVersion;
        DoctorAcceleration = accelerationLabel.Label;
        DoctorAccelerationTooltip = acceleration;
        DoctorAccelerationReady = accelerationLabel.Ready;
        DoctorGpu = DoctorSummary.Gpu(gpuName, gpuDriver);
        DoctorGpuTooltip = $"GPU {gpuName} driver {gpuDriver}".Trim();

        DoctorText = string.Join(Environment.NewLine, new[]
        {
            $"SDK: {DoctorSdk}",
            $"Emulator: {DoctorEmulator}",
            $"Image: {DoctorImage}",
            $"ADB: {DoctorAdb}",
            $"Acceleration: {DoctorAcceleration}",
            $"GPU: {DoctorGpu}",
        });
    }

    /// <summary>
    /// The doctor block can only report what the supervisor returned. When there
    /// is no report every row says so, and the raw text carries the reason the
    /// copy button hands over.
    /// </summary>
    private void SetDoctorUnavailable(string message)
    {
        DoctorSdk = "Not reported";
        DoctorEmulator = "Not reported";
        DoctorImage = "Not reported";
        DoctorImageTooltip = message;
        DoctorAdb = "Not reported";
        DoctorAcceleration = "Not reported";
        DoctorAccelerationTooltip = message;
        DoctorAccelerationReady = false;
        DoctorGpu = "Not reported";
        DoctorGpuTooltip = message;
        DoctorText = message;
    }

    private async Task RefreshDevicesAsync()
    {
        var data = await RequestAsync(OperationNames.InstanceList, null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(true);

        var selectedName = SelectedDevice?.Name;
        var incoming = data["instances"]!.AsArray()
            .Select(node => DeviceItemViewModel.From(node!.AsObject()))
            .ToList();

        Devices.Clear();
        foreach (var device in incoming)
        {
            Devices.Add(device);
        }

        if (selectedName is not null)
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Name == selectedName);
        }
    }

    private async Task OnDeviceSelectedAsync(DeviceItemViewModel? device)
    {
        DesktopLog.Write($"select {device?.Name}");
        var unsubscribe = UnsubscribeFramesAsync();
        Frame = null;
        FrameWidth = 0;
        FrameHeight = 0;
        GuestWidth = 0;
        GuestHeight = 0;
        _frameSequence = 0;
        _droppedFrames = 0;
        _lastFramePresented = default;
        _lastFrameInfoUpdated = default;
        FramesPerSecond = 0;
        _frameIntervals.Clear();
        _pendingInput = null;
        _frameWaitSince = null;
        _launchedUtc = null;
        _captureRestricted = false;
        _captureRestrictedReason = "";
        _labRecoveryRequired = false;
        _labRecoveryDetail = "";
        _labInstallInProgress = false;
        IsFrameStale = false;
        ClearError();
        ResetBootStages();
        UpdateStage();
        await StopLogcatAsync().ConfigureAwait(true);
        await unsubscribe.ConfigureAwait(true);

        if (device is null)
        {
            return;
        }

        try
        {
            var status = await RequestAsync(
                OperationNames.InstanceStatus,
                new JsonObject { ["name"] = device.Name },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            DesktopLog.Write($"selected status {device.State}");
            var before = device.Lifecycle;
            device.ApplyStatus(status);
            DesktopLog.Write($"applied status {device.State}");
            if (SelectedDevice?.Name == device.Name)
            {
                ApplyBootEvidence(status);
                UpdateFrameWait(device);
                UpdateStage();
            }

            OnPropertyChanged(nameof(ReadinessText));
            OnPropertyChanged(nameof(BootValueText));
            OnPropertyChanged(nameof(BootTooltip));
            OnPropertyChanged(nameof(PrimaryActionText));
            OnLifecycleChanged(device, before);

            // UX-18: the frame stream is attached when the guest is really up,
            // not when a stale record word says so.
            if (device.Lifecycle.CanAttachFrames)
            {
                await SubscribeFramesAsync(device).ConfigureAwait(true);
                if (IsLogcatOpen)
                {
                    await EnsureLogcatAsync().ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex)
        {
            ShowError(ex, "instance.status", device.Name);
        }

        // The Magisk lab state lives in the supervisor, not in the window: read
        // it so a guest that needs stock recovery says so on selection instead
        // of waiting for someone to open the Root Lab panel.
        await RefreshMagiskStatusAsync(device).ConfigureAwait(true);
        if (SelectedDevice?.Name == device.Name)
        {
            UpdateStage();
        }
    }

    private async Task RefreshStatusAsync()
    {
        DesktopLog.Write("refresh tick");
        // UX-03: the status poll no longer stops while an operation is busy.
        // It is read-only, it is the only evidence source for the boot stages
        // and it keeps the state current while a long install or boot runs.
        if (SelectedDevice is not { } device || _connection is null || _statusPollInFlight)
        {
            return;
        }

        _statusPollInFlight = true;
        try
        {
            var status = await RequestAsync(
                OperationNames.InstanceStatus,
                new JsonObject { ["name"] = device.Name },
                TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            if (SelectedDevice?.Name != device.Name)
            {
                return;
            }

            var before = device.Lifecycle;
            device.ApplyStatus(status);
            ApplyBootEvidence(status);
            UpdateFrameWait(device);
            UpdateFrameHealth();

            // UX-18: the Magisk lab state follows its transaction. It used to
            // be read once, when the guest was selected, so the window kept
            // saying "Prepared" through the whole install and the stage never
            // said that a lab was running. The read is a journal read (the
            // supervisor answers it in ~0.2 s even while the transaction
            // holds the mutation lease).
            await RefreshMagiskStatusAsync(device).ConfigureAwait(true);

            UpdateStage();
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(ReadinessText));
            OnPropertyChanged(nameof(BootValueText));
            OnPropertyChanged(nameof(BootTooltip));
            OnPropertyChanged(nameof(AbiApiText));
            OnLifecycleChanged(device, before);
            if (device.Lifecycle.CanAttachFrames && _frameSubscription is null)
            {
                await SubscribeFramesAsync(device).ConfigureAwait(true);
            }

            // UX-18: a closed log stream is re-attached the same way the frame
            // stream is, so the drawer comes back by itself after the guest
            // does.
            if (IsLogcatOpen && _logcatSubscription is null)
            {
                await EnsureLogcatAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // UX-18: a status read that failed is the one hard evidence that the
            // guest stopped answering, so the frame stream's state follows it.
            _guestResponsive = false;
            UpdateFrameHealth();
            StatusMessage = "Status refresh failed: " + ex.Message;
        }
        finally
        {
            _statusPollInFlight = false;
        }
    }

    private async Task PrimaryActionAsync()
    {
        if (SelectedDevice is null)
        {
            await CreateAndStartAsync().ConfigureAwait(true);
            return;
        }

        // UX-18: the one lifecycle decides what the primary action means. A
        // booting guest can still be stopped; a stopped or dead one can be
        // started; a guest in a transition has no primary action at all.
        var lifecycle = SelectedDevice.Lifecycle;
        if (lifecycle.CanStop)
        {
            await StopDeviceAsync().ConfigureAwait(true);
            return;
        }

        if (lifecycle.CanStart)
        {
            await StartDeviceAsync().ConfigureAwait(true);
        }
    }

    private async Task CreateAndStartAsync()
    {
        if (HasBlockingIssue)
        {
            return;
        }

        var name = "dev";
        var index = 1;
        while (Devices.Any(d => d.Name == name))
        {
            index++;
            name = "dev" + index;
        }

        await RunBusyAsync("Creating the AVD from the pinned AOSP API 35 image...", async () =>
        {
            await RequestAsync(OperationNames.InstanceCreate, new JsonObject { ["name"] = name }, TimeSpan.FromMinutes(5))
                .ConfigureAwait(true);
            await RefreshDevicesAsync().ConfigureAwait(true);
            SelectedDevice = Devices.FirstOrDefault(d => d.Name == name);
            ShowSetup = false;
        }).ConfigureAwait(true);

        await StartDeviceAsync().ConfigureAwait(true);
    }

    private async Task StartDeviceAsync()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        var name = device.Name;
        await RunBusyAsync("Starting engine...", async () =>
        {
            await RequestAsync(
                OperationNames.InstanceStart,
                new JsonObject { ["name"] = name, ["coldBoot"] = true },
                TimeSpan.FromMinutes(2)).ConfigureAwait(true);
            await RefreshDevicesAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

        await RunBusyAsync("Waiting for Android to reach interactive readiness...", async () =>
        {
            // UX-03: the wait is driven by supervisor evidence. While the
            // readiness call runs, the boot stepper is refreshed from
            // instance.status, so each step is a reported stage and a stage
            // the supervisor never reports stays worded as unreported.
            var waitTask = RequestAsync(
                OperationNames.InstanceWait,
                new JsonObject { ["name"] = name, ["timeoutSeconds"] = 600 },
                TimeSpan.FromMinutes(11));
            while (!waitTask.IsCompleted)
            {
                await Task.Delay(1200).ConfigureAwait(true);
                if (!waitTask.IsCompleted)
                {
                    await PollBootEvidenceAsync(name).ConfigureAwait(true);
                }
            }

            var readiness = await waitTask.ConfigureAwait(true);
            var target = Devices.FirstOrDefault(d => d.Name == name) ?? device;
            target.Readiness = readiness;
            if (SelectedDevice?.Name == target.Name)
            {
                _launchedUtc = TryReadUtc(readiness["launchedUtc"]) ?? _launchedUtc;
                OnPropertyChanged(nameof(ReadinessText));
            OnPropertyChanged(nameof(BootValueText));
            OnPropertyChanged(nameof(BootTooltip));
            }

            var status = await RequestAsync(
                OperationNames.InstanceStatus,
                new JsonObject { ["name"] = name },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            target.ApplyStatus(status);
            if (SelectedDevice?.Name == target.Name)
            {
                ApplyBootEvidence(status);
                UpdateFrameWait(target);
                UpdateStage();
            }

            if (SelectedDevice?.Name == target.Name)
            {
                await SubscribeFramesAsync(target).ConfigureAwait(true);
                if (IsLogcatOpen)
                {
                    await EnsureLogcatAsync().ConfigureAwait(true);
                }
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// UX-03: one status poll while a boot is in progress. It only updates the
    /// stepper of the guest that is booting; a selection change mid-boot leaves
    /// the other guest's presentation alone.
    /// </summary>
    private async Task PollBootEvidenceAsync(string name)
    {
        if (_connection is null || _statusPollInFlight)
        {
            return;
        }

        _statusPollInFlight = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var status = await RequestAsync(
                OperationNames.InstanceStatus,
                new JsonObject { ["name"] = name },
                TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            var device = Devices.FirstOrDefault(d => d.Name == name);
            if (device is null)
            {
                return;
            }

            device.ApplyStatus(status);
            DesktopLog.Write($"boot poll {name} state={device.State} adb={status["adbState"]} "
                + $"boot={status["bootCompleted"]} in {stopwatch.ElapsedMilliseconds} ms");
            if (SelectedDevice?.Name == name)
            {
                ApplyBootEvidence(status);
                UpdateFrameWait(device);
                UpdateStage();
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
        catch (Exception ex)
        {
            // The wait itself reports a failure with a typed code; a poll that
            // races the engine start must not raise a second error surface.
            DesktopLog.Write($"boot poll {name} failed after {stopwatch.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _statusPollInFlight = false;
        }
    }

    private async Task StopDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        await RunBusyAsync("Stopping guest...", async () =>
        {
            await RequestAsync(
                OperationNames.InstanceStop,
                new JsonObject { ["name"] = SelectedDevice.Name },
                TimeSpan.FromMinutes(2)).ConfigureAwait(true);
            await UnsubscribeFramesAsync().ConfigureAwait(true);
            await StopLogcatAsync().ConfigureAwait(true);
            await RefreshDevicesAsync().ConfigureAwait(true);
            Frame = null;
            _frameWaitSince = null;
            IsFrameStale = false;
            ResetBootStages();
        }).ConfigureAwait(true);
        UpdateStage();
    }

    private async Task RestartAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        await RunBusyAsync("Restarting guest...", async () =>
        {
            await RequestAsync(
                OperationNames.InstanceRestart,
                new JsonObject { ["name"] = SelectedDevice.Name },
                TimeSpan.FromMinutes(3)).ConfigureAwait(true);
            await RefreshDevicesAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
        await StartDeviceAsync().ConfigureAwait(true);
    }

    private async Task ColdBootAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        await RunBusyAsync("Cold booting guest...", async () =>
        {
            await RequestAsync(
                OperationNames.InstanceStop,
                new JsonObject { ["name"] = SelectedDevice.Name },
                TimeSpan.FromMinutes(2)).ConfigureAwait(true);
            await RefreshDevicesAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
        await StartDeviceAsync().ConfigureAwait(true);
    }

    private async Task KeyAsync(string key)
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        try
        {
            await RequestAsync(
                OperationNames.InputKey,
                new JsonObject { ["name"] = device.Name, ["key"] = key },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError(ex, "input.key", device.Name);
        }
    }

    /// <summary>
    /// One latency trial: dispatch a tap, wait for a presented frame whose sample
    /// marker colour changed, then timestamp the next WPF composition pass.
    /// Returns null on timeout.
    /// </summary>
    public async Task<LatencyProbeSample?> ProbeTapAsync(int timeoutMs = 2500)
    {
        if (SelectedDevice is not { IsRunning: true } device || Frame is not { } frame
            || GuestWidth <= 0 || GuestHeight <= 0 || FindProbeMarker(frame) is not { } marker)
        {
            return null;
        }

        var x = (int)((frame.PixelWidth / 2 + 0.5) * GuestWidth / frame.PixelWidth);
        var y = (int)((marker.Row + 0.5) * GuestHeight / frame.PixelHeight);
        var expected = ProbePalette[(marker.PaletteIndex + 1) % ProbePalette.Length];
        var probe = new ProbeState(device.Name, marker.Row, frame.PixelWidth, frame.PixelHeight, expected);
        _probe = probe;
        void OnRendering(object? sender, EventArgs args)
        {
            if (probe.FrameAvailableMs is double frameMs && SelectedDevice?.Name == probe.Device)
            {
                probe.Completion.TrySetResult(new LatencyProbeSample(frameMs, probe.Clock.Elapsed.TotalMilliseconds)
                {
                    SupervisorFrameWriteMs = probe.SupervisorFrameWriteMs,
                    ChangedFrameReceivedMs = probe.ChangedFrameReceivedMs,
                    FrameDecodeAndDispatchMs = probe.FrameDecodeAndDispatchMs,
                });
            }
        }

        CompositionTarget.Rendering += OnRendering;
        try
        {
            probe.DispatchUtc = DateTimeOffset.UtcNow;
            probe.Clock.Restart();
            var response = await RequestAsync(
                OperationNames.InputTap,
                new JsonObject { ["name"] = device.Name, ["x"] = x, ["y"] = y },
                TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(true);
            var acknowledgedMs = probe.Clock.Elapsed.TotalMilliseconds;
            var remaining = Math.Max(0, timeoutMs - (int)probe.Clock.ElapsedMilliseconds);
            var sample = await probe.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(remaining)).ConfigureAwait(true);
            return sample with
            {
                TapAcknowledgedMs = acknowledgedMs,
                ControllerSetupMs = response["controllerSetupMs"]?.GetValue<double>(),
                ControllerTapMs = response["controllerTapMs"]?.GetValue<double>(),
            };
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SupervisorRequestException)
        {
            return null;
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            _probe = null;
        }
    }

    private static readonly int[] ProbePalette =
        [0xE53935, 0x43A047, 0x1E88E5, 0xFDD835, 0x8E24AA, 0x00ACC1];

    private static (int Row, int PaletteIndex)? FindProbeMarker(BitmapSource frame)
    {
        var height = frame.PixelHeight;
        var pixels = ReadProbeColumn(frame, 0, height);
        var bestLength = Math.Max(4, frame.PixelWidth / 14) - 1;
        (int Row, int PaletteIndex)? marker = null;
        for (var row = 0; row < height;)
        {
            var color = ProbePixel(pixels, row);
            var end = row + 1;
            while (end < height && ProbePixel(pixels, end) == color)
            {
                end++;
            }

            var index = Array.IndexOf(ProbePalette, color);
            if (index >= 0 && end - row > bestLength)
            {
                bestLength = end - row;
                marker = ((row + end) / 2, index);
            }
            row = end;
        }

        return marker;
    }

    private static byte[] ReadProbeColumn(BitmapSource frame, int row, int height)
    {
        var source = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[height * 4];
        source.CopyPixels(new Int32Rect(frame.PixelWidth / 2, row, 1, height), pixels, 4, 0);
        return pixels;
    }

    private static int ProbePixel(byte[] pixels, int row)
    {
        var offset = row * 4;
        return (pixels[offset + 2] << 16) | (pixels[offset + 1] << 8) | pixels[offset];
    }

    public async Task<JsonObject?> GetFrameTraceAsync()
    {
        if (SelectedDevice is null)
        {
            return null;
        }

        try
        {
            return await RequestAsync(
                OperationNames.FrameTrace,
                new JsonObject { ["name"] = SelectedDevice.Name },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string GpuMode => SelectedDevice?.Gpu ?? "";

    public int DeliveredFrameWidth => FrameWidth;

    public int DeliveredFrameHeight => FrameHeight;

    public double DeliveredFps => FramesPerSecond;

    public int DroppedFrames => _droppedFrames;

    public async Task SendTapAsync(int x, int y)
    {
        if (SelectedDevice is not { } device || !device.Lifecycle.CanSendInput)
        {
            return;
        }

        if (IsInputPaused)
        {
            // UX-03: a stalled frame must not accept coordinates, because the
            // picture may be out of date. UX-18: an idle guest is not stalled -
            // its picture is current, so the tap goes through. A supervised
            // operation that holds the guest's ADB channel still pauses the
            // input.
            return;
        }

        var baseline = SampleMarkerColor();
        _pendingInput = new PendingInput(DateTimeOffset.UtcNow, baseline);
        try
        {
            await RequestAsync(
                OperationNames.InputTap,
                new JsonObject { ["name"] = device.Name, ["x"] = x, ["y"] = y },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _pendingInput = null;
            ShowError(ex, "input.tap", device.Name);
        }
    }

    public async Task SendSwipeAsync(int x1, int y1, int x2, int y2)
    {
        if (SelectedDevice is not { } device || !device.Lifecycle.CanSendInput)
        {
            return;
        }

        if (IsInputPaused)
        {
            // UX-18: as with a tap, only a stalled stream or a held channel
            // pauses the input; an idle guest still swipes.
            return;
        }

        try
        {
            await RequestAsync(
                OperationNames.InputSwipe,
                new JsonObject
                {
                    ["name"] = device.Name,
                    ["x1"] = x1,
                    ["y1"] = y1,
                    ["x2"] = x2,
                    ["y2"] = y2,
                    ["durationMs"] = 280,
                },
                TimeSpan.FromSeconds(40)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError(ex, "input.swipe", device.Name);
        }
    }

    private async Task ToggleRotationAsync()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        var next = device.Rotation == 1 ? 0 : 1;
        try
        {
            await RequestAsync(
                OperationNames.DisplayRotate,
                new JsonObject { ["name"] = device.Name, ["rotation"] = next },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            device.Rotation = next;
            StatusMessage = next == 1 ? "Rotation: landscape." : "Rotation: portrait.";
        }
        catch (Exception ex)
        {
            ShowError(ex, "display.rotate", SelectedDevice?.Name);
        }
    }

    private async Task ScreenshotAsync()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        try
        {
            var data = await RequestAsync(
                OperationNames.FrameSnapshot,
                new JsonObject { ["name"] = device.Name, ["width"] = 1080 },
                TimeSpan.FromSeconds(60)).ConfigureAwait(true);
            var bytes = Convert.FromBase64String(data["pngBase64"]!.GetValue<string>());
            var directory = Path.Combine(_paths.Root, "screenshots");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{device.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            ScreenshotPath = path;
            StatusMessage = "Screenshot saved: " + path;
            _captureRestricted = false;
            _captureRestrictedReason = "";
            UpdateStage();
            var size = TryReadPngSize(bytes, out var width, out var height) ? $"{width}×{height} PNG" : "PNG";
            ShowToast("Screenshot saved", $"{device.Name} · {size} · {DateTime.Now:HH:mm:ss}", path);
        }
        catch (Exception ex)
        {
            ShowError(ex, "frame.snapshot", device.Name);
        }
    }

    private async Task InstallApkDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Install an APK on the selected guest",
            Filter = "Android packages (*.apk)|*.apk",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            await InstallApkAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    private async Task BuildApkSetDialogAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select an Android App Bundle",
            Filter = "Android App Bundle (*.aab)|*.aab",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true || MessageBox.Show(
                "Build the APK set with your Android debug key for local testing?",
                "Test signing", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync("Building test APK set...", async () =>
        {
            var result = await RequestAsync(
                OperationNames.AppBuildApkSet,
                new JsonObject { ["path"] = dialog.FileName, ["testSigning"] = true },
                TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            StatusMessage = $"Saved test-signed APK set to {result["path"]!.GetValue<string>()}.";
        }).ConfigureAwait(true);
    }

    public async Task InstallApkAsync(string path)
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        ApkPath = path;
        BeginFrameHold("the APK install");        try
        {
            await RunBusyAsync("Installing " + Path.GetFileName(path) + "...", async () =>
            {
            var install = await RequestAsync(
                OperationNames.AppInstall,
                new JsonObject { ["name"] = device.Name, ["path"] = path, ["inspectManifest"] = true },
                TimeSpan.FromMinutes(10)).ConfigureAwait(true);
            var manifest = install["manifest"];
            var package = manifest?["package"]?.GetValue<string>();
            if (package is not null)
            {
                await RequestAsync(
                    OperationNames.AppLaunch,
                    new JsonObject { ["name"] = device.Name, ["package"] = package },
                    TimeSpan.FromSeconds(60)).ConfigureAwait(true);
                var abis = manifest?["nativeAbis"]?.AsArray().Select(abi => abi!.GetValue<string>()).ToArray() ?? [];
                var abiText = abis.Length == 0 ? "no declared native ABIs" : string.Join(", ", abis);
                var sdk = manifest?["minSdk"]?.GetValue<string>() ?? "unknown";
                StatusMessage = $"Installed and launched {package}. Minimum API {sdk}, {abiText}.";
            }
            else
            {
                var reason = manifest?["reason"]?.GetValue<string>();
                StatusMessage = reason is null
                    ? $"Installed {Path.GetFileName(path)}. Launch it from the guest."
                    : $"Installed {Path.GetFileName(path)}. {reason}";
            }

            if (IsLogcatOpen)
            {
                await EnsureLogcatAsync().ConfigureAwait(true);
            }

            // The result is a toast as well as the status line: a drop or a
            // dialog install ends with Open/Copy for the exact file and guest.
            ShowToast("APK installed", StatusMessage, path);
            }).ConfigureAwait(true);
        }
        finally
        {
            EndFrameHold();
        }
    }

    private async Task LaunchAsync(string package)
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        BeginFrameHold("the app launch");
        try
        {
            await RequestAsync(
                OperationNames.AppLaunch,
                new JsonObject { ["name"] = device.Name, ["package"] = package },
                TimeSpan.FromSeconds(60)).ConfigureAwait(true);
            StatusMessage = "Launched " + package;
        }
        catch (Exception ex)
        {
            ShowError(ex, "app.launch", device.Name);
        }
        finally
        {
            EndFrameHold();
        }
    }

    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            var data = await RequestAsync(OperationNames.DiagnosticsSummary, null, TimeSpan.FromMinutes(2))
                .ConfigureAwait(true);
            Clipboard.SetText(data["summary"]!.GetValue<string>());
            StatusMessage = "Redacted diagnostics copied to the clipboard.";
        }
        catch (Exception ex)
        {
            ShowError(ex, "diagnostics.summary", SelectedDevice?.Name);
        }
    }

    private void ToggleTheme()
    {
        // UX-06: the settings drawer names both modes and shows the name and the
        // segmented selection, so the facts are re-raised with the swap.
        SetTheme(!ThemeManager.IsDark);
    }

    private void ClearLog()
    {
        LogEntries.Clear();
        _logInbox.Clear();
        _logPausedBuffer.Clear();
        Volatile.Write(ref _logBufferedCount, 0);
        NotifyLogBuffered();
        NotifyLogCounts();
    }

    private async Task ExportLogAsync()
    {
        var directory = Path.Combine(_paths.Root, "logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"logcat-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllLinesAsync(path, LogEntries.Select(e => e.Raw)).ConfigureAwait(true);
        StatusMessage = "Logcat exported: " + path;
    }

    private async Task SubscribeFramesAsync(DeviceItemViewModel device)
    {
        await _subscribeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // UX-03: a frame subscription is only opened for a guest the
            // supervisor reports as up. Starting one while Android boots
            // holds the guest's ADB channel, which starves the status reads the
            // boot stepper and the state pill depend on.
            // UX-18: the lifecycle decides, so a guest whose own boot answer
            // says it is up is attached even if the record still says Starting.
            if (_frameSubscription is not null
                || !device.Lifecycle.CanAttachFrames
                || SelectedDevice?.Name != device.Name)
            {
                return;
            }
            DesktopLog.Write("subscribe request");
            Volatile.Write(ref _frameSubscribePending, true);
            var data = await RequestAsync(
                OperationNames.FrameSubscribe,
                new JsonObject
                {
                    ["name"] = device.Name,
                    ["format"] = "rgb",
                    ["width"] = _halfCapture ? 270 : 540,
                    ["height"] = _halfCapture ? 600 : 1200,
                },
                TimeSpan.FromSeconds(60)).ConfigureAwait(true);
            var subscription = data["subscription"]!.GetValue<string>();
            if (SelectedDevice?.Name != device.Name)
            {
                await RequestAsync(OperationNames.FrameUnsubscribe,
                    new JsonObject { ["subscription"] = subscription }, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
                return;
            }

            Volatile.Write(ref _frameSubscription, subscription);
            DesktopLog.Write($"frame subscribed {device.Name} sub={subscription}");
            _frameWaitSince = DateTimeOffset.UtcNow;
            StartFrameLoop();
            if (SelectedDevice?.Name == device.Name)
            {
                UpdateStage();
            }
        }
        catch (Exception ex)
        {
            // A frame subscription that fails while Android is still starting is
            // a normal part of boot; it is reported in the status line and the
            // frame-wait stage, not as a persistent error the operator must clear.
            //
            // UX-18: the same is true when the guest goes away between the poll
            // and the subscribe - which is what a Magisk lab transaction does to
            // its guest, over and over. That refusal is not an error the
            // operator has to clear: the poll subscribes again as soon as the
            // guest is up.
            if (device.Lifecycle.Phase == DevicePhase.Starting
                || ex is SupervisorRequestException { Code: ErrorCodes.NotRunning })
            {
                StatusMessage = "Frame stream waits for the guest display: " + ex.Message;
            }
            else
            {
                ShowError(ex, "frame.subscribe", device.Name);
            }
        }
        finally
        {
            Volatile.Write(ref _frameSubscribePending, false);
            _subscribeGate.Release();
        }
    }

    private async Task UnsubscribeFramesAsync()
    {
        if (_frameSubscription is null)
        {
            return;
        }

        var subscription = _frameSubscription;
        Volatile.Write(ref _frameSubscription, null);
        try
        {
            await RequestAsync(
                OperationNames.FrameUnsubscribe,
                new JsonObject { ["subscription"] = subscription },
                TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        }
        catch (Exception)
        {
        }
    }

    private async Task EnsureLogcatAsync()
    {
        // UX-18: the log stream follows the guest's engine, so it attaches
        // while Android is still booting - the boot log is worth reading.
        if (_logcatSubscription is not null || SelectedDevice is null || !SelectedDevice.Lifecycle.EngineAlive)
        {
            NotifyLogFilterState();
            return;
        }

        try
        {
            var data = await RequestAsync(
                OperationNames.LogcatSubscribe,
                new JsonObject { ["name"] = SelectedDevice.Name },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            Volatile.Write(ref _logcatSubscription, data["subscription"]!.GetValue<string>());
            // UX-08: a successful attach clears the drawer's own error band.
            SetLogcatError("");
        }
        catch (Exception ex)
        {
            // UX-08: a refused subscription now reports inside the drawer, beside
            // the Retry that re-attaches it. The window-wide bar is left for the
            // operations it can retry itself (it re-reads the whole workspace), and
            // the status bar stays free of a sentence that would outlive the
            // drawer's own band. A cancellation is the app's own lifecycle (the
            // drawer was closed, the guest changed), not a refusal, so it leaves
            // the band empty and lets the chip and the empty state say "not
            // attached" without claiming something failed.
            //
            // UX-18: a guest that is not running is the same case - the stream
            // attaches again by itself when the guest is up, so it is not a
            // refusal the operator has to act on either.
            if (ex is OperationCanceledException
                || ex is SupervisorRequestException { Code: ErrorCodes.NotRunning })
            {
                SetLogcatError("");
            }
            else
            {
                SetLogcatError(ex.Message);
            }
        }
        finally
        {
            NotifyLogFilterState();
        }
    }

    private async Task StopLogcatAsync()
    {
        if (_logcatSubscription is null)
        {
            NotifyLogFilterState();
            return;
        }

        var subscription = _logcatSubscription;
        Volatile.Write(ref _logcatSubscription, null);
        try
        {
            await RequestAsync(
                OperationNames.LogcatUnsubscribe,
                new JsonObject { ["subscription"] = subscription },
                TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        catch (Exception)
        {
        }
        finally
        {
            NotifyLogFilterState();
        }
    }

    private void OnSupervisorEvent(EventEnvelope envelope)
    {
        switch (envelope.Event)
        {
            case EventNames.Frame:
                if ((envelope.Subscription == Volatile.Read(ref _frameSubscription) || Volatile.Read(ref _frameSubscribePending))
                    && envelope.Data is JsonObject frame && frame["path"]?.GetValue<string>() is { } path)
                {
                    var format = frame["format"]?.GetValue<string>() ?? "png";
                    var frameWidth = frame["width"]?.GetValue<int>() ?? 0;
                    var frameHeight = frame["height"]?.GetValue<int>() ?? 0;
                    var stride = frame["stride"]?.GetValue<int>() ?? frameWidth * 3;
                    var guestWidth = frame["guestWidth"]?.GetValue<int>() ?? 0;
                    var guestHeight = frame["guestHeight"]?.GetValue<int>() ?? 0;
                    var dropped = frame["dropped"]?.GetValue<int>() ?? 0;
                    var sequence = frame["seq"]?.GetValue<int>() ?? 0;
                    var receivedUtc = frame["receivedUtc"]?.GetValue<DateTimeOffset>();
                    var writtenUtc = frame["writtenUtc"]?.GetValue<DateTimeOffset>();
                    _frames.Writer.TryWrite(new PendingFrame(envelope.Subscription, path, format, frameWidth, frameHeight,
                        stride, guestWidth, guestHeight, sequence, dropped, receivedUtc, writtenUtc, Stopwatch.GetTimestamp()));
                }

                break;

            case EventNames.Logcat:
                if (envelope.Subscription == Volatile.Read(ref _logcatSubscription)
                    && envelope.Data is JsonObject entry)
                {
                    if (LogPaused)
                    {
                        // Hold the line instead of dropping it; resume flushes it.
                        _logPausedBuffer.Enqueue(LogEntryViewModel.From(entry));
                        while (_logPausedBuffer.Count > 2500 && _logPausedBuffer.TryDequeue(out _))
                        {
                        }

                        Volatile.Write(ref _logBufferedCount, _logPausedBuffer.Count);
                    }
                    else
                    {
                        _logInbox.Enqueue(LogEntryViewModel.From(entry));
                    }
                }

                break;

            case EventNames.SubscriptionClosed:
                _dispatcher.BeginInvoke(() =>
                {
                    var reason = envelope.Data?["reason"]?.GetValue<string>() ?? "closed";

                    // UX-18: a closed subscription is not the end of the stream.
                    // The supervisor closes it when the guest's engine goes away
                    // or a session is reset; the window used to keep the closed
                    // id, so the status poll saw "already subscribed" and never
                    // opened a new one - the viewport stayed dead for the rest of
                    // the session. Clearing the field lets the poll re-attach
                    // as soon as the guest is up again.
                    var kind = SubscriptionClose.Which(
                        envelope.Subscription,
                        Volatile.Read(ref _frameSubscription),
                        Volatile.Read(ref _logcatSubscription));

                    switch (kind)
                    {
                        case SubscriptionKind.Frames:
                            Volatile.Write(ref _frameSubscription, null);
                            StatusMessage = "Frame stream closed: " + reason;
                            break;

                        case SubscriptionKind.Logcat:
                            Volatile.Write(ref _logcatSubscription, null);
                            NotifyLogFilterState();
                            StatusMessage = "Log stream closed: " + reason;
                            break;

                        default:
                            StatusMessage = "Subscription closed: " + reason;
                            break;
                    }
                });
                break;

            case EventNames.Progress:
                _dispatcher.BeginInvoke(() =>
                {
                    var phase = envelope.Data?["stage"]?.GetValue<string>()
                                ?? envelope.Data?["phase"]?.GetValue<string>()
                                ?? "progress";
                    StatusMessage = phase;
                });
                break;
        }
    }

    private void StartFrameLoop()
    {
        if (_frameLoopRunning)
        {
            return;
        }

        _frameLoopRunning = true;
        _ = Task.Run(FrameLoopAsync);
    }

    private async Task FrameLoopAsync()
    {
        await foreach (var pending in _frames.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
        {
            try
            {
                var bitmap = LoadFrame(pending);
                if (bitmap is not null)
                {
                    var now = DateTimeOffset.UtcNow;
                    await _dispatcher.InvokeAsync(() => PresentFrame(bitmap, pending, now), DispatcherPriority.Render);
                }
            }
            catch (Exception ex)
            {
                DesktopLog.Write("decode failed: " + ex.GetType().Name + ": " + ex.Message);
                // A replaced file or a transient decode failure must not stop the
                // pipeline; the next frame is already on its way.
            }
        }
    }

    private static BitmapSource? LoadFrame(PendingFrame frame)
    {
        if (frame.Format is "rgb24" or "bgr24")
        {
            if (frame.Width <= 0 || frame.Height <= 0)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(frame.Path);
            if (bytes.Length != frame.Width * frame.Height * 3)
            {
                return null;
            }

            var source = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Rgb24,
                null,
                bytes,
                frame.Stride);
            source.Freeze();
            return source;
        }

        return LoadBitmap(frame.Path);
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 540;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void PresentFrame(BitmapSource bitmap, PendingFrame source, DateTimeOffset received)
    {
        if (source.Subscription != _frameSubscription)
        {
            return;
        }
        _droppedFrames += _frameSequence > 0 && source.Sequence > _frameSequence
            ? source.Sequence - _frameSequence - 1
            : source.Dropped;
        _frameSequence = source.Sequence;
        GuestWidth = source.GuestWidth > 0 ? source.GuestWidth : GuestWidth;
        GuestHeight = source.GuestHeight > 0 ? source.GuestHeight : GuestHeight;
        var hadFrame = Frame is not null;
        Frame = bitmap;
        var widthChanged = FrameWidth != bitmap.PixelWidth || FrameHeight != bitmap.PixelHeight;
        FrameWidth = bitmap.PixelWidth;
        FrameHeight = bitmap.PixelHeight;

        // UX-03: a delivered frame is the only thing that clears the frame-wait
        // and stale stages; no timer does it.
        _frameWaitSince = null;
        if (IsFrameStale)
        {
            IsFrameStale = false;
        }

        if (!hadFrame)
        {
            UpdateStage();
        }

        if (_lastFramePresented != default)
        {
            var interval = (received - _lastFramePresented).TotalMilliseconds;
            if (interval > 0 && interval < 5000)
            {
                _frameIntervals.Add(interval);
                while (_frameIntervals.Count > 120)
                {
                    _frameIntervals.RemoveAt(0);
                }

                var average = _frameIntervals.Average();
                FramesPerSecond = average > 0 ? 1000.0 / average : 0;
            }
        }

        _lastFramePresented = received;
        if (widthChanged || (received - _lastFrameInfoUpdated).TotalMilliseconds >= 250)
        {
            _lastFrameInfoUpdated = received;
            OnPropertyChanged(nameof(FrameInfo));
            OnPropertyChanged(nameof(GuestDisplayText));
            OnPropertyChanged(nameof(DeliveredFrameText));
            OnPropertyChanged(nameof(FrameRateText));
            OnPropertyChanged(nameof(DroppedFramesText));
            // UX-05: one sparkline sample per 250 ms of delivered frames.
            PushTelemetrySample();
        }

        if (_pendingInput is { } pending)
        {
            var color = SampleMarkerColor();
            if (pending.BaselineColor is int baseline && color is int current && current != baseline)
            {
                var latency = (DateTimeOffset.UtcNow - pending.DispatchedUtc).TotalMilliseconds;
                _lastInputLatency = latency;
                _inputLatencies.Add(latency);
                _pendingInput = null;
                OnPropertyChanged(nameof(InputLatencyText));

            }
        }

        if (_probe is { FrameAvailableMs: null } probe && SelectedDevice?.Name == probe.Device
            && bitmap.PixelWidth == probe.Width && bitmap.PixelHeight == probe.Height
            && ProbePixel(ReadProbeColumn(bitmap, probe.Row, 1), 0) == probe.ExpectedColor)
        {
            probe.FrameAvailableMs = probe.Clock.Elapsed.TotalMilliseconds;
            if (source.ReceivedUtc is { } receivedUtc)
            {
                probe.ChangedFrameReceivedMs = (receivedUtc - probe.DispatchUtc).TotalMilliseconds;
            }
            if (source.ReceivedUtc is { } frameReceived && source.WrittenUtc is { } frameWritten)
            {
                probe.SupervisorFrameWriteMs = (frameWritten - frameReceived).TotalMilliseconds;
            }
            probe.FrameDecodeAndDispatchMs = Stopwatch.GetElapsedTime(source.EventTimestamp).TotalMilliseconds;
        }

    }

    /// <summary>Colour of the sample app marker at (w/2, h*0.30), BGRA packed.</summary>
    private int? SampleMarkerColor()
    {
        var frame = Frame;
        if (frame is null || frame.PixelWidth < 4 || frame.PixelHeight < 4)
        {
            return null;
        }

        try
        {
            var x = frame.PixelWidth / 2;
            var y = (int)(frame.PixelHeight * 0.30);
            x = Math.Clamp(x, 0, frame.PixelWidth - 1);
            y = Math.Clamp(y, 0, frame.PixelHeight - 1);

            BitmapSource source = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[2] << 16) | (pixel[1] << 8) | pixel[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void DrainLog()
    {
        var added = 0;
        while (added < 300 && _logInbox.TryDequeue(out var entry))
        {
            entry.ApplyHighlight(_logSearch);
            LogEntries.Add(entry);
            added++;
        }

        var trimmed = false;
        while (LogEntries.Count > 2500)
        {
            LogEntries.RemoveAt(0);
            trimmed = true;
        }

        // Fix pass item 7: the dock's count line is derived from these lines, so
        // it is re-read when the collection actually changed.
        if (added > 0 || trimmed)
        {
            NotifyLogCounts();
        }

        // The paused buffer is filled on the pipe thread; its chip is refreshed
        // here, on the UI thread, only when the number actually moved.
        var buffered = Volatile.Read(ref _logBufferedCount);
        if (buffered != _logBufferedNotified)
        {
            _logBufferedNotified = buffered;
            NotifyLogBuffered();
        }
    }

    private bool FilterLog(object item)
    {
        if (item is not LogEntryViewModel entry)
        {
            return false;
        }

        // UX-08: one rule, in LogcatPresentation, for the five level chips.
        if (!LogFilter.Allows(entry.Level))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(LogTag)
            && !entry.Tag.Contains(LogTag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(LogSearch)
            && !entry.Message.Contains(LogSearch, StringComparison.OrdinalIgnoreCase)
            && !entry.Tag.Contains(LogSearch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task RunBusyAsync(string busyText, Func<Task> action)
    {
        IsBusy = true;
        BusyText = busyText;
        ClearError();
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError(ex, busyText);
            StatusMessage = "Operation failed.";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            UpdateStage();
        }
    }

    // ---- UX-03 state mapping ---------------------------------------------

    /// <summary>
    /// Chooses the viewport stage from the supervisor's reported state. Every
    /// branch is evidence: instance.status fields, the readiness timestamps or
    /// the presence of a delivered frame. No timer decides that a guest is ready.
    /// </summary>
    private void UpdateStage()
    {
        UpdateStageCore();
        // UX-05: the stage chips, the boot elapsed figure and the status bar all
        // re-read the same evidence after every stage decision.
        NotifyLiveSurfaces();
    }

    private void UpdateStageCore()
    {
        if (ShowSetup)
        {
            StageVisible = false;
            ShowBootStages = false;
            return;
        }

        var device = SelectedDevice;
        if (device is null)
        {
            SetStage("info", "No device selected",
                "Create or select a managed device to stream its Android display.",
                "Guests are isolated per instance; nothing boots until you start one.");
            ShowBootStages = false;
            return;
        }

        // UX-18: the stage reads the one lifecycle, so the Studio says the same
        // thing about a guest as the device library and the command palette do.
        var lifecycle = device.Lifecycle;

        if (lifecycle.Phase == DevicePhase.Failed)
        {
            SetStage("error", "Guest failed",
                string.IsNullOrWhiteSpace(device.LastError)
                    ? "The supervisor reported a failed state for this guest."
                    : device.LastError,
                "Inspect Diagnostics and the instance log, then retry. A cold boot is only safe once the cause is known.");
            ShowBootStages = true;
            return;
        }

        if (lifecycle.Phase == DevicePhase.EngineGone)
        {
            SetStage("error", "The guest's engine is gone",
                string.IsNullOrWhiteSpace(device.LastError)
                    ? $"The engine process for {device.Name} is no longer running."
                    : device.LastError,
                "The guest stopped outside the supervisor. Start it again to bring it back.");
            ShowBootStages = false;
            return;
        }

        if (_labRecoveryRequired)
        {
            SetStage("warning", "Lab recovery required",
                string.IsNullOrWhiteSpace(_labRecoveryDetail) ? MagiskState : _labRecoveryDetail,
                "Agents are fenced while a lab needs recovery. Use Recover stock in Root Lab first.");
            ShowBootStages = false;
            return;
        }

        if (_labInstallInProgress)
        {
            // UX-18: a transaction that is still running is not a failure. The
            // guest is rebooted while the patch is installed, so the stage says
            // what is happening instead of claiming that recovery is due.
            SetStage("info", "Magisk lab in progress",
                string.IsNullOrWhiteSpace(MagiskState)
                    ? "The lab transaction is running."
                    : MagiskState,
                "The guest reboots while the patch is installed. The lab journal keeps every step.");
            ShowBootStages = false;
            return;
        }

        if (lifecycle.IsRunning && HasFrame)
        {
            StageVisible = false;
            ShowBootStages = false;
            return;
        }

        if (_captureRestricted)
        {
            SetStage("warning", "Capture restricted",
                string.IsNullOrWhiteSpace(_captureRestrictedReason)
                    ? "The guest refused the last capture."
                    : _captureRestrictedReason,
                "A FLAG_SECURE window blocks pixels. Close the protected app or window in the guest, then capture again.");
            ShowBootStages = false;
            return;
        }

        if (lifecycle.Phase == DevicePhase.Starting)
        {
            // igr-feedback-ux05 item 2: the elapsed figure lives only in the
            // stepper card's mono line (BootElapsedText); "Elapsed since launch:
            // 48s" here was the same number twice in one card.
            SetStage("info", "Starting Android",
                "The engine is running and the supervisor reports each boot stage as it lands.",
                "");
            ShowBootStages = true;
            return;
        }

        if (lifecycle.Phase == DevicePhase.Stopping)
        {
            SetStage("info", "Stopping the guest",
                "The supervisor is shutting the engine down. The window can stay open; the guest is not tied to it.",
                "");
            ShowBootStages = false;
            return;
        }

        if (lifecycle.IsRunning)
        {
            var waited = _frameWaitSince is { } since
                ? Math.Max(0, (DateTimeOffset.UtcNow - since).TotalSeconds)
                : 0;
            SetStage("info", "Waiting for the first frame",
                $"Android reports running. The frame stream has not delivered a frame yet — {waited:N0}s.",
                waited >= 10
                    ? "If the guest stays dark, open Logcat or Diagnostics and check the instance log for the frame pump."
                    : "");
            ShowBootStages = true;
            return;
        }

        SetStage("info", "Guest stopped",
            $"{device.Name} is stopped. Start it to stream the guest display.",
            "Stopped guests keep their profile, disks and snapshots.");
        ShowBootStages = false;
    }

    private void SetStage(string tone, string title, string detail, string hint)
    {
        StageTone = tone;
        StageTitle = title;
        StageDetail = detail;
        StageHint = hint;
        StageVisible = true;
        NotifyLiveSurfaces();
    }

    /// <summary>
    /// Maps instance.status evidence onto the three boot steps. A stage the
    /// supervisor cannot report yet is worded "not reported", never "done".
    /// </summary>
    private void ApplyBootEvidence(JsonObject status)
    {
        var engineAlive = status["engineAlive"]?.GetValue<bool>() == true;
        var adbState = (status["adbState"]?.GetValue<string>() ?? "").Trim();
        var bootCompleted = status["bootCompleted"]?.GetValue<string>() == "1";
        // adb get-state returns "device", "offline" or an error sentence. Only
        // the exact word means the transport is usable.
        var adbOnline = adbState == "device";
        var interactive = false;
        if (status["readiness"] is JsonObject readiness)
        {
            interactive = readiness["interactiveUtc"] is not null;
            _launchedUtc = TryReadUtc(readiness["launchedUtc"]) ?? _launchedUtc;
        }

        // UX-18: the guest's own focused window is the evidence that it is
        // interactive. A guest this supervisor did not start (adopted after a
        // restart) has no readiness stamp, and its window answers the same
        // question.
        if (!interactive && bootCompleted)
        {
            var focus = (status["foreground"]?.GetValue<string>() ?? "").Trim();
            interactive = focus.Length > 0;
        }

        // igr-feedback-ux05 item 2: the same evidence that fills the third boot
        // step gates the nav cluster.
        if (_guestInteractive != interactive)
        {
            _guestInteractive = interactive;
            OnPropertyChanged(nameof(IsGuestInteractive));
        }

        // UX-18: the frame stream's own health. The supervisor says whether the
        // capture is running, and the guest's transport says whether the guest
        // is still answering. Together they tell an idle guest apart from a
        // stalled stream.
        if (status["frameStream"] is JsonObject frameStream)
        {
            _frameStreamRunning = frameStream["running"]?.GetValue<bool>();
        }

        _guestResponsive = adbOnline;
        UpdateFrameHealth();

        // UX-18: the stepper reads the same lifecycle as every other surface,
        // so a guest that reported boot completion is shown as booted even when
        // the record's word still says Starting.
        var lifecycle = DeviceLifecycle.From(
            status["state"]?.GetValue<string>(),
            status["engineAlive"]?.GetValue<bool>(),
            status["pid"]?.GetValue<int>() is > 0,
            bootCompleted);
        var starting = lifecycle.Phase == DevicePhase.Starting;
        var booting = lifecycle.Phase is DevicePhase.Starting or DevicePhase.Running;

        BootStages[0].Set(
            engineAlive ? "done" : starting ? "active" : "pending",
            engineAlive ? "process alive" : starting ? "starting process" : "not started");
        BootStages[1].Set(
            bootCompleted ? "done" : adbOnline ? "active" : starting ? "active" : booting ? "unknown" : "pending",
            bootCompleted ? "boot_completed reported"
                : adbOnline ? "ADB transport online"
                : starting ? (adbState == "offline" ? "ADB transport offline" : "waiting for ADB")
                : booting ? "not reported" : "not started");
        BootStages[2].Set(
            interactive ? "done" : bootCompleted ? "active" : "pending",
            interactive ? "launcher interactive"
                : bootCompleted ? "waiting for the launcher"
                : "not reached");
    }

    private void ResetBootStages()
    {
        if (_guestInteractive)
        {
            _guestInteractive = false;
            OnPropertyChanged(nameof(IsGuestInteractive));
        }

        foreach (var stage in BootStages)
        {
            stage.Set("pending", "not started");
        }
    }

    /// <summary>Starts the frame-wait clock once the guest reports running.</summary>
    private void UpdateFrameWait(DeviceItemViewModel device)
    {
        if (!device.IsRunning || HasFrame)
        {
            if (!device.IsRunning)
            {
                _frameWaitSince = null;
            }

            return;
        }

        _frameWaitSince ??= DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// A frame older than the stale budget is visibly stale and stops accepting
    /// input; the last delivered frame is timestamped in the chip, never hidden.
    ///
    /// UX-18: the budget alone is not a fault. The emulator's image stream is
    /// change driven, so a guest whose screen does not change sends no frames:
    /// that is an idle guest with a current picture, not a stalled stream. The
    /// stream is only stalled when the supervisor says the capture is not
    /// running or the guest stopped answering.
    /// </summary>
    private void UpdateFrameHealth()
    {
        var running = SelectedDevice?.Lifecycle.IsRunning == true;
        var age = _lastFramePresented == default
            ? 0
            : Math.Max(0, (DateTimeOffset.UtcNow - _lastFramePresented).TotalSeconds);
        var state = FrameStreamHealth.Evaluate(
            running,
            HasFrame,
            age,
            streamBroken: _frameStreamRunning == false,
            guestResponsive: _guestResponsive,
            held: _frameHold.Length > 0);

        if (_frameState != state)
        {
            _frameState = state;
            OnPropertyChanged(nameof(StaleFrameTitle));
            OnPropertyChanged(nameof(StaleFrameDetail));
            OnPropertyChanged(nameof(StaleFrameTone));
            OnPropertyChanged(nameof(IsInputPaused));
        }

        IsFrameStale = FrameStreamHealth.IsStalled(state);

        // The age is part of the sentence, so refresh it while it holds.
        OnPropertyChanged(nameof(StageGuestChipLive));
        OnPropertyChanged(nameof(StageGuestChipTooltipLive));
    }

    /// <summary>
    /// One-second presentation tick: refresh only the sentences that contain a
    /// timestamp. It cannot promote a stage, clear an error or declare readiness.
    /// </summary>
    private void TickClock()
    {
        if (StageVisible && SelectedDevice is { } device
            && (device.Lifecycle.Phase == DevicePhase.Starting || (device.IsRunning && !HasFrame)))
        {
            UpdateStage();
        }

        if (IsFrameStale)
        {
            OnPropertyChanged(nameof(StaleFrameTitle));
        }

        // UX-05: "Elapsed 53s" is the only thing the one-second tick may touch
        // besides those sentences - it re-reads the supervisor's launch stamp.
        OnPropertyChanged(nameof(BootElapsedText));
    }

    // igr-feedback-ux05 item 2: the "48s" string helper went with the duplicated
    // "Elapsed since launch" sentence. BootElapsedText is the one elapsed figure.

    private static DateTimeOffset? TryReadUtc(JsonNode? node)
    {
        var raw = node?.GetValue<string>();
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// A closed pipe, a refused connect or an unavailable endpoint is a
    /// supervisor failure; nothing else in the setup card should be blamed for
    /// it.
    /// </summary>
    private static bool IsSupervisorFailure(Exception ex) =>
        ex is SupervisorUnavailableException or IOException or ObjectDisposedException
        || ex.Message.Contains("Pipe is broken", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("supervisor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a failed operation into a persistent, typed error bar: plain cause,
    /// next safe action, and copyable details. It never disappears on its own.
    /// </summary>
    private void ShowError(Exception ex, string operation, string? device = null)
    {
        var code = ex switch
        {
            SupervisorRequestException supervisor => supervisor.Code,
            SupervisorUnavailableException => "supervisor_unavailable",
            TimeoutException => "timeout",
            OperationCanceledException => "cancelled",
            IOException => "supervisor_unavailable",
            _ when IsSupervisorFailure(ex) => "supervisor_unavailable",
            _ => "error",
        };

        var (title, action) = DescribeError(code);
        ErrorBarTitle = title;
        ErrorBarText = ex.Message;
        ErrorBarAction = action;
        // Retry re-reads the supervisor, the SDK report and the device state; it
        // is only offered for failures where that is safe (no mutation is
        // replayed automatically).
        ShowErrorRetry = code is "supervisor_unavailable" or "timeout" or "adb_failure"
            or "instance_unavailable" or "image_missing";
        ErrorBarDetails = string.Join(Environment.NewLine, new[]
        {
            $"code: {code}",
            $"operation: {operation}",
            $"device: {device ?? SelectedDevice?.Name ?? "(none)"}",
            $"time: {DateTimeOffset.Now:O}",
            $"workspace: {_paths.Root}",
            $"message: {ex.Message}",
        });
        HasErrorBar = true;
        ErrorMessage = ex.Message;

        if (code == "secure_window")
        {
            _captureRestricted = true;
            _captureRestrictedReason = ex.Message;
        }

        UpdateStage();
    }

    private static (string Title, string Action) DescribeError(string code) => code switch
    {
        "supervisor_unavailable" => ("Supervisor is not connected",
            "Start the workspace supervisor for this root, then Retry."),
        "timeout" => ("The operation timed out",
            "Check the guest state and the instance log, then retry when the engine is responsive."),
        "instance_not_running" => ("The guest is not running",
            "Start the device, then repeat the action."),
        "instance_already_running" => ("The guest is already running",
            "Stop it first if you need a cold start."),
        "instance_unavailable" => ("The guest is unavailable",
            "Refresh the device list and reattach before retrying."),
        "low_disk" => ("Not enough disk space",
            "Free space on the workspace volume; verified recovery artifacts are never deleted automatically."),
        "image_missing" => ("The pinned system image is missing",
            "Install the pinned image with sdkmanager, then Refresh."),
        "port_conflict" => ("A required port is in use",
            "Close whatever owns the ports, or recreate the guest to get a new pair."),
        "capability_unavailable" => ("This guest cannot do that",
            "The image does not support it. Use a supported profile; the reason above names the capability."),
        "secure_window" => ("Capture restricted by the guest",
            "A FLAG_SECURE window blocks pixels. Close the protected window in the guest, then capture again."),
        "install_failed" => ("The APK could not be installed",
            "Check the package, its ABI and minimum API, then try again."),
        "app_launch_failed" => ("The app did not start",
            "Check the package id and Logcat, then launch it again."),
        "adb_failure" => ("ADB reported a failure",
            "Refresh the device state; if it persists, cold boot the guest."),
        "denied" or "forbidden_scope" or "unauthorized" => ("Permission denied",
            "Pair the client, or approve the exact action, then retry."),
        "cancelled" => ("The operation was cancelled",
            "Nothing else changed. Retry when ready."),
        _ => ("The operation failed",
            "Retry, or copy the details into a report. The guest state was not changed silently."),
    };

    private void ClearError()
    {
        HasErrorBar = false;
        ShowErrorRetry = false;
        ErrorBarTitle = "";
        ErrorBarText = "";
        ErrorBarAction = "";
        ErrorBarDetails = "";
        ErrorMessage = "";
    }

    private void CopyErrorDetails()
    {
        try
        {
            if (ErrorBarDetails.Length == 0)
            {
                return;
            }

            Clipboard.SetText(ErrorBarDetails);
            StatusMessage = "Error details copied to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not copy the error details: " + ex.Message;
        }
    }

    // ---- UX-03 feedback surfaces -----------------------------------------

    /// <summary>
    /// The APK drag affordance. It names the exact target guest, refuses a
    /// stopped guest or a non-APK file with words, and is driven by the same
    /// selection and run state the install command uses.
    /// </summary>
    public void ShowDropTarget(string[]? files)
    {
        DropVisible = true;
        var device = SelectedDevice;
        if (device is null)
        {
            DropAccepted = false;
            DropTitle = "Select a device first";
            DropDetail = "An install targets exactly one guest. Choose a device, then drop the APK again.";
            return;
        }

        if (!device.Lifecycle.CanInstall)
        {
            DropAccepted = false;
            DropTitle = device.Lifecycle.Phase == DevicePhase.Starting
                ? $"{device.Name} is still starting"
                : $"Start {device.Name} to install";
            DropDetail = device.Lifecycle.Phase == DevicePhase.Starting
                ? "The supervisor installs into a running guest. Drop the APK again once Android is interactive."
                : "The supervisor installs into a running guest. Start this device, then drop the APK again.";
            return;
        }

        if (files is { Length: > 0 }
            && !files.Any(path => path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
        {
            DropAccepted = false;
            DropTitle = "Only .apk files install here";
            DropDetail = "Split APK sets use Build APK set in the palette, and an .aab bundle has to be built first.";
            return;
        }

        DropAccepted = true;
        DropTitle = $"Drop APK to install on {device.Name}";
        DropDetail = "The APK installs on this guest and launches when its package is readable.";
    }

    public void HideDropTarget()
    {
        DropVisible = false;
        DropAccepted = false;
    }

    private void ShowToast(string title, string text, string? path)
    {
        _toastTimer.Interval = TimeSpan.FromSeconds(6);
        ToastTitle = title;
        ToastText = text;
        _toastPath = path;
        OnPropertyChanged(nameof(HasToastPath));
        ToastVisible = true;
        ToastHovered = false;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>
    /// UX-08 fix pass: a transient status sentence becomes one short toast, bottom
    /// centre, for 2.5 s. Housekeeping sentences ("Supervisor connected.") and
    /// blanks are skipped, and a new sentence replaces the one on screen, so only
    /// one toast is ever visible.
    /// </summary>
    private void ShowStatusToast(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text is "Supervisor connected." or "Refreshed." or "Starting desktop...")
        {
            return;
        }

        _toastTimer.Interval = TimeSpan.FromSeconds(2.5);
        ToastTitle = text;
        ToastText = "";
        _toastPath = null;
        OnPropertyChanged(nameof(HasToastPath));
        ToastVisible = true;
        ToastHovered = false;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastVisible = false;
    }

    private void CopyToastPath()
    {
        if (_toastPath is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            Clipboard.SetText(path);
            StatusMessage = "Copied: " + path;
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not copy the path: " + ex.Message;
        }
    }

    private void OpenToastArtifact()
    {
        if (_toastPath is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open the artifact: " + ex.Message;
        }
    }

    /// <summary>
    /// UX-06: copies a workspace path from the settings drawer's path row. The
    /// same Clipboard call and the same status sentence as the capture toast, so
    /// a copy behaves identically wherever it is offered.
    /// </summary>
    public void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
            StatusMessage = "Copied: " + path;
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not copy the path: " + ex.Message;
        }
    }

    /// <summary>
    /// UX-07 fix pass: copies the raw doctor report exactly as the supervisor's
    /// fields were assembled for the card (redacted by the supervisor, no local
    /// values added), the same text the row tooltips name.
    /// </summary>
    public void CopyDoctorReport()
    {
        if (string.IsNullOrWhiteSpace(DoctorText))
        {
            return;
        }

        try
        {
            Clipboard.SetText(DoctorText);
            StatusMessage = "Doctor report copied to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not copy the doctor report: " + ex.Message;
        }
    }

    /// <summary>
    /// UX-06: opens a folder in Explorer. The folder exists by the time it is
    /// shown (the workspace root, and the capture/log folders are created on
    /// demand), so this never invents a location.
    /// </summary>
    public void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open the folder: " + ex.Message;
        }
    }

    /// <summary>Reads the exact pixel size from a PNG IHDR; used by the capture toast.</summary>
    private static bool TryReadPngSize(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 24
            || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            return false;
        }

        width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0;
    }

    /// <summary>
    /// Refreshes the ADB daemon identity of the selected guest so the Root Lab
    /// panel shows the effective user and the transport the daemon runs on.
    /// </summary>
    public async Task RefreshAdbStateAsync()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            var state = await RequestAsync(
                    OperationNames.InstanceAdbRootState,
                    new JsonObject { ["name"] = device.Name },
                    TimeSpan.FromSeconds(60))
                .ConfigureAwait(true);
            AdbUid = state["uid"]?.GetValue<string>() ?? "n/a";
            AdbRootCapable = state["rootCapable"]?.GetValue<bool>() ?? false;
            AdbBuildType = state["buildType"]?.GetValue<string>() ?? "";
            AdbTransportId = state["transportId"]?.GetValue<string>() ?? "";
        }
        catch (SupervisorRequestException ex)
        {
            AdbUid = "n/a";
            AdbRootCapable = false;
            AdbBuildType = "";
            AdbTransportId = "";
            StatusMessage = "ADB daemon state unavailable: " + ex.Message;
            DesktopLog.Write($"ADB daemon state unavailable: {ex.Message}");
        }

        try
        {
            var lab = await RequestAsync("magisk.status", new JsonObject { ["name"] = device.Name }, TimeSpan.FromSeconds(30));
            ApplyMagiskStatus(device, lab);
        }
        catch (SupervisorRequestException ex)
        {
            if (SelectedDevice?.Name == device.Name) MagiskState = "Magisk lab: " + ex.Message;
            _labRecoveryRequired = false;
            _labRecoveryDetail = "";
        }
        OnPropertyChanged(nameof(AdbUid));
        OnPropertyChanged(nameof(AdbUidValue));
        OnPropertyChanged(nameof(AdbDaemonValue));
        OnPropertyChanged(nameof(AdbUidTooltip));
        OnPropertyChanged(nameof(AdbRootCapable));
        OnPropertyChanged(nameof(AdbBuildType));
        OnPropertyChanged(nameof(AdbTransportId));
        OnPropertyChanged(nameof(AdbRootState));
        // UX-05: the Root Lab cards and the status bar read the same fields.
        OnPropertyChanged(nameof(AdbDaemonChip));
        OnPropertyChanged(nameof(AdbDaemonChipTone));
        OnPropertyChanged(nameof(AdbDaemonDetail));
        OnPropertyChanged(nameof(AdbUidLine));
        OnPropertyChanged(nameof(StatusUid));
        OnPropertyChanged(nameof(HasAdbUid));
        UpdateStage();
    }

    /// <summary>
    /// UX-03: reads only the Magisk lab transaction state for the selected
    /// guest, so a lab that needs stock recovery is visible on selection
    /// without opening the Root Lab panel or touching the ADB daemon.
    /// </summary>
    private async Task RefreshMagiskStatusAsync(DeviceItemViewModel device)
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            var lab = await RequestAsync("magisk.status", new JsonObject { ["name"] = device.Name }, TimeSpan.FromSeconds(30))
                .ConfigureAwait(true);
            if (SelectedDevice?.Name == device.Name)
            {
                ApplyMagiskStatus(device, lab);
            }
        }
        catch (Exception ex)
        {
            // A lab state that cannot be read must not take the instance
            // status poll with it: the poll keeps the boot stages and the
            // frame state current, and the lab row simply keeps its last
            // value.
            DesktopLog.Write($"Magisk lab state unavailable: {ex.Message}");
        }
    }

    private void ApplyMagiskStatus(DeviceItemViewModel device, JsonObject lab)
    {
        if (SelectedDevice?.Name != device.Name)
        {
            return;
        }

        // UX-18: the supervisor classifies the lab phase. A transaction that is
        // still running is reported as running, so the stage never claims that
        // a healthy install needs a stock recovery.
        var phase = lab["phase"]?.GetValue<string>() ?? "unknown";
        var recoveryRequired = lab["recoveryRequired"]?.GetValue<bool>() == true;
        var installInProgress = lab["installInProgress"]?.GetValue<bool>() == true;
        MagiskState = "Magisk lab: " + phase
            + (recoveryRequired ? " (recovery required)" : installInProgress ? " (install in progress)" : "");
        _labRecoveryRequired = recoveryRequired;
        _labInstallInProgress = installInProgress;
        _labRecoveryDetail = recoveryRequired
            ? $"The Magisk lab transaction is in phase '{phase}'. Use Recover stock in Root Lab before starting or changing this guest."
            : "";
    }

    private async Task SetAdbRootAsync(bool root)
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        // UX-09: rooting the guest's daemon is the one consequential ADB step, so it
        // asks for the same consent surface the lab actions use. Unroot stays a
        // single click: it only removes privilege.
        if (root && !AskConsent(AdbRootConsent(device)))
        {
            return;
        }

        try
        {
            var result = await RequestAsync(
                    OperationNames.InstanceAdbRoot,
                    new JsonObject { ["name"] = device.Name, ["root"] = root },
                    TimeSpan.FromMinutes(3))
                .ConfigureAwait(true);
            DesktopLog.Write($"ADB daemon uid {result["uidBefore"]} -> {result["uidAfter"]} (transport {result["transportId"]})");
        }
        catch (SupervisorRequestException ex)
        {
            DesktopLog.Write($"ADB {(root ? "root" : "unroot")} refused: {ex.Code} {ex.Message}");
            ShowError(ex, root ? "instance.adbRoot" : "instance.adbUnroot", device.Name);
        }

        await RefreshAdbStateAsync().ConfigureAwait(true);
    }

    private async Task InstallMagiskAsync()
    {
        if (SelectedDevice is not { } device) return;
        string? Pick(string title, string filter)
        {
            var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
        var patch = Pick("Select the verified Magisk lab ramdisk", "Ramdisk (*.img)|*.img");
        if (patch is null) return;
        var apk = Pick("Select the pinned Magisk manager APK", "Android package (*.apk)|*.apk");
        if (apk is null) return;
        var probe = Pick("Select the lab app root probe APK", "Android package (*.apk)|*.apk");
        if (probe is null) return;
        // UX-09: the consent surface is the app's own dialog, not the OS message
        // box. It prints the same facts (this guest, these three files, the same
        // consequences) and the hashes are read from the selected files for
        // display only. Declining here returns before the first supervisor
        // request, so Cancel creates no transaction and writes no journal entry.
        var artifacts = new[]
        {
            await ConsentArtifact.ReadAsync("Ramdisk", patch).ConfigureAwait(true),
            await ConsentArtifact.ReadAsync("Manager APK", apk).ConfigureAwait(true),
            await ConsentArtifact.ReadAsync("Root probe", probe).ConfigureAwait(true),
        };
        if (!AskConsent(InstallMagiskConsent(device, artifacts))) return;
        await RunBusyAsync("Checking Magisk lab inputs...", async () =>
        {
            var prepared = await RequestAsync("magisk.prepare", new JsonObject
            {
                ["name"] = device.Name, ["patch"] = patch, ["apk"] = apk,
                ["probe"] = probe, ["allowProvisioningAdbRoot"] = true,
            }, TimeSpan.FromMinutes(3));
            MagiskState = "Magisk lab: prepared";
            await ApprovedMagiskAction(device.Name, prepared, "install");
        });
    }

    private async Task RecoverMagiskAsync()
    {
        if (SelectedDevice is not { } device) return;
        await RunBusyAsync("Checking stock recovery...", async () =>
        {
            var state = await RequestAsync("magisk.status", new JsonObject { ["name"] = device.Name }, TimeSpan.FromSeconds(30));
            if (state["transaction"] is null) { MagiskState = "Magisk lab: not configured"; return; }
            if (!AskConsent(RecoverStockConsent(device))) return;
            await ApprovedMagiskAction(device.Name, state, "recover");
        });
    }

    /// <summary>
    /// UX-09: one shared consent surface for the consequential root actions. It
    /// returns true only when the operator pressed the dialog's confirm; every
    /// other exit (Cancel, Esc, the scrim, the window close box) returns false, and
    /// the caller then returns before its first supervisor request.
    /// </summary>
    private bool AskConsent(ConsentDialogModel model)
    {
        var dialog = new ConsentDialogWindow(model) { Owner = Application.Current?.MainWindow };
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// igr-feedback-ux10 item 6: "emulator-5554 · Android 15 · AOSP · x86_64" -
    /// the reading name of the real image (the same table the inspector uses),
    /// never the raw package id. The full raw value stays in the tooltip.
    /// </summary>
    private static string GuestMetaText(DeviceItemViewModel device)
    {
        var image = ImageNaming.Describe(device.ImagePackage);
        return string.Join(" · ", new[] { device.Serial, image.Length > 0 ? image : device.Abi }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>The tooltip: the same target with the raw package the supervisor stores.</summary>
    private static string GuestMetaTooltip(DeviceItemViewModel device) =>
        string.Join(" · ", new[] { device.Serial, device.Abi, device.ImagePackage }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// UX-18: the consent dialog's state chip takes its tone from the one
    /// lifecycle, so a guest that is up (or coming up, or failed) reads as
    /// "warn" and a guest that is safely stopped reads as "neutral".
    /// </summary>
    private static string GuestStateTone(DeviceItemViewModel device) =>
        device.Lifecycle.Phase is DevicePhase.Running or DevicePhase.Starting
            or DevicePhase.Stopping or DevicePhase.Failed or DevicePhase.EngineGone
            ? "warn"
            : "neutral";

    private static ConsentDialogModel InstallMagiskConsent(
        DeviceItemViewModel device, IReadOnlyList<ConsentArtifact> artifacts) => new()
    {
        Title = "Install Magisk lab on this guest?",
        Subtitle = "This patches the boot image. Check the exact target, artifacts and consequences first.",
        TileTone = "err",
        GuestName = device.Name,
        GuestMeta = GuestMetaText(device),
        GuestMetaTooltip = GuestMetaTooltip(device),
        StateLabel = device.StateLabel,
        StateTone = GuestStateTone(device),
        Artifacts = artifacts,
        Consequences = new[]
        {
            new ConsentConsequence("warn", "ADB is rooted temporarily during the install, then restored to UID 2000 (shell)."),
            new ConsentConsequence("warn", "Any host shell connected to this guest gets root access while Magisk is installed."),
            new ConsentConsequence("ok", "A recovery point and a snapshot fence are taken first, so stock can be recovered even if boot fails."),
            new ConsentConsequence("ok", "Zygisk stays off and no modules are installed."),
            new ConsentConsequence("err", "Backups made after this point will contain a modified boot image."),
        },
        AcknowledgeText = "I understand this guest's boot image will be modified. This consent does not replace the supervisor's own authorization.",
        ConfirmWord = device.Name,
        ConfirmLabel = "Install Magisk lab",
    };

    private static ConsentDialogModel AdbRootConsent(DeviceItemViewModel device) => new()
    {
        Title = "Root this guest's ADB daemon?",
        Subtitle = "A privileged provisioning step for this guest only. Read what changes before it runs.",
        TileTone = "warn",
        GuestName = device.Name,
        GuestMeta = GuestMetaText(device),
        GuestMetaTooltip = GuestMetaTooltip(device),
        StateLabel = device.StateLabel,
        StateTone = GuestStateTone(device),
        Consequences = new[]
        {
            new ConsentConsequence("warn", "The daemon restarts as uid 0; any process that can reach this guest's ADB gets root while it runs."),
            new ConsentConsequence("ok", "ADB unroot returns the daemon to the shell user."),
            new ConsentConsequence("ok", "This applies to this guest only; other guests keep their own daemon."),
            new ConsentConsequence("info", "The Magisk lab state and agent sessions are not changed by this step."),
        },
        AcknowledgeText = "I understand this guest's ADB daemon will run as root until I unroot it.",
        ConfirmLabel = "Root the daemon",
    };

    private static ConsentDialogModel RecoverStockConsent(DeviceItemViewModel device) => new()
    {
        Title = "Restore the stock boot image?",
        Subtitle = "This returns the guest to its pre-install boot image and fences the lab.",
        TileTone = "accent",
        GuestName = device.Name,
        GuestMeta = GuestMetaText(device),
        GuestMetaTooltip = GuestMetaTooltip(device),
        StateLabel = device.StateLabel,
        StateTone = GuestStateTone(device),
        Consequences = new[]
        {
            new ConsentConsequence("ok", "The supervisor can stop an unbootable guest from Windows."),
            new ConsentConsequence("warn", "Current guest data is preserved when possible; otherwise the verified pre-install data backup is restored."),
            new ConsentConsequence("ok", "Failed overlays remain available as evidence."),
            new ConsentConsequence("info", "The Magisk lab state returns to not configured for this guest."),
        },
        AcknowledgeText = "I understand this guest will boot from the stock boot image and its data may fall back to the verified backup.",
        ConfirmWord = device.Name,
        ConfirmLabel = "Restore stock",
    };

    private async Task ApprovedMagiskAction(string name, JsonObject state, string action)
    {
        var approval = await RequestAsync("magisk.approve", new JsonObject
        {
            ["name"] = name, ["transaction"] = state["transaction"]!.DeepClone(), ["action"] = action,
        }, TimeSpan.FromSeconds(30));
        var result = await RequestAsync("magisk." + action, new JsonObject
        {
            ["name"] = name, ["transaction"] = state["transaction"]!.DeepClone(),
            ["approval"] = approval["approval"]!.DeepClone(),
        }, TimeSpan.FromMinutes(60));
        MagiskState = "Magisk lab: " + result["phase"];
        StatusMessage = MagiskState;
        await RefreshDevicesAsync();
    }

    private async Task<JsonObject> RequestAsync(string op, JsonObject? args, TimeSpan deadline)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("The supervisor is not connected.");
        }

        try
        {
            var response = await _connection.RequestAsync(op, args, deadline).ConfigureAwait(true);
            if (!response.Ok)
            {
                throw new SupervisorRequestException(response.Code, response.Message);
            }

            return response.Data as JsonObject ?? new JsonObject();
        }
        catch (SupervisorUnavailableException ex)
        {
            // The pipe is gone. Drop the client so Refresh can reconnect instead
            // of reusing a dead stream forever.
            var dead = _connection;
            _connection = null;
            IsConnected = false;
            // UX-08: a dead pipe also ends the log subscription, so the drawer
            // stops saying "Following" when nothing is streaming any more.
            if (Volatile.Read(ref _logcatSubscription) is not null)
            {
                Volatile.Write(ref _logcatSubscription, null);
                NotifyLogFilterState();
            }

            if (dead is not null)
            {
                _ = dead.DisposeAsync();
            }

            // UX-16 item C: a dead pipe starts one bounded recovery run.
            BeginSupervisorRecovery($"request {op}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// UX-18: a guest's lifecycle changed, so every control that is enabled
    /// from it has to be re-evaluated. Without this the Studio kept offering a
    /// "Start" for a guest that was already running, and kept accepting input
    /// for a guest whose engine had gone - the command states were only
    /// refreshed when the selection or the busy flag changed.
    /// </summary>
    private void OnLifecycleChanged(DeviceItemViewModel device, DeviceLifecycle before)
    {
        if (device.Lifecycle == before)
        {
            return;
        }

        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionEnabled));
        OnPropertyChanged(nameof(CanSendInput));
        OnPropertyChanged(nameof(IsGuestUp));
        NotifyLogFilterState();
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        PrimaryActionCommand.RaiseCanExecuteChanged();
        CreateDeviceCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        ColdBootCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        HomeCommand.RaiseCanExecuteChanged();
        RecentsCommand.RaiseCanExecuteChanged();
        ScreenshotCommand.RaiseCanExecuteChanged();
        InstallApkCommand.RaiseCanExecuteChanged();
        BuildApkSetCommand.RaiseCanExecuteChanged();
        LaunchSampleCommand.RaiseCanExecuteChanged();
        RotateCommand.RaiseCanExecuteChanged();
        AdbRootCommand.RaiseCanExecuteChanged();
        AdbUnrootCommand.RaiseCanExecuteChanged();
        RefreshAdbStateCommand.RaiseCanExecuteChanged();
        MagiskInstallCommand.RaiseCanExecuteChanged();
        MagiskRecoverCommand.RaiseCanExecuteChanged();
    }

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        var index = Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    public async ValueTask DisposeAsync()
    {
        _statusTimer.Stop();
        _logTimer.Stop();
        _lifetime.Cancel();
        await UnsubscribeFramesAsync().ConfigureAwait(false);
        await StopLogcatAsync().ConfigureAwait(false);
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record PendingInput(DateTimeOffset DispatchedUtc, int? BaselineColor);

    public sealed record LatencyProbeSample(double FrameAvailableMs, double CompositionMs)
    {
        public double TapAcknowledgedMs { get; init; }
        public double? ControllerSetupMs { get; init; }
        public double? ControllerTapMs { get; init; }
        public double? SupervisorFrameWriteMs { get; init; }
        public double? ChangedFrameReceivedMs { get; init; }
        public double? FrameDecodeAndDispatchMs { get; init; }
    }

    private sealed record ProbeState(string Device, int Row, int Width, int Height, int ExpectedColor)
    {
        public TaskCompletionSource<LatencyProbeSample> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public double? FrameAvailableMs { get; set; }
        public double? SupervisorFrameWriteMs { get; set; }
        public DateTimeOffset DispatchUtc { get; set; }
        public double? ChangedFrameReceivedMs { get; set; }
        public double? FrameDecodeAndDispatchMs { get; set; }
    }

    private sealed record PendingFrame(string Subscription, string Path, string Format, int Width, int Height, int Stride,
        int GuestWidth, int GuestHeight, int Sequence, int Dropped, DateTimeOffset? ReceivedUtc, DateTimeOffset? WrittenUtc,
        long EventTimestamp);
}

public sealed class SupervisorRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// UX-03: one evidence-backed boot step. <see cref="State"/> drives colour only;
/// <see cref="Status"/> always prints the state as a word ("done", "active",
/// "unknown", "pending") so colour is never the only signal.
/// </summary>
public sealed class BootStageViewModel(string label) : ObservableObject
{
    private string _state = "pending";
    private string _status = "not started";

    public string Label { get; } = label;

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public void Set(string state, string status)
    {
        State = state;
        Status = status;
    }
}

public sealed class DeviceItemViewModel : ObservableObject
{
    public required string Name { get; init; }

    public required string Serial { get; init; }

    public string ImagePackage { get; init; } = "";

    /// <summary>
    /// igr-feedback-ux05 item 5: the inspector's Image row prints a reading name
    /// built from the REAL system-image package ("Android 15 · Google APIs ·
    /// x86_64") and keeps the raw package in the tooltip. The API level maps to
    /// an Android version through the same table the library uses (an unknown
    /// level falls back to "API n"), and a tag the table does not name falls back
    /// to its raw text. Nothing here is a hardcoded sample value.
    /// </summary>
    public string ImageDisplay
    {
        get
        {
            var package = (ImagePackage ?? "").Trim();
            return package.Length == 0 ? "—" : ImageNaming.Describe(package);
        }
    }

    public int ConsolePort { get; init; }

    public int GrpcPort { get; init; }

    public string Gpu { get; init; } = "";

    private string _state = "Defined";

    /// <summary>
    /// UX-18: the evidence that came with the last status read. The record's
    /// word alone cannot tell a booting guest from a running one, and it cannot
    /// tell a live guest from a dead engine, so the lifecycle is built from
    /// both.
    /// </summary>
    private bool? _engineAlive;

    private bool _enginePidKnown;

    private bool _bootCompleted;

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    /// <summary>
    /// UX-18: the one lifecycle every surface reads. The Studio, the device
    /// library and the command palette all resolve the same guest through it,
    /// so a control is enabled exactly when the supervisor would accept the
    /// action.
    /// </summary>
    public DeviceLifecycle Lifecycle =>
        DeviceLifecycle.From(_state, _engineAlive, _enginePidKnown, _bootCompleted);

    /// <summary>
    /// UX-07 nit 1: the user-facing state word. The supervisor's "Defined" record
    /// state is internal vocabulary, so every surface prints the neutral "Stopped"
    /// instead; the enum name stays on <see cref="State"/> for the logic and the
    /// converters.
    /// </summary>
    public string StateLabel => Lifecycle.Label;

    /// <summary>
    /// UX-18: the guest is up and Android reported boot completion. A guest
    /// that is still booting is not running, and a guest whose engine is gone is
    /// not running either.
    /// </summary>
    public bool IsRunning => Lifecycle.IsRunning;

    private string _foreground = "";

    public string Foreground
    {
        get => _foreground;
        private set
        {
            if (SetProperty(ref _foreground, value))
            {
                OnPropertyChanged(nameof(ForegroundDisplay));
            }
        }
    }

    /// <summary>
    /// UX-05 fix pass (igr-feedback-ux04b item 6a): the supervisor reports
    /// dumpsys' raw current-focus window ("Window{82fc746 u0 com.example/.Main}").
    /// The inspector prints the PACKAGE ONLY ("com.android.launcher3") and keeps
    /// the full raw string in the tooltip, so the row reads as identity rather
    /// than a token dump.
    /// </summary>
    public string ForegroundDisplay
    {
        get
        {
            var raw = (Foreground ?? "").Trim();
            if (raw.Length == 0)
            {
                return "—";
            }

            var start = raw.IndexOf("Window{", StringComparison.Ordinal);
            if (start < 0)
            {
                return PackageOnly(raw);
            }

            var body = raw[(start + "Window{".Length)..];
            var end = body.IndexOf('}');
            if (end >= 0)
            {
                body = body[..end];
            }

            // Drop the "<hash> u<userId> " prefix, keep what is left.
            var space = 0;
            for (var tokens = 0; tokens < 2 && space >= 0; tokens++)
            {
                space = body.IndexOf(' ', space + 1);
            }

            var name = space > 0 ? body[(space + 1)..].Trim() : body.Trim();
            return name.Length == 0 ? raw : PackageOnly(name);
        }
    }

    /// <summary>"package/activity" and "package" both reduce to the package.</summary>
    private static string PackageOnly(string name)
    {
        var slash = name.IndexOf('/');
        return slash > 0 ? name[..slash] : name;
    }

    private string _abi = "";

    public string Abi
    {
        get => _abi;
        private set
        {
            if (SetProperty(ref _abi, value))
            {
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(AbiApi));
            }
        }
    }

    private string _sdkLevel = "";

    public string SdkLevel
    {
        get => _sdkLevel;
        private set
        {
            if (SetProperty(ref _sdkLevel, value))
            {
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(AbiApi));
                OnPropertyChanged(nameof(ApiLabel));
            }
        }
    }

    /// <summary>
    /// UX-12: the title-bar crumb's second half ("API 35"). Built from the
    /// reported SDK level only; empty until the supervisor reports one, so the
    /// crumb never prints a stale or invented API number.
    /// </summary>
    public string ApiLabel => string.IsNullOrWhiteSpace(SdkLevel) ? "" : $"API {SdkLevel}";

    /// <summary>
    /// UX-04b: "x86_64 · 35" for the inspector's ABI / API row. Built from the
    /// reported fields and notified whenever a status tick fills them, so the row
    /// never shows a stale pair or a bare separator.
    /// </summary>
    public string AbiApi
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Abi))
            {
                return string.IsNullOrWhiteSpace(SdkLevel) ? "" : SdkLevel;
            }

            return string.IsNullOrWhiteSpace(SdkLevel) ? Abi : $"{Abi} · {SdkLevel}";
        }
    }

    /// <summary>
    /// UX-04b: the identity line under the device name — "API 35 · x86_64 ·
    /// emulator-5554" — built only from fields the supervisor reported. Empty
    /// parts are dropped rather than printed as blanks or placeholders.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(SdkLevel))
            {
                parts.Add($"API {SdkLevel}");
            }

            if (!string.IsNullOrWhiteSpace(Abi))
            {
                parts.Add(Abi);
            }

            if (!string.IsNullOrWhiteSpace(Serial))
            {
                parts.Add(Serial);
            }

            return string.Join(" · ", parts);
        }
    }

    private string _lastError = "";

    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public JsonObject? Readiness { get; set; }

    public int Rotation { get; set; }

    public static DeviceItemViewModel From(JsonObject node)
    {
        var device = new DeviceItemViewModel
        {
            Name = node["name"]!.GetValue<string>(),
            Serial = node["serial"]!.GetValue<string>(),
            ImagePackage = node["imagePackage"]?.GetValue<string>() ?? "",
            ConsolePort = node["consolePort"]?.GetValue<int>() ?? 0,
            GrpcPort = node["grpcPort"]?.GetValue<int>() ?? 0,
            Gpu = node["gpu"]?.GetValue<string>() ?? "",
            State = node["state"]?.GetValue<string>() ?? "Unknown",
        };

        // The list carries the record's engine pid: a record that points at a
        // process is a guest that was started, which the lifecycle needs to
        // tell "never started" from "the process is gone".
        device._enginePidKnown = node["enginePid"]?.GetValue<int>() is > 0;
        return device;
    }

    public void ApplyStatus(JsonObject status)
    {
        var before = Lifecycle;

        // UX-18: the evidence comes first, so the lifecycle is built once from
        // the record's word and what the supervisor observed about it.
        _engineAlive = status["engineAlive"]?.GetValue<bool>();
        _enginePidKnown = status["pid"]?.GetValue<int>() is > 0;
        _bootCompleted = status["bootCompleted"]?.GetValue<string>()?.Trim() == "1";
        Foreground = status["foreground"]?.GetValue<string>() ?? "";
        Abi = status["abi"]?.GetValue<string>() ?? "";
        SdkLevel = status["sdkLevel"]?.GetValue<string>() ?? "";
        LastError = status["lastError"]?.GetValue<string>() ?? "";
        if (status["readiness"] is JsonObject readiness)
        {
            Readiness = readiness;
        }

        State = status["state"]?.GetValue<string>() ?? State;

        if (Lifecycle != before)
        {
            OnPropertyChanged(nameof(Lifecycle));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StateLabel));
        }
    }
}

/// <summary>One command palette entry.</summary>
public sealed class PaletteEntryViewModel
{
    private readonly Func<string>? _reason;

    /// <param name="label">The command name the operator reads.</param>
    /// <param name="hint">The one-line description under the name.</param>
    /// <param name="command">The same command the toolbar and the rail submit.</param>
    /// <param name="group">The palette section header (see <see cref="PaletteGroups"/>).</param>
    /// <param name="iconKey">
    /// Resource key of the 16-grid line icon the row prints. A key that does not
    /// resolve draws nothing rather than a placeholder glyph.
    /// </param>
    /// <param name="reason">
    /// UX-07: why the command cannot run, in the operator's words ("Already
    /// running", "No device selected"). Read only while the command's own
    /// predicate is false, so an enabled row never claims a limitation.
    /// </param>
    public PaletteEntryViewModel(
        string label,
        string hint,
        System.Windows.Input.ICommand command,
        string group,
        string iconKey,
        Func<string>? reason = null)
    {
        Label = label;
        Hint = hint;
        Command = command;
        Group = group;
        IconKey = iconKey;
        _reason = reason;
    }

    public string Label { get; }

    public string Hint { get; }

    public string Group { get; }

    public string IconKey { get; }

    public System.Windows.Input.ICommand Command { get; }

    /// <summary>The reason chip for a disabled row; empty while the row can run.</summary>
    public string Reason { get; private set; } = "";

    /// <summary>The command's own predicate, re-read when the palette filters.</summary>
    public bool CanRun { get; private set; } = true;

    /// <summary>
    /// Characters the fuzzy query matched in the title and the subtitle, so the
    /// row can paint them in the accent colour. Empty with no query.
    /// </summary>
    public IReadOnlyList<int> LabelHits { get; internal set; } = Array.Empty<int>();

    /// <inheritdoc cref="LabelHits" />
    public IReadOnlyList<int> HintHits { get; internal set; } = Array.Empty<int>();

    /// <summary>
    /// Re-reads the command's predicate and, when it is false, the reason. The
    /// palette calls this every time it rebuilds its list, so an open palette
    /// never shows a stale enabled state or a stale reason.
    /// </summary>
    public void Refresh()
    {
        CanRun = Command.CanExecute(null);
        Reason = CanRun ? "" : _reason?.Invoke() ?? "";
    }

    public void Invoke()
    {
        if (Command.CanExecute(null))
        {
            Command.Execute(null);
        }
    }
}

public sealed class LogEntryViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<int> _highlightIndexes = Array.Empty<int>();
    private IReadOnlyList<int> _tagHighlightIndexes = Array.Empty<int>();

    public required string Time { get; init; }

    public required string Level { get; init; }

    public required string Tag { get; init; }

    public required string Message { get; init; }

    public required string Raw { get; init; }

    /// <summary>
    /// UX-08 page: the characters of <see cref="Message"/> the search matches.
    /// Recomputed when the query or the line changes, so the page's
    /// HighlightTextBlock paints exactly what the filter kept.
    /// </summary>
    public IReadOnlyList<int> HighlightIndexes
    {
        get => _highlightIndexes;
        private set
        {
            if (!_highlightIndexes.SequenceEqual(value))
            {
                _highlightIndexes = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightIndexes)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The same offsets inside <see cref="Tag"/>, for the page's tag cell.</summary>
    public IReadOnlyList<int> TagHighlightIndexes
    {
        get => _tagHighlightIndexes;
        private set
        {
            if (!_tagHighlightIndexes.SequenceEqual(value))
            {
                _tagHighlightIndexes = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagHighlightIndexes)));
            }
        }
    }

    public void ApplyHighlight(string? query)
    {
        HighlightIndexes = LogcatPresentation.IndexesOf(Message, query);
        TagHighlightIndexes = LogcatPresentation.IndexesOf(Tag, query);
    }

    /// <summary>
    /// The row's accessible name: the same three facts a sighted operator reads,
    /// so a screen reader announces the line instead of the view-model type name.
    /// </summary>
    public string AccessibleLine => string.IsNullOrEmpty(Tag)
        ? $"{Time} {Level} {Message}"
        : $"{Time} {Level} {Tag}: {Message}";

    public static LogEntryViewModel From(JsonObject node)
    {
        var received = node["receivedUtc"]?.GetValue<string>();
        var time = DateTimeOffset.TryParse(received, out var parsed) ? parsed.ToLocalTime().ToString("HH:mm:ss.fff") : "";
        return new LogEntryViewModel
        {
            Time = time,
            Level = node["level"]?.GetValue<string>() ?? "I",
            Tag = node["tag"]?.GetValue<string>() ?? "",
            Message = node["message"]?.GetValue<string>() ?? "",
            Raw = node["raw"]?.GetValue<string>() ?? "",
        };
    }
}
