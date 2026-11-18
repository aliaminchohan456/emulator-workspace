using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Frames;
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
    // EV-10: the frame path keeps one surface and one waiting frame. The slot
    // replaces (and counts) the older frame, so presentation latency has one
    // decode's bound instead of a growing queue's.
    private readonly FrameSlot<PendingFrame> _frameSlot = new();
    // EV-10b: the background task owns read + conversion; this one-deep mailbox
    // hands the newest staged frame to the render tick. A frame held after a
    // failed TryLock goes back here and is retried, without being a drop.
    private readonly FrameStageSlot<StagedFrame> _stageSlot = new();
    private readonly ViewportSurfaceCache _surfaceCache = new();
    // EV-11: one reader per frame subscription, opened when the stream goes
    // live and disposed when it ends (unsubscribe, guest switch, lost pipe).
    private readonly object _mapGate = new();
    private FrameRingReader? _frameMap;
    /// <summary>
    /// EV-11: EW_FRAME_DELIVERY=file pins the pre-change path (a file per
    /// frame, read back by the window) for the before/after measurement; the
    /// default reads the shared-memory ring and falls back to files by itself.
    /// </summary>
    private bool _frameFileFallback = Environment.GetEnvironmentVariable("EW_FRAME_DELIVERY") is "file";
    private bool _mapFailureLogged;
    private readonly bool _legacyViewport = ViewportPathSelection.LegacyEnabled(
        Environment.GetEnvironmentVariable("EW_VIEWPORT_LEGACY"),
        ViewportPathSelection.LegacyByDefault);
    private readonly ViewportStageMode _stageMode = ViewportPixelPolicy.ParseMode(
        Environment.GetEnvironmentVariable("EW_VIEWPORT_FORMAT"));
    private readonly bool _writePixelsCopy =
        Environment.GetEnvironmentVariable("EW_VIEWPORT_COPY") is "write";
    private bool _renderingHooked;

    // EV-10b: opt-in per-frame timing split (queue, file read, staging,
    // TryLock, row copy, AddDirtyRect+Unlock, unlock to next render tick). One
    // line per 20 frames, so a measurement run can say where the hot path's
    // time goes without flooding the log.
    private readonly bool _viewportTrace =
        Environment.GetEnvironmentVariable("EW_VIEWPORT_TRACE") is "1";
    private int _viewportTraceCounter;
    private int _lockBusy;
    private int _frameRingTorn;
    private int _frameRingSkipped;
    private long _renderTicks;
    private long _traceTicksAtLastLine;
    private PendingPresentTrace? _pendingTrace;

    /// <summary>
    /// EV-10b: raised on the UI thread after a frame is staged. The window
    /// invalidates the viewport so WPF schedules a composition; the frame is
    /// still presented only from <see cref="OnFrameRendering"/>. Without this
    /// WPF stops raising Rendering on an otherwise idle window, and a staged
    /// frame waits for an unrelated repaint.
    /// </summary>
    public event Action? FrameStaged;

    // The Shared pool tops out at 1 MiB, and a 540x1200 RGB24 frame is 1.86 MiB,
    // so a dedicated pool is what actually reuses the buffer instead of
    // allocating a new LOH array per frame. 4 MiB covers the decoded 540x1200
    // BGRA fallback (2.59 MiB) with headroom.
    private static readonly ArrayPool<byte> FrameBuffers =
        ArrayPool<byte>.Create(4 * 1024 * 1024, maxArraysPerBucket: 8);
    private readonly SemaphoreSlim _subscribeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private SupervisorConnection? _connection;

    // UX-16 item C: the bounded recovery rule for a supervisor that went away.
    // The view model owns the start/connect/log; the rule owns the budget.
    private readonly SupervisorWatchdog _watchdog = new();
    private int _frameSequence;

    // EV-06: the connect is single-flight. The slot is claimed before any work
    // runs, so a second Refresh, a watchdog attempt or a re-entrant call joins
    // the task already in flight instead of opening a second pipe.
    private readonly object _initializeGate = new();
    private Task? _initializeTask;
    private bool _isConnecting;

    // EV-05: each stream has one owner and one transition function. The phase,
    // the guest its id belongs to and the id itself are one value, changed only
    // through StreamSubscription.Next - so a late grant, a stale close and a
    // second request cannot leave a field behind.
    private StreamSubscription _frameSub = StreamSubscription.None;
    private StreamSubscription _logSub = StreamSubscription.None;
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
    // EV-04: the lab's one reading. The phase word, the two supervisor flags
    // and the journal are classified once, here, so the inspector row, the rail
    // dot, the stage and both lab commands cannot disagree.
    private LabUiState _lab = LabUiState.NotChecked;
    private bool _statusPollInFlight;
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

    // EV-12: the capture size the current (or next) subscription asks for, in
    // device pixels. The window derives it from the viewport and calls
    // ApplyCaptureSizeAsync after a resize burst; a subscription uses it so a
    // late attach starts at the right size instead of the old hard-coded 540.
    private int _captureWidth;
    private int _captureHeight;

    public MainViewModel(bool halfCapture = false)
    {
        _captureWidth = halfCapture ? 270 : 540;
        _captureHeight = halfCapture ? 600 : 1200;
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
        RestartCommand = new AsyncRelayCommand(
            RestartAsync,
            () => SelectedDevice?.Lifecycle.CanRestart == true && !IsBusy);
        ColdBootCommand = new AsyncRelayCommand(
            ColdBootAsync,
            () => SelectedDevice?.Lifecycle.CanColdBoot == true && !IsBusy);
        BackCommand = new AsyncRelayCommand(() => KeyAsync("BACK"), () => CanSendInput);
        HomeCommand = new AsyncRelayCommand(() => KeyAsync("HOME"), () => CanSendInput);
        RecentsCommand = new AsyncRelayCommand(() => KeyAsync("RECENTS"), () => CanSendInput);
        ScreenshotCommand = new AsyncRelayCommand(
            ScreenshotAsync,
            () => SelectedDevice?.Lifecycle.CanScreenshot == true && !IsBusy);
        InstallApkCommand = new AsyncRelayCommand(
            InstallApkDialogAsync,
            () => SelectedDevice?.Lifecycle.CanInstall == true && !IsBusy);
        BuildApkSetCommand = new AsyncRelayCommand(BuildApkSetDialogAsync, () => IsConnected && !IsBusy);
        LaunchSampleCommand = new AsyncRelayCommand(
            () => LaunchAsync("dev.emulatorworkspace.sample"),
            () => SelectedDevice?.Lifecycle.CanInstall == true && !IsBusy);
        RotateCommand = new AsyncRelayCommand(ToggleRotationAsync, () => CanSendInput);
        RefreshCommand = new AsyncRelayCommand(InitializeAsync, () => !IsConnecting && !_watchdog.IsRunning);
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
        AdbRootCommand = new AsyncRelayCommand(
            () => SetAdbRootAsync(true),
            () => SelectedDevice?.Lifecycle.CanChangeAdbRoot == true && !IsBusy);
        AdbUnrootCommand = new AsyncRelayCommand(
            () => SetAdbRootAsync(false),
            () => SelectedDevice?.Lifecycle.CanChangeAdbRoot == true && !IsBusy);
        RefreshAdbStateCommand = new AsyncRelayCommand(
            RefreshAdbStateAsync,
            () => SelectedDevice?.Lifecycle.CanChangeAdbRoot == true && !IsBusy);
        // EV-04: the lab's own reading gates its two actions. A journal on disk
        // makes magisk.prepare refuse, so install is offered only when there is
        // none; recovery is offered only when it is due or the guest is no
        // longer stock, and never while an install is being driven.
        MagiskInstallCommand = new AsyncRelayCommand(
            InstallMagiskAsync,
            () => SelectedDevice is not null && !IsBusy && _lab.CanInstall);
        MagiskRecoverCommand = new AsyncRelayCommand(
            RecoverMagiskAsync,
            () => SelectedDevice is not null && !IsBusy && _lab.CanRecover);

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
            () => PaletteDisabledReason(DeviceAction.Start)));
        Palette.Add(new PaletteEntryViewModel(
            "Stop device", "Stop the selected guest", StopCommand,
            PaletteGroups.Device, "IconStop",
            () => PaletteDisabledReason(DeviceAction.Stop)));
        Palette.Add(new PaletteEntryViewModel(
            "Restart device", "Restart the selected guest", RestartCommand,
            PaletteGroups.Device, "IconRestart",
            () => PaletteDisabledReason(DeviceAction.Restart)));
        Palette.Add(new PaletteEntryViewModel(
            "Cold boot", "Cold boot the selected guest", ColdBootCommand,
            PaletteGroups.Device, "IconBolt",
            () => PaletteDisabledReason(DeviceAction.ColdBoot)));
        Palette.Add(new PaletteEntryViewModel(
            "Install APK", "Install an APK on the selected guest", InstallApkCommand,
            PaletteGroups.AppsAndCapture, "IconPackage",
            () => PaletteDisabledReason(DeviceAction.Screenshot)));
        Palette.Add(new PaletteEntryViewModel(
            "Build APK set", "Convert an Android App Bundle using test signing", BuildApkSetCommand,
            PaletteGroups.AppsAndCapture, "IconStack",
            () => IsConnected ? "Busy" : "Supervisor not connected"));
        Palette.Add(new PaletteEntryViewModel(
            "Launch sample", "Launch the sample application", LaunchSampleCommand,
            PaletteGroups.AppsAndCapture, "IconLaunch",
            () => PaletteDisabledReason(DeviceAction.Screenshot)));
        Palette.Add(new PaletteEntryViewModel(
            "Screenshot", "Capture a PNG of the selected guest", ScreenshotCommand,
            PaletteGroups.AppsAndCapture, "IconScreenshot",
            () => PaletteDisabledReason(DeviceAction.Screenshot)));
        Palette.Add(new PaletteEntryViewModel(
            "Toggle Logcat", "Show or hide the Logcat drawer", ToggleLogcatCommand,
            PaletteGroups.Diagnostics, "IconLogs"));
        Palette.Add(new PaletteEntryViewModel(
            "Refresh ADB state", "Re-read the ADB daemon identity", RefreshAdbStateCommand,
            PaletteGroups.Diagnostics, "IconRefresh",
            () => PaletteDisabledReason(DeviceAction.RefreshAdbState)));
        Palette.Add(new PaletteEntryViewModel(
            "Export diagnostics", "Write the diagnostic bundle", CopyDiagnosticsCommand,
            PaletteGroups.Diagnostics, "IconDownload"));
        Palette.Add(new PaletteEntryViewModel(
            "ADB root", "Run the guest ADB daemon as root", AdbRootCommand,
            PaletteGroups.Privileged, "IconKey",
            () => PaletteDisabledReason(DeviceAction.AdbRoot)));
        Palette.Add(new PaletteEntryViewModel(
            "ADB unroot", "Return the guest ADB daemon to the shell user", AdbUnrootCommand,
            PaletteGroups.Privileged, "IconUndo",
            () => PaletteDisabledReason(DeviceAction.AdbUnroot)));

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

    /// <summary>
    /// EV-04: true while the stream is stalled. It is derived from the one frame
    /// state, so the chip, the input gate and this flag cannot disagree.
    /// </summary>
    public bool IsFrameStale => FrameStreamHealth.IsStalled(_frameState);

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

            if (!IsFrameStale)
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
    /// <remarks>
    /// EV-06: the status bar reads the same value, so it is gated by the same
    /// session rule as the inspector rows. A stopped guest shows the dash, not
    /// the rate the previous run ended on.
    /// </remarks>
    public string TelemetryFpsValue =>
        Frame is null || !InspectorSessionRows.IsReported(SelectedPhase)
            ? "—"
            : FramesPerSecond.ToString("N0");

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
    public bool HasLogStream => Volatile.Read(ref _logSub).Phase == SubPhase.Live;

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

    // ---- EV-06: one page ------------------------------------------------
    //
    // UX-08's full Logcat page and UX-13's Agents page used to be two independent
    // flags, so the shell could say "Logcat" and "Agents" at the same time and
    // the Studio was hidden by both. One value now answers every question, and
    // the UI still binds to the same computed names.

    private ShellPage _currentPage = ShellPage.Studio;

    public ShellPage CurrentPage
    {
        get => _currentPage;
        set
        {
            var previous = _currentPage;
            if (previous == value)
            {
                return;
            }

            _currentPage = value;
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(IsLogcatPage));
            OnPropertyChanged(nameof(IsAgentsPage));
            OnPropertyChanged(nameof(IsStudioVisible));
            OnPropertyChanged(nameof(LogcatRailActive));
            OnPropertyChanged(nameof(AgentsRailActive));

            // Only a Logcat page entering or leaving changes whether a log
            // surface is visible; Studio <-> Agents leaves the dock untouched.
            if (ShellPageRules.TouchesLogcat(previous, value))
            {
                UpdateLogcatAttachment();
                NotifyLogFilterState();
            }
        }
    }

    /// <summary>The full Logcat page is the current page.</summary>
    public bool IsLogcatPage => ShellPageRules.ShowsLogcat(CurrentPage);

    // ---- UX-13 Agents page --------------------------------------------------

    /// <summary>The Agents page is the current page.</summary>
    public bool IsAgentsPage => ShellPageRules.ShowsAgents(CurrentPage);

    /// <summary>The Studio exercises are shown whenever no full page covers them.</summary>
    public bool IsStudioVisible => ShellPageRules.ShowsStudio(CurrentPage);

    /// <summary>The rail's Logcat destination is active for the dock or the page.</summary>
    public bool LogcatRailActive => ShellPageRules.LogcatRailActive(CurrentPage, IsLogcatOpen);

    /// <summary>The rail's Agents destination is active only while its page is.</summary>
    public bool AgentsRailActive => ShellPageRules.AgentsRailActive(CurrentPage);

    /// <summary>
    /// Rail / Ctrl+4: show the Agents page. Leaving the Logcat page detaches the
    /// stream only when the dock is closed too, exactly like its own close.
    /// </summary>
    public void OpenAgentsPage() => CurrentPage = ShellPage.Agents;

    private void OpenLogcatPage()
    {
        // Page first, then close the dock: the attachment decision below then sees
        // one open log surface and never unsubscribes between the two.
        CurrentPage = ShellPage.Logcat;
        if (IsLogcatOpen)
        {
            IsLogcatOpen = false;
        }

        _ = EnsureLogcatAsync();
    }

    private void CloseLogcatPage()
    {
        if (IsLogcatPage)
        {
            CurrentPage = ShellPage.Studio;
        }

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
    /// EV-04: the Session row and the Root Lab chip print the classified label
    /// ("Not configured", "Install in progress", "Stock recovery required").
    /// The raw phase word lives in <see cref="MagiskLabTooltip"/> only, which
    /// fixes filed issue 1.
    /// </summary>
    public string MagiskLabValue => _lab.Label;

    public string MagiskLabTooltip => _lab.Tooltip;

    /// <summary>
    /// UX-04b: the rail's Root Lab dot is painted only from a lab state the
    /// supervisor actually reported — ok when the lab is prepared, warn when
    /// stock recovery is due or an install runs, nothing while the state is
    /// unchecked or absent.
    /// </summary>
    public string LabDotTone => _lab.Phase switch
    {
        LabUiPhase.RecoveryRequired => "warn",
        LabUiPhase.Installing => "warn",
        LabUiPhase.Prepared => "ok",
        _ => "",
    };

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
                    NotifySessionRows();
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

    /// <summary>EV-12: the capture width the stream is asked for, in device pixels.</summary>
    public int CaptureWidth => Volatile.Read(ref _captureWidth);

    /// <summary>EV-12: the capture height the stream is asked for, in device pixels.</summary>
    public int CaptureHeight => Volatile.Read(ref _captureHeight);

    internal string? ViewportSubscription => Volatile.Read(ref _frameSub).Id;

    public string FrameInfo => Frame is null
        ? "no frame"
        : $"{FrameWidth}x{FrameHeight}  -  {FramesPerSecond:N1} fps  -  {DroppedFrames} dropped";

    // UX-04b: the inspector's RESOURCES rows need the same live numbers as one
    // value each (tabular mono, fixed width). They are raised on the same 250 ms
    // cadence as FrameInfo, so nothing here runs its own timer and no value is
    // shown before the supervisor has delivered a frame.
    //
    // EV-06: they are session rows, so they are also gated by the lifecycle. A
    // guest that is not Running or Starting reports nothing, even when a stale
    // frame or a readiness stamp from the previous run is still in memory.
    private DevicePhase? SelectedPhase => SelectedDevice?.Lifecycle.Phase;

    public string GuestDisplayText =>
        InspectorSessionRows.GuestDisplay(SelectedPhase, GuestWidth, GuestHeight);

    public string DeliveredFrameText =>
        InspectorSessionRows.DeliveredFrame(SelectedPhase, Frame is not null, FrameWidth, FrameHeight);

    public string FrameRateText =>
        InspectorSessionRows.FrameRate(SelectedPhase, Frame is not null, FramesPerSecond);

    public string DroppedFramesText =>
        InspectorSessionRows.Dropped(SelectedPhase, Frame is not null, DroppedFrames);

    private double _lastInputLatency = double.NaN;

    public string InputLatencyText => double.IsNaN(_lastInputLatency)
        ? "not measured"
        : $"{_lastInputLatency:N0} ms (p95 {Percentile(_inputLatencies, 0.95):N0} ms / {_inputLatencies.Count} taps)";

    public string ReadinessText
    {
        get
        {
            if (!InspectorSessionRows.IsReported(SelectedPhase))
            {
                return "boot is reported while the guest runs";
            }

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
    /// the explanation behind the tooltip. EV-06: it is a session row, so a guest
    /// that is not Running or Starting shows the dash, never last run's figure.
    /// </summary>
    public string BootValueText =>
        InspectorSessionRows.Boot(
            SelectedPhase,
            SelectedDevice?.Readiness?["launchedToInteractiveSeconds"] is { } interactive
                ? interactive.GetValue<double>()
                : null);

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
    /// EV-03: the one reason a palette row is refused. Nothing selected and a
    /// supervised operation that owns the channel come first; after that the
    /// guest's own lifecycle answers, in its own words. The palette prints this
    /// only when the command's CanExecute is already false, so a row can never
    /// claim "Already running" for a guest that is stopping or unknown.
    /// </summary>
    private string PaletteDisabledReason(DeviceAction action) =>
        SelectedDevice is null ? "No device selected"
        : IsBusy ? "Busy"
        : SelectedDevice.Lifecycle.DisabledReason(action) ?? "Not available";

    /// <summary>
    /// EV-03: a command button's tooltip. While the command can run it is the
    /// action's own sentence; while it cannot, it is the reason the lifecycle
    /// gives, so a disabled control explains itself instead of going quiet.
    /// </summary>
    private string ActionTooltip(DeviceAction action, string allowed)
    {
        if (SelectedDevice is null)
        {
            return "No device selected";
        }

        return IsBusy
            ? "Busy"
            : SelectedDevice.Lifecycle.DisabledReason(action) ?? allowed;
    }

    public string RestartTooltip => ActionTooltip(DeviceAction.Restart, "Restart the selected guest");

    public string ColdBootTooltip => ActionTooltip(DeviceAction.ColdBoot, "Cold boot the selected guest");

    public string AdbRootTooltip => ActionTooltip(
        DeviceAction.AdbRoot,
        "Run adb root on the selected guest (confirmation is the supervisor's approval)");

    public string AdbUnrootTooltip => ActionTooltip(
        DeviceAction.AdbUnroot,
        "Return the guest ADB daemon to the shell user");

    public string RefreshAdbStateTooltip => ActionTooltip(
        DeviceAction.RefreshAdbState,
        "Re-read the ADB daemon identity");

    public string ScreenshotTooltip => ActionTooltip(
        DeviceAction.Screenshot,
        "Capture a PNG of the selected guest");

    public string MagiskInstallTooltip => SelectedDevice is null ? "No device selected"
        : IsBusy ? "Busy"
        : _lab.InstallDisabledReason
            ?? "Patch this guest's boot image with a verified Magisk build (asks for consent)";

    public string MagiskRecoverTooltip => SelectedDevice is null ? "No device selected"
        : IsBusy ? "Busy"
        : _lab.CanRecover
            ? "Restore the stock boot image from the verified recovery point (asks for consent)"
            : _lab.RecoverDisabledReason ?? "Lab is stock";

    /// <summary>
    /// Re-raises the derived settings facts. The drawer calls this when it opens,
    /// because motion and high contrast can change while the window is running.
    /// </summary>
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

    /// <summary>
    /// EV-06: true only while the connect/doctor/devices pass is running. It is
    /// what disables Refresh: pressing it again cannot start a second connect.
    /// </summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// EV-06: one connect at a time. The slot is claimed before any work runs,
    /// so the Refresh command, the watchdog's recovery attempt and a re-entrant
    /// caller all share the task in flight instead of opening a second pipe.
    /// A completed task is replaced, so a later Refresh really does re-read.
    /// </summary>
    public Task InitializeAsync()
    {
        lock (_initializeGate)
        {
            if (_initializeTask is { IsCompleted: false } running)
            {
                DesktopLog.Write("initialize: joined the connect already in flight");
                return running;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _initializeTask = completion.Task;
            DesktopLog.Write("initialize: connect requested");
            _ = CompleteInitializeAsync(completion);
            return completion.Task;
        }
    }

    private async Task CompleteInitializeAsync(TaskCompletionSource completion)
    {
        try
        {
            await InitializeCoreAsync().ConfigureAwait(true);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            // InitializeCoreAsync reports its own failures; this keeps an
            // unexpected throw observable by the awaiter instead of escaping as
            // an unobserved task exception.
            completion.TrySetException(ex);
        }
    }

    private async Task InitializeCoreAsync()
    {
        IsConnecting = true;
        IsBusy = true;
        BusyText = "Connecting to the local supervisor...";
        ClearError();

        // A deliberate refresh or Retry re-arms the watchdog; its own recovery
        // attempts must not, or the budget would never be spent. The watchdog is
        // running for the whole attempt, not just while this method executes, so
        // a Refresh that lands during a backoff delay cannot reset the budget.
        if (!_watchdog.IsRunning)
        {
            _watchdog.Reset();
        }

        try
        {
            _connection ??= await SupervisorConnection.ConnectAsync(
                    _paths.Root, TimeSpan.FromSeconds(25), log: DesktopLog.Write)
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
            IsConnecting = false;
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
    ///
    /// EV-05: this is the <see cref="SubEvent.PipeLost"/> event on both
    /// streams. No unsubscribe is sent - there is no pipe to send it on - and a
    /// subscribe that is still in flight is handed back when its grant lands.
    /// </summary>
    private void ForgetSubscriptions()
    {
        ApplyFrameEvent(new SubEvent.PipeLost());
        ApplyLogcatEvent(new SubEvent.PipeLost());
        NotifyLogFilterState();
    }

    /// <summary>
    /// EV-05: the only writer of the frame stream's state. Returns the id that
    /// has to be unsubscribed, if the event made one obsolete.
    /// </summary>
    private string? ApplyFrameEvent(SubEvent e)
    {
        var unsubscribe = Advance(ref _frameSub, e);
        // EV-11: the ring belongs to the subscription's life. A stream that
        // stopped holding an id - unsubscribe, guest switch, a close from the
        // supervisor, a lost pipe - closes the mapping here and nowhere else.
        if (Volatile.Read(ref _frameSub).Phase != SubPhase.Live)
        {
            CloseFrameMap();
        }

        return unsubscribe;
    }

    /// <summary>
    /// EV-11: opens the per-instance ring once for this subscription. Returns
    /// false when the supervisor did not name a map or the map could not be
    /// opened, which is the caller's signal to fall back to frame files.
    /// </summary>
    private bool OpenFrameMap(string? mapName, string device)
    {
        if (string.IsNullOrEmpty(mapName))
        {
            if (!_mapFailureLogged)
            {
                _mapFailureLogged = true;
                DesktopLog.Write($"frames {device} map: the supervisor named no ring; using frame files");
            }

            _frameFileFallback = true;
            return false;
        }

        var reader = FrameRingReader.TryOpen(mapName, out var error);
        if (reader is null)
        {
            if (!_mapFailureLogged)
            {
                _mapFailureLogged = true;
                DesktopLog.Write($"frames {device} map {mapName} could not be opened: {error}");
            }

            _frameFileFallback = true;
            return false;
        }

        lock (_mapGate)
        {
            _frameMap?.Dispose();
            _frameMap = reader;
        }

        DesktopLog.Write(
            $"frames {device} map {mapName} opened ({reader.SlotCount} slots of {reader.Capacity} bytes)");
        return true;
    }

    private void CloseFrameMap()
    {
        lock (_mapGate)
        {
            if (_frameMap is null)
            {
                return;
            }

            _frameMap.Dispose();
            _frameMap = null;
        }

        DesktopLog.Write("frames map closed");
    }

    /// <summary>EV-05: the only writer of the log stream's state.</summary>
    private string? ApplyLogcatEvent(SubEvent e) => Advance(ref _logSub, e);

    /// <summary>
    /// EV-05: applies one event to one stream. The field is re-read and swapped
    /// until the swap wins, so a caller on the pipe thread and a caller on the
    /// UI thread cannot both apply an event to the same old value.
    /// </summary>
    private static string? Advance(ref StreamSubscription field, SubEvent e)
    {
        while (true)
        {
            var current = Volatile.Read(ref field);
            var (next, unsubscribe) = StreamSubscription.Next(current, e);
            if (ReferenceEquals(Interlocked.CompareExchange(ref field, next, current), current))
            {
                return unsubscribe;
            }
        }
    }

    /// <summary>
    /// EV-05: sends an unsubscribe for an id this window no longer holds. The
    /// state already moved; a refusal here (the supervisor already closed it,
    /// or the pipe is gone) changes nothing and is only logged.
    /// </summary>
    private async Task UnsubscribeAsync(string operation, string id, TimeSpan deadline)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        try
        {
            await RequestAsync(operation, new JsonObject { ["subscription"] = id }, deadline).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"unsubscribe {operation} id={id} failed: {ex.GetType().Name}: {ex.Message}");
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
        RefreshCommand.RaiseCanExecuteChanged();
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

            try
            {
                // The same initialization the app runs on launch and on Retry:
                // locate or start the supervisor, connect, doctor, devices. It is
                // single-flight, so a Refresh that races this attempt joins it
                // instead of opening a second pipe.
                await InitializeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // InitializeAsync reports its own failures; this keeps a
                // cancelled or unexpected throw from escaping the fire-and-forget
                // run as an unobserved task exception.
                DesktopLog.Write("watchdog: attempt threw " + ex.GetType().Name + ": " + ex.Message);
            }

            if (IsConnected)
            {
                _watchdog.AttemptSucceeded();
                DesktopLog.Write($"watchdog: supervisor reachable again on attempt {attempt}");
                RefreshCommand.RaiseCanExecuteChanged();
                return;
            }

            _watchdog.AttemptFailed();
            RefreshCommand.RaiseCanExecuteChanged();
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

        // EV-05: the selection change is the DeviceChanged event on both
        // streams. It is a no-op when the same guest is re-selected (a refresh
        // rebuilds the list), so a poll never churns a live stream, and a live
        // id is unsubscribed exactly once here.
        var sameFrameGuest = StreamSubscription.IsSameGuest(Volatile.Read(ref _frameSub), device?.Name);
        var unsubscribedFrame = ApplyFrameEvent(new SubEvent.DeviceChanged(device?.Name));
        var unsubscribedLog = ApplyLogcatEvent(new SubEvent.DeviceChanged(device?.Name));
        if (!sameFrameGuest)
        {
            Frame = null;
            FrameWidth = 0;
            FrameHeight = 0;
            GuestWidth = 0;
            GuestHeight = 0;
            _frameSequence = 0;
            _droppedFrames = 0;
            _frameSlot.ResetDropped();
            _lastFramePresented = default;
            _lastFrameInfoUpdated = default;
            FramesPerSecond = 0;
            _frameIntervals.Clear();
            _pendingInput = null;
            _frameWaitSince = null;
            _launchedUtc = null;
            _captureRestricted = false;
            _captureRestrictedReason = "";
            _lab = LabUiState.NotChecked;
            MagiskState = _lab.Tooltip;
            // EV-06: the inspector's session rows belong to the guest that was
            // selected, so the new selection starts from "not reported".
            NotifySessionRows();
            UpdateFrameHealth();
            ClearError();
            ResetBootStages();
            UpdateStage();
        }

        NotifyLogFilterState();
        await UnsubscribeAsync(OperationNames.FrameUnsubscribe, unsubscribedFrame ?? "", TimeSpan.FromSeconds(15))
            .ConfigureAwait(true);
        await UnsubscribeAsync(OperationNames.LogcatUnsubscribe, unsubscribedLog ?? "", TimeSpan.FromSeconds(10))
            .ConfigureAwait(true);

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
            // not when a stale record word says so. EV-05: the state machine
            // decides whether anything has to be opened at all.
            if (StreamSubscription.ShouldSubscribe(
                    Volatile.Read(ref _frameSub), device.Lifecycle.CanAttachFrames, device.Name))
            {
                await SubscribeFramesAsync(device).ConfigureAwait(true);
            }

            if (IsLogcatOpen || IsLogcatPage)
            {
                await EnsureLogcatAsync().ConfigureAwait(true);
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
            if (StreamSubscription.ShouldSubscribe(
                    Volatile.Read(ref _frameSub), device.Lifecycle.CanAttachFrames, device.Name))
            {
                await SubscribeFramesAsync(device).ConfigureAwait(true);
            }

            // UX-18: a closed log stream is re-attached the same way the frame
            // stream is, so the drawer comes back by itself after the guest
            // does.
            if (IsLogcatOpen || IsLogcatPage)
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
            UpdateFrameHealth();
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
            await SubscribeFramesCoreAsync(device, CaptureWidth, CaptureHeight).ConfigureAwait(true);
        }
        finally
        {
            _subscribeGate.Release();
        }
    }

    /// <summary>
    /// EV-12: the window's viewport chose a capture size. If the stream is
    /// live, it is closed through the EV-05 state machine and opened again at
    /// the new size, which also gives the supervisor a fresh EV-11 ring sized
    /// for the new frame. The last picture stays on screen for the whole round
    /// trip (Frame is never cleared here), so the viewport has no black gap.
    ///
    /// Returns whether the call changed the size the stream asks for.
    /// </summary>
    public async Task<bool> ApplyCaptureSizeAsync(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        await _subscribeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var current = Volatile.Read(ref _frameSub);
            var device = SelectedDevice;
            var changed = width != _captureWidth || height != _captureHeight;
            if (!changed && current.Phase == SubPhase.Live)
            {
                return false;
            }

            if (current.Phase == SubPhase.Live
                && current.Id is { Length: > 0 } live
                && device is not null
                && StreamSubscription.IsSameGuest(current, device.Name))
            {
                // The live id is closed locally and handed back; the EV-11 ring
                // belongs to that id and is closed by the transition.
                ApplyFrameEvent(new SubEvent.Closed(live));
                await UnsubscribeAsync(OperationNames.FrameUnsubscribe, live, TimeSpan.FromSeconds(15))
                    .ConfigureAwait(true);

                Volatile.Write(ref _captureWidth, width);
                Volatile.Write(ref _captureHeight, height);
                DesktopLog.Write($"frames capture {device.Name} {width}x{height}");

                // A close or a pipe loss during the unsubscribe is not retried
                // here - the status poll reattaches with the size above.
                if (SelectedDevice?.Name == device.Name
                    && StreamSubscription.ShouldSubscribe(
                        Volatile.Read(ref _frameSub), device.Lifecycle.CanAttachFrames, device.Name))
                {
                    await SubscribeFramesCoreAsync(device, width, height).ConfigureAwait(true);
                }

                return true;
            }

            // No live stream (still booting, or between attachments): remember
            // the size so the next subscribe opens at it.
            Volatile.Write(ref _captureWidth, width);
            Volatile.Write(ref _captureHeight, height);
            if (changed)
            {
                DesktopLog.Write($"frames capture pending {width}x{height}");
            }

            return changed;
        }
        finally
        {
            _subscribeGate.Release();
        }
    }

    /// <summary>
    /// EV-05 / EV-12: opens one frame subscription at the given capture size.
    /// The caller holds <see cref="_subscribeGate"/>.
    /// </summary>
    private async Task SubscribeFramesCoreAsync(DeviceItemViewModel device, int captureWidth, int captureHeight)
    {
        try
        {
            // UX-03: a frame subscription is only opened for a guest the
            // supervisor reports as up. Starting one while Android boots
            // holds the guest's ADB channel, which starves the status reads the
            // boot stepper and the state pill depend on.
            // UX-18: the lifecycle decides, so a guest whose own boot answer
            // says it is up is attached even if the record still says Starting.
            // EV-05: the state machine decides whether anything is opened at
            // all, so two callers cannot each start a stream.
            if (SelectedDevice?.Name != device.Name
                || !StreamSubscription.ShouldSubscribe(
                    Volatile.Read(ref _frameSub), device.Lifecycle.CanAttachFrames, device.Name))
            {
                return;
            }

            DesktopLog.Write($"frames subscribe {device.Name} {captureWidth}x{captureHeight}");
            ApplyFrameEvent(new SubEvent.Requested(device.Name));
            var data = await RequestAsync(
                OperationNames.FrameSubscribe,
                new JsonObject
                {
                    ["name"] = device.Name,
                    ["format"] = "rgb",
                    ["width"] = captureWidth,
                    ["height"] = captureHeight,
                    // EV-11: read the per-instance ring. A window that falls back
                    // to files asks for "file", which is what the CLI always
                    // asked for, so only a real fallback makes frames/ churn.
                    // The legacy A/B path renders from the file, so it asks for
                    // one too.
                    ["delivery"] = _frameFileFallback || _legacyViewport ? FrameDelivery.File : FrameDelivery.Shared,
                },
                TimeSpan.FromSeconds(60)).ConfigureAwait(true);

            var subscription = data["subscription"]!.GetValue<string>();
            // EV-05: a grant lands only for the guest the stream waits on. A
            // late one - the selection moved while this request was out - is
            // handed back to the supervisor instead of becoming this window's
            // id, which used to leave the viewport attached to the wrong guest.
            var stale = ApplyFrameEvent(new SubEvent.Granted(subscription, device.Name));
            if (stale is not null)
            {
                DesktopLog.Write($"frames subscribe {device.Name} late id={subscription}; unsubscribed");
                await UnsubscribeAsync(OperationNames.FrameUnsubscribe, stale, TimeSpan.FromSeconds(15))
                    .ConfigureAwait(true);
                return;
            }

            // EV-11: the map is opened once, here, while the subscription is
            // live. A window that cannot open it falls back to the file path on
            // the next poll, and says so once.
            if (!_frameFileFallback && !OpenFrameMap(data["mapName"]?.GetValue<string>(), device.Name))
            {
                DesktopLog.Write($"frames subscribe {device.Name} map unavailable; falling back to files");
                var live = Volatile.Read(ref _frameSub).Id;
                ApplyFrameEvent(new SubEvent.Closed(live ?? subscription));
                await UnsubscribeAsync(OperationNames.FrameUnsubscribe, subscription, TimeSpan.FromSeconds(15))
                    .ConfigureAwait(true);
                return;
            }

            DesktopLog.Write($"frames subscribed {device.Name} id={subscription}");
            _frameWaitSince = DateTimeOffset.UtcNow;
            StartFrameLoop();
            if (SelectedDevice?.Name == device.Name)
            {
                UpdateStage();
            }
        }
        catch (Exception ex)
        {
            // EV-05: a failed request leaves nothing behind, so the state
            // returns to None and the next poll may try again.
            ApplyFrameEvent(new SubEvent.Refused());
            DesktopLog.Write($"frames subscribe {device.Name} failed: {ex.GetType().Name}: {ex.Message}");

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
    }

    /// <summary>
    /// EV-05: ends the frame stream. A live id is closed locally, which hands
    /// back the id for an unsubscribe; a subscribe that is still in flight is
    /// dropped instead, because the window does not know its id yet - the grant
    /// is handed back when it lands.
    /// </summary>
    private async Task UnsubscribeFramesAsync()
    {
        var current = Volatile.Read(ref _frameSub);
        if (current.Phase == SubPhase.Live && current.Id is { Length: > 0 } live)
        {
            // A local close: the id is closed here, so it is this caller that
            // hands it back. (The state machine's Closed rule answers a close
            // the supervisor sent, which needs no second request.)
            ApplyFrameEvent(new SubEvent.Closed(live));
            DesktopLog.Write($"frames unsubscribe id={live}");
            await UnsubscribeAsync(OperationNames.FrameUnsubscribe, live, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);
            return;
        }

        // A subscribe that is still in flight has no id yet: it is dropped here
        // and its grant is handed back when it lands.
        ApplyFrameEvent(new SubEvent.PipeLost());
    }

    private async Task EnsureLogcatAsync()
    {
        // UX-18: the log stream follows the guest's engine, so it attaches
        // while Android is still booting - the boot log is worth reading.
        // EV-05: one state machine decides whether a stream is opened, so
        // opening the drawer twice cannot open two streams.
        var device = SelectedDevice;
        if (device is null
            || !StreamSubscription.ShouldSubscribe(
                Volatile.Read(ref _logSub), device.Lifecycle.EngineAlive, device.Name))
        {
            NotifyLogFilterState();
            return;
        }

        try
        {
            DesktopLog.Write($"logcat subscribe {device.Name}");
            ApplyLogcatEvent(new SubEvent.Requested(device.Name));
            var data = await RequestAsync(
                OperationNames.LogcatSubscribe,
                new JsonObject { ["name"] = device.Name },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            var subscription = data["subscription"]!.GetValue<string>();
            var stale = ApplyLogcatEvent(new SubEvent.Granted(subscription, device.Name));
            if (stale is not null)
            {
                DesktopLog.Write($"logcat subscribe {device.Name} late id={subscription}; unsubscribed");
                await UnsubscribeAsync(OperationNames.LogcatUnsubscribe, stale, TimeSpan.FromSeconds(10))
                    .ConfigureAwait(true);
                return;
            }

            DesktopLog.Write($"logcat subscribed {device.Name} id={subscription}");
            // UX-08: a successful attach clears the drawer's own error band.
            SetLogcatError("");
        }
        catch (Exception ex)
        {
            ApplyLogcatEvent(new SubEvent.Refused());
            DesktopLog.Write($"logcat subscribe {device.Name} failed: {ex.GetType().Name}: {ex.Message}");
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

    /// <summary>
    /// EV-05: ends the log stream the same way the frame stream is ended - a
    /// live id is closed locally and handed back, an in-flight subscribe is
    /// dropped and its grant handed back when it lands.
    /// </summary>
    private async Task StopLogcatAsync()
    {
        var current = Volatile.Read(ref _logSub);
        // The window's shutdown path disposes off the dispatcher once its first
        // await has completed; a visual refresh there would touch the log view
        // from a pool thread.
        if (_dispatcher.CheckAccess())
        {
            NotifyLogFilterState();
        }

        if (current.Phase == SubPhase.Live && current.Id is { Length: > 0 } live)
        {
            ApplyLogcatEvent(new SubEvent.Closed(live));
            DesktopLog.Write($"logcat unsubscribe id={live}");
            await UnsubscribeAsync(OperationNames.LogcatUnsubscribe, live, TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);
            return;
        }

        ApplyLogcatEvent(new SubEvent.PipeLost());
    }

    private void OnSupervisorEvent(EventEnvelope envelope)
    {
        switch (envelope.Event)
        {
            case EventNames.Frame:
                if (AcceptsFrames(envelope.Subscription) && envelope.Data is JsonObject frame)
                {
                    // EV-11: a shared-memory frame carries its slot; a file
                    // frame carries its path. Either one is a deliverable frame.
                    var slot = frame["slot"]?.GetValue<int>() ?? -1;
                    var mapName = frame["mapName"]?.GetValue<string>();
                    var path = frame["path"]?.GetValue<string>() ?? "";
                    if (slot < 0 && path.Length == 0)
                    {
                        break;
                    }

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
                    _frameSlot.Post(new PendingFrame(envelope.Subscription, path, format, frameWidth, frameHeight,
                        stride, guestWidth, guestHeight, sequence, dropped, receivedUtc, writtenUtc,
                        Stopwatch.GetTimestamp(), slot, mapName));
                }

                break;

            case EventNames.Logcat:
                if (envelope.Subscription == Volatile.Read(ref _logSub).Id
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
                    // the session. EV-05: the close is the Closed(id) event, and
                    // the state machine ignores an id it does not hold, so a
                    // close for a replaced subscription cannot cancel the live
                    // one.
                    var kind = SubscriptionClose.Which(
                        envelope.Subscription,
                        Volatile.Read(ref _frameSub).Id,
                        Volatile.Read(ref _logSub).Id);

                    switch (kind)
                    {
                        case SubscriptionKind.Frames:
                            ApplyFrameEvent(new SubEvent.Closed(envelope.Subscription));
                            DesktopLog.Write($"frames closed id={envelope.Subscription} reason={reason}");
                            StatusMessage = "Frame stream closed: " + reason;
                            break;

                        case SubscriptionKind.Logcat:
                            ApplyLogcatEvent(new SubEvent.Closed(envelope.Subscription));
                            DesktopLog.Write($"logcat closed id={envelope.Subscription} reason={reason}");
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

    /// <summary>
    /// EV-05: whether a frame event can belong to the stream this window is
    /// opening or following. While the state is
    /// <see cref="SubPhase.Subscribing"/> the granted id is not known yet - the
    /// reply and the first frames travel the same pipe and the handler can run
    /// before the request's continuation - so those frames are queued and
    /// <see cref="PresentFrame"/> drops them unless the id matches by the time
    /// they are shown. That is exactly what the old "subscribe pending" flag
    /// did, without the second field.
    /// </summary>
    private bool AcceptsFrames(string subscription) =>
        Volatile.Read(ref _frameSub).Phase != SubPhase.None && !string.IsNullOrEmpty(subscription);

    private void StartFrameLoop()
    {
        if (_frameLoopRunning)
        {
            return;
        }

        // EV-10b: presentation is driven by the render tick, at most one frame
        // per tick. The subscription is made on the UI thread; the view model is
        // created there and every frame-subscription reply continues there.
        if (!_renderingHooked)
        {
            if (_dispatcher.CheckAccess())
            {
                HookFrameRendering();
            }
            else
            {
                _ = _dispatcher.BeginInvoke(HookFrameRendering, DispatcherPriority.Normal);
            }
        }

        _frameLoopRunning = true;
        _ = Task.Run(FrameLoopAsync);
    }

    private void HookFrameRendering()
    {
        if (_renderingHooked)
        {
            return;
        }

        CompositionTarget.Rendering += OnFrameRendering;
        _renderingHooked = true;
    }

    private async Task FrameLoopAsync()
    {
        if (_viewportTrace)
        {
            // One quiet-host reading of the same conversion the live path runs,
            // taken before the frame loop starts. The per-frame trace line shows
            // the live cost; the difference is host scheduling, not the code.
            var benchSource = FrameBuffers.Rent(540 * 1200 * 3);
            var benchDestination = FrameBuffers.Rent(540 * 1200 * 4);
            try
            {
                var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime;
                var benchStart = Stopwatch.GetTimestamp();
                double firstMs = 0;
                double lastTenMs = 0;
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    var lap = Stopwatch.GetTimestamp();
                    FramePixelConverter.Convert(
                        benchSource.AsSpan(0, 540 * 1200 * 3), 540, 1200, 540 * 3,
                        FramePixelFormat.Rgb24,
                        benchDestination.AsSpan(0, 540 * 1200 * 4), 540 * 4);
                    var lapMs = Elapsed(lap, Stopwatch.GetTimestamp());
                    if (iteration == 0)
                    {
                        firstMs = lapMs;
                    }

                    if (iteration >= 90)
                    {
                        lastTenMs += lapMs;
                    }
                }

                var wall = Elapsed(benchStart, Stopwatch.GetTimestamp());
                var cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                var fillStart = Stopwatch.GetTimestamp();
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    benchDestination.AsSpan(0, 540 * 1200 * 4).Fill(0x5A);
                }

                var fill = Elapsed(fillStart, Stopwatch.GetTimestamp());
                var inlineStart = Stopwatch.GetTimestamp();
                var benchSourceSpan = benchSource.AsSpan(0, 540 * 1200 * 3);
                var benchDestinationSpan = benchDestination.AsSpan(0, 540 * 1200 * 4);
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    var s = 0;
                    var d = 0;
                    for (var pixel = 0; pixel < 540 * 1200; pixel++)
                    {
                        benchDestinationSpan[d] = benchSourceSpan[s + 2];
                        benchDestinationSpan[d + 1] = benchSourceSpan[s + 1];
                        benchDestinationSpan[d + 2] = benchSourceSpan[s];
                        benchDestinationSpan[d + 3] = 255;
                        s += 3;
                        d += 4;
                    }
                }

                var inline = Elapsed(inlineStart, Stopwatch.GetTimestamp());

                // EV-10b: the two UI-thread copies the A/B compares. Both are
                // plain block moves; the only difference is 1.86 MB vs 2.59 MB.
                var nativeCopyStart = Stopwatch.GetTimestamp();
                var nativeLength = 540 * 1200 * 3;
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    FrameCopier.CopyRows(
                        benchSource.AsSpan(0, nativeLength), 540 * 3, 540 * 3, 1200,
                        benchDestination.AsSpan(0, nativeLength), 540 * 3);
                }

                var nativeCopy = Elapsed(nativeCopyStart, Stopwatch.GetTimestamp());
                var bgraLength = 540 * 1200 * 4;
                var benchBgra = FrameBuffers.Rent(bgraLength);
                try
                {
                    var bgraCopyStart = Stopwatch.GetTimestamp();
                    for (var iteration = 0; iteration < 100; iteration++)
                    {
                        FrameCopier.CopyRows(
                            benchDestination.AsSpan(0, bgraLength), 540 * 4, 540 * 4, 1200,
                            benchBgra.AsSpan(0, bgraLength), 540 * 4);
                    }

                    var bgraCopy = Elapsed(bgraCopyStart, Stopwatch.GetTimestamp());
                    DesktopLog.Write(
                        $"convert bench 540x1200: {wall / 100:N2} ms/frame wall, {cpu / 100:N2} ms/frame cpu, " +
                        $"first={firstMs:N2} ms, last10={lastTenMs / 10:N2} ms, memfill={fill / 100:N2} ms, " +
                        $"inline={inline / 100:N2} ms, nativeCopy={nativeCopy / 100:N2} ms, " +
                        $"bgraCopy={bgraCopy / 100:N2} ms " +
                        $"(Ssse3={System.Runtime.Intrinsics.X86.Ssse3.IsSupported}, " +
                        $"priority={process.PriorityClass}, cores={Environment.ProcessorCount}, " +
                        $"thread={Thread.CurrentThread.Priority}, pool={Thread.CurrentThread.IsThreadPoolThread}, " +
                        $"arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
                        $"x64={Environment.Is64BitProcess}, vec={Vector128<byte>.Count})");
                }
                finally
                {
                    FrameBuffers.Return(benchBgra);
                }
            }
            finally
            {
                FrameBuffers.Return(benchSource);
                FrameBuffers.Return(benchDestination);
            }
        }

        while (!_lifetime.IsCancellationRequested)
        {
            PendingFrame pending;
            try
            {
                pending = await _frameSlot.TakeAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var takenAt = Stopwatch.GetTimestamp();
            if (_legacyViewport)
            {
                // EV-10 comparison path: the pre-change bitmap, one frozen
                // BitmapSource per frame, kept for one slice behind
                // EW_VIEWPORT_LEGACY=1.
                try
                {
                    var loadStart = Stopwatch.GetTimestamp();
                    var bitmap = LoadFrame(pending);
                    var loadedAt = Stopwatch.GetTimestamp();
                    if (bitmap is not null)
                    {
                        await _dispatcher.InvokeAsync(
                            () =>
                            {
                                var presentAt = Stopwatch.GetTimestamp();
                                PresentFrame(bitmap, pending, DateTimeOffset.UtcNow);
                                if (_viewportTrace && (++_viewportTraceCounter % 20) == 0)
                                {
                                    DesktopLog.Write(
                                        $"legacy frame trace seq={pending.Sequence} " +
                                        $"waitedMs={Elapsed(pending.EventTimestamp, takenAt):N1} " +
                                        $"loadMs={Elapsed(loadStart, loadedAt):N1} " +
                                        $"uiWaitMs={Elapsed(loadedAt, presentAt):N1} " +
                                        $"totalMs={Elapsed(pending.EventTimestamp, Stopwatch.GetTimestamp()):N1}");
                                }
                            },
                            DispatcherPriority.Render);
                    }
                }
                catch (Exception ex)
                {
                    DesktopLog.Write("decode failed: " + ex.GetType().Name + ": " + ex.Message);
                }

                continue;
            }

            // EV-10b: read and stage on this background task. The UI thread
            // never converts a frame; it only copies the staged buffer into the
            // surface when the render tick gets the lock.
            try
            {
                var staged = StageFrame(pending, takenAt);
                if (staged is not null && _stageSlot.Post(staged) is { } replaced)
                {
                    replaced.Dispose();
                }

                if (staged is not null)
                {
                    // Ask the window for one composition so the render tick
                    // that presents this frame actually happens.
                    _ = _dispatcher.BeginInvoke(
                        () => FrameStaged?.Invoke(),
                        DispatcherPriority.Render);
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

    /// <summary>
    /// EV-10b: one file read plus one staging conversion, all on the frame-loop
    /// task. A source layout WPF renders directly is staged as-is (the read
    /// buffer is handed over, no second copy); anything else is expanded into a
    /// pooled BGRA buffer. The staged frame carries the three background
    /// timings so the trace can attribute every millisecond.
    /// </summary>
    private StagedFrame? StageFrame(PendingFrame pending, long takenAt)
    {
        var readStart = Stopwatch.GetTimestamp();
        var read = ReadFrame(pending);
        if (read is null)
        {
            return null;
        }

        var readAt = Stopwatch.GetTimestamp();
        try
        {
            var surfaceFormat = ViewportPixelPolicy.SurfaceFormatFor(read.Format, _stageMode);
            if (ViewportPixelPolicy.NeedsConversion(read.Format, surfaceFormat))
            {
                var rowBytes = checked(read.Width * FramePixelConverter.Bgra32BytesPerPixel);
                var length = checked(rowBytes * read.Height);
                var buffer = FrameBuffers.Rent(length);
                try
                {
                    FramePixelConverter.Convert(
                        read.Pixels.Span,
                        read.Width,
                        read.Height,
                        read.Stride,
                        read.Format,
                        buffer.AsSpan(0, length),
                        rowBytes);
                }
                catch
                {
                    FrameBuffers.Return(buffer);
                    throw;
                }

                var stagedAt = Stopwatch.GetTimestamp();
                return new StagedFrame(buffer, read.Width, read.Height, rowBytes, surfaceFormat, pending,
                    Elapsed(pending.EventTimestamp, takenAt), Elapsed(takenAt, readAt), Elapsed(readAt, stagedAt));
            }

            // Native copy: the read buffer already has the layout the surface
            // wants, so it is the staged buffer. Reusing it saves a full-frame
            // copy on the background task (and one pooled rent per frame).
            var rowBytesNative = checked(read.Width * ViewportPixelPolicy.BytesPerPixel(surfaceFormat));
            var owned = read.Detach();
            return new StagedFrame(owned, read.Width, read.Height, rowBytesNative, surfaceFormat, pending,
                Elapsed(pending.EventTimestamp, takenAt), Elapsed(takenAt, readAt), 0);
        }
        finally
        {
            read.Dispose();
        }
    }

    /// <summary>
    /// EV-10b: one present attempt per CompositionTarget.Rendering tick. It takes
    /// at most one staged frame; a frame whose TryLock fails is put back and
    /// retried on the next tick, and only a replacement by a newer frame counts
    /// as a drop.
    /// </summary>
    private void OnFrameRendering(object? sender, EventArgs e)
    {
        var tickAt = Stopwatch.GetTimestamp();
        _renderTicks++;
        FlushPresentTrace(tickAt);
        if (_legacyViewport)
        {
            return;
        }

        if (!_stageSlot.TryTake(out var staged))
        {
            return;
        }

        if (staged.Source.Subscription != Volatile.Read(ref _frameSub).Id)
        {
            staged.Dispose();
            return;
        }

        if (!TryPresentStaged(staged))
        {
            if (_stageSlot.PutBack(staged) is { } discarded)
            {
                // A newer frame is already waiting: this older one is the one
                // drop the policy allows.
                discarded.Dispose();
            }
        }
    }

    /// <summary>
    /// EV-10b: the UI-thread half. <c>TryLock(TimeSpan.Zero)</c> never blocks;
    /// when the render thread still holds the back buffer the frame stays
    /// staged. The copy is a plain row copy because the buffer already has the
    /// surface layout. EW_VIEWPORT_COPY=write measures WPF's WritePixels instead.
    /// </summary>
    private bool TryPresentStaged(StagedFrame staged)
    {
        var bitmap = _surfaceCache.Get(staged.Width, staged.Height, staged.SurfaceFormat);
        var lockStart = Stopwatch.GetTimestamp();
        long lockedAt;
        long copiedAt;
        long unlockedAt;

        if (_writePixelsCopy)
        {
            lockedAt = lockStart;
            bitmap.WritePixels(
                new Int32Rect(0, 0, staged.Width, staged.Height),
                staged.Buffer,
                staged.Stride,
                0);
            copiedAt = Stopwatch.GetTimestamp();
            unlockedAt = copiedAt;
        }
        else
        {
            if (!bitmap.TryLock(TimeSpan.Zero))
            {
                staged.LockRetries++;
                Interlocked.Increment(ref _lockBusy);
                return false;
            }

            lockedAt = Stopwatch.GetTimestamp();
            try
            {
                unsafe
                {
                    var backBuffer = new Span<byte>(
                        (void*)bitmap.BackBuffer, bitmap.BackBufferStride * staged.Height);
                    FrameCopier.CopyRows(
                        staged.Pixels,
                        staged.Stride,
                        staged.RowBytes,
                        staged.Height,
                        backBuffer,
                        bitmap.BackBufferStride);
                }

                copiedAt = Stopwatch.GetTimestamp();
                bitmap.AddDirtyRect(new Int32Rect(0, 0, staged.Width, staged.Height));
            }
            finally
            {
                bitmap.Unlock();
                unlockedAt = Stopwatch.GetTimestamp();
            }
        }

        PresentFrame(bitmap, staged.Source, DateTimeOffset.UtcNow);
        if (_viewportTrace)
        {
            _pendingTrace = new PendingPresentTrace(
                staged.Source.Sequence,
                staged.QueueMs,
                staged.ReadMs,
                staged.StageMs,
                Elapsed(lockStart, lockedAt),
                Elapsed(lockedAt, copiedAt),
                Elapsed(copiedAt, unlockedAt),
                unlockedAt,
                Stopwatch.GetTimestamp(),
                staged.LockRetries,
                staged.SurfaceFormat,
                staged.Source.EventTimestamp);
        }

        staged.Dispose();
        return true;
    }

    /// <summary>
    /// EV-10b: completes the previous frame's split with the one segment that
    /// can only be read on the next tick: Unlock to the next
    /// CompositionTarget.Rendering.
    /// </summary>
    private void FlushPresentTrace(long tickAt)
    {
        if (_pendingTrace is not { } trace)
        {
            return;
        }

        _pendingTrace = null;
        if (!_viewportTrace || (++_viewportTraceCounter % 20) != 0)
        {
            return;
        }

        var ticks = _renderTicks - _traceTicksAtLastLine;
        _traceTicksAtLastLine = _renderTicks;
        DesktopLog.Write(
            $"frame trace seq={trace.Sequence} readMs={trace.ReadMs:N1} stageMs={trace.StageMs:N1} " +
            $"queueMs={trace.QueueMs:N1} lockMs={trace.LockMs:N1} copyMs={trace.CopyMs:N1} " +
            $"dirtyMs={trace.DirtyMs:N1} toRenderingMs={Elapsed(trace.UnlockedAt, tickAt):N1} " +
            $"uiMs={trace.UiMs:N1} totalMs={Elapsed(trace.EventTimestamp, trace.PresentedAt):N1} " +
            $"retries={trace.Retries} ticks={ticks} copy={(_writePixelsCopy ? "write" : "lock")} " +
            $"format={trace.SurfaceFormat} stageMode={_stageMode} busy={Volatile.Read(ref _lockBusy)} " +
            $"slotDropped={_frameSlot.Dropped} stageReplaced={_stageSlot.Replaced} " +
            $"ringTorn={Volatile.Read(ref _frameRingTorn)} ringSkipped={Volatile.Read(ref _frameRingSkipped)}");
    }

    private readonly record struct PendingPresentTrace(
        int Sequence,
        double QueueMs,
        double ReadMs,
        double StageMs,
        double LockMs,
        double CopyMs,
        double DirtyMs,
        long UnlockedAt,
        long PresentedAt,
        int Retries,
        string SurfaceFormat,
        long EventTimestamp)
    {
        public double UiMs => LockMs + CopyMs + DirtyMs;
    }

    private static double Elapsed(long from, long to) =>
        Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    /// <summary>
    /// EV-10: reads one frame into a pooled buffer on this background task. Raw
    /// frames are read straight into the pool; a PNG (the fallback transport) is
    /// decoded to BGRA into the same kind of buffer, so the UI thread only ever
    /// converts and copies.
    ///
    /// EV-11: a raw frame that names a ring slot is copied out of shared memory,
    /// which is one memcpy instead of a file open plus a 1.94 MB read. The copy
    /// is verified against the slot's seqlock; a torn copy is retried once and
    /// then skipped. The file path stays for everything else.
    /// </summary>
    private FrameRead? ReadFrame(PendingFrame frame)
    {
        if (FramePixelConverter.TryParse(frame.Format, out var format))
        {
            var bytesPerPixel = FramePixelConverter.BytesPerPixel(format);
            if (frame.Width <= 0 || frame.Height <= 0
                || frame.Width > 16384 || frame.Height > 16384)
            {
                return null;
            }

            var stride = checked(frame.Width * bytesPerPixel);
            if (frame.Stride != 0 && frame.Stride != stride)
            {
                // The producer writes exactly width*bytesPerPixel per row; a
                // padded stride would not match the file length the pump
                // validates, so such a frame is not readable.
                return null;
            }

            var length = checked(stride * frame.Height);
            if (frame.Slot >= 0 && ReadFromMap(frame, length, stride, format) is { } mapped)
            {
                return mapped;
            }

            if (frame.Path.Length == 0)
            {
                return null;
            }

            var buffer = FrameBuffers.Rent(length);
            try
            {
                using var stream = new FileStream(frame.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                stream.ReadExactly(buffer.AsSpan(0, length));
                return new FrameRead(buffer, frame.Width, frame.Height, stride, format);
            }
            catch
            {
                FrameBuffers.Return(buffer);
                throw;
            }
        }

        if (!string.Equals(frame.Format, "png", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var image = LoadBitmap(frame.Path);
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var pngStride = checked(width * FramePixelConverter.Bgra32BytesPerPixel);
        var pngLength = checked(pngStride * height);
        var pngBuffer = FrameBuffers.Rent(pngLength);
        try
        {
            converted.CopyPixels(pngBuffer, pngStride, 0);
            return new FrameRead(pngBuffer, width, height, pngStride, FramePixelFormat.Bgra32);
        }
        catch
        {
            FrameBuffers.Return(pngBuffer);
            throw;
        }
    }

    /// <summary>
    /// EV-11: one frame copied out of the shared-memory ring. The copy is
    /// verified by the slot's seqlock; a torn copy is retried exactly once and
    /// then skipped, which is the price of a writer that never waits for a
    /// reader. Returns null when the ring cannot serve this frame, and the
    /// caller then uses the file path if the event carried one.
    /// </summary>
    private FrameRead? ReadFromMap(PendingFrame frame, int length, int stride, FramePixelFormat format)
    {
        var buffer = FrameBuffers.Rent(length);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                FrameRingReadStatus status;
                FrameRingFrameInfo info;
                lock (_mapGate)
                {
                    if (_frameMap is null)
                    {
                        break;
                    }

                    status = _frameMap.TryRead(frame.Slot, buffer.AsSpan(0, length), stride, out info);
                }

                if (status == FrameRingReadStatus.Ok)
                {
                    // The slot must describe exactly the frame the event named;
                    // a slot from another size would not fit the rented buffer.
                    if (info.Width <= 0 || info.Height <= 0 || info.Stride != stride || info.Bytes > length)
                    {
                        break;
                    }

                    return new FrameRead(buffer, info.Width, info.Height, info.Stride, format);
                }

                if (status != FrameRingReadStatus.Torn)
                {
                    break;
                }

                // Torn: the writer took this slot while the copy ran. One more
                // attempt, then the frame is skipped rather than shown half
                // written.
                Interlocked.Increment(ref _frameRingTorn);
            }
        }
        catch
        {
            FrameBuffers.Return(buffer);
            throw;
        }

        FrameBuffers.Return(buffer);
        Interlocked.Increment(ref _frameRingSkipped);
        return null;
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
        if (source.Subscription != Volatile.Read(ref _frameSub).Id)
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
        // and stale stages; no timer does it. EV-04: the frame state is
        // recomputed from the same evidence the chip reads.
        _frameWaitSince = null;
        UpdateFrameHealth();

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
    /// EV-04: the stage is one resolution and one assignment. Every branch is
    /// still evidence - the lifecycle, the lab reading, the frame snapshot and
    /// the supervisor's own error text - but the order and the words live in
    /// <see cref="StudioStage"/> where they are tested without a window.
    /// </summary>
    private void UpdateStage()
    {
        ApplyStage(ShowSetup ? StudioStage.Hidden : StudioStage.Resolve(BuildStageContext()));
        NotifyLiveSurfaces();
    }

    private StudioStageContext BuildStageContext()
    {
        var device = SelectedDevice;
        return new StudioStageContext(
            device?.Lifecycle,
            _frameState,
            _lab,
            Connected: device is not null,
            HasFrame: HasFrame,
            DeviceName: device?.Name ?? "",
            LastError: device?.LastError ?? "",
            CaptureRestricted: _captureRestricted,
            CaptureRestrictedReason: _captureRestrictedReason,
            FrameWaitSeconds: _frameWaitSince is { } since
                ? Math.Max(0, (DateTimeOffset.UtcNow - since).TotalSeconds)
                : 0);
    }

    private void ApplyStage(StudioStage.Value stage)
    {
        StageTone = stage.Tone;
        StageTitle = stage.Title;
        StageDetail = stage.Detail;
        StageHint = stage.Hint;
        StageVisible = stage.Visible;
        ShowBootStages = stage.ShowBootStages;
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

        var wasStale = IsFrameStale;
        if (_frameState != state)
        {
            _frameState = state;
            OnPropertyChanged(nameof(StaleFrameTitle));
            OnPropertyChanged(nameof(StaleFrameDetail));
            OnPropertyChanged(nameof(StaleFrameTone));
            OnPropertyChanged(nameof(IsInputPaused));
        }

        if (wasStale != IsFrameStale)
        {
            OnPropertyChanged(nameof(IsFrameStale));
        }

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
            if (SelectedDevice?.Name == device.Name)
            {
                SetLab(LabUiState.From(
                    ex.Message,
                    installInProgress: false,
                    recoveryRequired: false,
                    LabJournalState.None));
            }
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

        // UX-18: the supervisor classifies the lab phase. EV-04: that reading,
        // the journal and the guest's stock state become one LabUiState, so the
        // row, the dot, the stage and both commands read the same value.
        var phase = lab["phase"]?.GetValue<string>() ?? "unknown";
        var recoveryRequired = lab["recoveryRequired"]?.GetValue<bool>() == true;
        var installInProgress = lab["installInProgress"]?.GetValue<bool>() == true;
        SetLab(LabUiState.From(
            phase,
            installInProgress,
            recoveryRequired,
            LabUiState.JournalFor(phase, lab["transaction"] is not null)));
    }

    private void SetLab(LabUiState lab)
    {
        _lab = lab;
        MagiskState = lab.Tooltip;
        OnPropertyChanged(nameof(MagiskLabValue));
        OnPropertyChanged(nameof(MagiskLabTooltip));
        OnPropertyChanged(nameof(LabDotTone));
        RaiseCommandStates();
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

        // UX-18: the lab needs the same three files every time and the
        // supervisor pins their hashes. The operator picks them once; after
        // that this button goes straight to the consent dialog. A file that has
        // gone missing, or that no longer matches its pin, asks for that one
        // file only.
        var remembered = LabInputsStore.Load(LabInputsStore.DefaultPath, DesktopLog.Write);
        var resolved = await ResolveLabInputsAsync(remembered).ConfigureAwait(true);
        if (resolved is null)
        {
            return;
        }

        // UX-09: the consent surface is the app's own dialog, not the OS message
        // box. It prints the same facts (this guest, these three files, the same
        // consequences) and the hashes are read from the selected files for
        // display only. Declining here returns before the first supervisor
        // request, so Cancel creates no transaction and writes no journal entry.
        var artifacts = new[]
        {
            await ConsentArtifact.ReadAsync("Ramdisk", resolved.Ramdisk).ConfigureAwait(true),
            await ConsentArtifact.ReadAsync("Manager APK", resolved.ManagerApk).ConfigureAwait(true),
            await ConsentArtifact.ReadAsync("Root probe", resolved.ProbeApk).ConfigureAwait(true),
        };
        if (!AskConsent(InstallMagiskConsent(device, artifacts))) return;

        // The files are remembered only after the operator has seen them in the
        // consent dialog, so what is stored is what was agreed to.
        LabInputsStore.Save(LabInputsStore.DefaultPath, resolved, DesktopLog.Write);

        await RunBusyAsync("Checking Magisk lab inputs...", async () =>
        {
            var prepared = await RequestAsync("magisk.prepare", new JsonObject
            {
                ["name"] = device.Name,
                ["patch"] = resolved.Ramdisk,
                ["apk"] = resolved.ManagerApk,
                ["probe"] = resolved.ProbeApk,
                ["allowProvisioningAdbRoot"] = true,
            }, TimeSpan.FromMinutes(3));
            SetLab(LabUiState.From("prepared", installInProgress: false, recoveryRequired: false, LabJournalState.Prepared));
            await ApprovedMagiskAction(device.Name, prepared, "install");
        });
    }

    /// <summary>
    /// UX-18: the lab's three input files.
    ///
    /// A remembered file is used as it is when it is still there and still
    /// matches the supervisor's pin. Otherwise the operator is asked for that
    /// one file, and a file that does not match its pin is refused here with
    /// the exact expected hash - before any consent, any journal or any guest
    /// change. The supervisor verifies the same pins again when it stages the
    /// transaction; this is the early, clearer half of that check.
    /// </summary>
    private async Task<LabInputs?> ResolveLabInputsAsync(LabInputs remembered)
    {
        var inputs = remembered;
        foreach (var role in LabInputRoles.All)
        {
            var path = inputs.For(role);
            if (path.Length > 0 && File.Exists(path) && IsPinnedLabInput(role, path))
            {
                continue;
            }

            var dialog = new OpenFileDialog
            {
                Title = LabInputRoles.PickerTitle(role),
                Filter = role == LabInputRoles.Ramdisk ? "Ramdisk (*.img)|*.img" : "Android package (*.apk)|*.apk",
                CheckFileExists = true,
                FileName = path.Length > 0 ? Path.GetFileName(path) : "",
            };
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            if (!IsPinnedLabInput(role, dialog.FileName))
            {
                var expected = ExpectedPin(role);
                ShowError(
                    new SupervisorRequestException(
                        ErrorCodes.InvalidRequest,
                        $"That file is not the pinned lab artifact. Expected SHA-256 {expected}, got {MagiskLabArtifacts.Hash(dialog.FileName)}."),
                    "magisk.prepare",
                    SelectedDevice?.Name ?? "");
                return null;
            }

            inputs = inputs.With(role, dialog.FileName);
        }

        return inputs;
    }

    /// <summary>Whether a file matches the supervisor's pin for that role.</summary>
    private static bool IsPinnedLabInput(string role, string path)
    {
        var expected = ExpectedPin(role);
        if (expected.Length == 0)
        {
            return false;
        }

        try
        {
            return MagiskLabArtifacts.Hash(path) == expected;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The supervisor's pinned hash for one role.</summary>
    private static string ExpectedPin(string role)
    {
        var key = LabInputRoles.PinKey(role);
        return key.Length > 0 && MagiskLabArtifacts.Pins.TryGetValue(key, out var hash)
            ? hash
            : "";
    }

    private async Task RecoverMagiskAsync()
    {
        if (SelectedDevice is not { } device) return;
        await RunBusyAsync("Checking stock recovery...", async () =>
        {
            var state = await RequestAsync("magisk.status", new JsonObject { ["name"] = device.Name }, TimeSpan.FromSeconds(30));
            if (state["transaction"] is null)
            {
                SetLab(LabUiState.From("not configured", installInProgress: false,
                    recoveryRequired: false, LabJournalState.None));
                return;
            }
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
        var phase = result["phase"]?.GetValue<string>() ?? "unknown";
        SetLab(LabUiState.From(
            phase,
            installInProgress: false,
            recoveryRequired: false,
            LabUiState.JournalFor(phase, hasTransaction: true)));
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
            // EV-05: that is PipeLost on both streams.
            ForgetSubscriptions();

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
        // EV-06: a guest that just stopped must stop reporting the frame and
        // boot figures of the run it was in, without waiting for the next frame.
        NotifySessionRows();
        NotifyLogFilterState();
        RaiseCommandStates();
    }

    /// <summary>
    /// EV-06: raises the five session-only inspector rows together. They share
    /// one rule (<see cref="InspectorSessionRows"/>) and one moment to change:
    /// a new selection, a lifecycle change or a cleared frame.
    /// </summary>
    private void NotifySessionRows()
    {
        OnPropertyChanged(nameof(GuestDisplayText));
        OnPropertyChanged(nameof(DeliveredFrameText));
        OnPropertyChanged(nameof(FrameRateText));
        OnPropertyChanged(nameof(DroppedFramesText));
        OnPropertyChanged(nameof(BootValueText));
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(BootTooltip));
        OnPropertyChanged(nameof(TelemetryFpsValue));
        OnPropertyChanged(nameof(TelemetryTooltip));
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
        RefreshCommand.RaiseCanExecuteChanged();
        AdbRootCommand.RaiseCanExecuteChanged();
        AdbUnrootCommand.RaiseCanExecuteChanged();
        RefreshAdbStateCommand.RaiseCanExecuteChanged();
        MagiskInstallCommand.RaiseCanExecuteChanged();
        MagiskRecoverCommand.RaiseCanExecuteChanged();
        // EV-03: a disabled control's tooltip is the reason the command is
        // refused, so it is re-read whenever the command states are.
        OnPropertyChanged(nameof(RestartTooltip));
        OnPropertyChanged(nameof(ColdBootTooltip));
        OnPropertyChanged(nameof(AdbRootTooltip));
        OnPropertyChanged(nameof(AdbUnrootTooltip));
        OnPropertyChanged(nameof(RefreshAdbStateTooltip));
        OnPropertyChanged(nameof(ScreenshotTooltip));
        OnPropertyChanged(nameof(MagiskInstallTooltip));
        OnPropertyChanged(nameof(MagiskRecoverTooltip));
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
        try
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_renderingHooked)
                {
                    CompositionTarget.Rendering -= OnFrameRendering;
                    _renderingHooked = false;
                }

                while (_stageSlot.TryTake(out var staged))
                {
                    staged.Dispose();
                }
            });
        }
        catch (InvalidOperationException)
        {
            // The dispatcher has already shut down; the pooled buffers are
            // reclaimed with the process.
        }

        await UnsubscribeFramesAsync().ConfigureAwait(false);
        await StopLogcatAsync().ConfigureAwait(false);
        CloseFrameMap();
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
        long EventTimestamp, int Slot = -1, string? MapName = null);

    /// <summary>
    /// EV-10: one frame read into a rented buffer, disposed back to the pool
    /// exactly once. It only ever exists between the background read and the
    /// UI-thread copy.
    /// </summary>
    private sealed class FrameRead : IDisposable
    {
        private byte[]? _buffer;

        public FrameRead(byte[] buffer, int width, int height, int stride, FramePixelFormat format)
        {
            _buffer = buffer;
            Width = width;
            Height = height;
            Stride = stride;
            Format = format;
        }

        public int Width { get; }

        public int Height { get; }

        public int Stride { get; }

        public FramePixelFormat Format { get; }

        public ReadOnlyMemory<byte> Pixels => _buffer ?? throw new ObjectDisposedException(nameof(FrameRead));

        /// <summary>Hands the read buffer to a staged frame; Dispose then no-ops.</summary>
        public byte[] Detach() => Interlocked.Exchange(ref _buffer, null)
            ?? throw new ObjectDisposedException(nameof(FrameRead));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
            {
                FrameBuffers.Return(buffer);
            }
        }
    }

    /// <summary>
    /// EV-10b: one frame in the layout its surface renders, ready for a plain
    /// copy. It is produced entirely on the frame-loop task (read included) and
    /// owns a pooled buffer until the UI thread has copied it or a newer frame
    /// replaced it.
    /// </summary>
    private sealed class StagedFrame : IDisposable
    {
        private byte[]? _buffer;

        public StagedFrame(
            byte[] buffer,
            int width,
            int height,
            int stride,
            string surfaceFormat,
            PendingFrame source,
            double queueMs,
            double readMs,
            double stageMs)
        {
            _buffer = buffer;
            Width = width;
            Height = height;
            Stride = stride;
            SurfaceFormat = surfaceFormat;
            Source = source;
            QueueMs = queueMs;
            ReadMs = readMs;
            StageMs = stageMs;
            RowBytes = checked(width * ViewportPixelPolicy.BytesPerPixel(surfaceFormat));
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The staged row stride (equals the producer's row length).</summary>
        public int Stride { get; }

        public int RowBytes { get; }

        public string SurfaceFormat { get; }

        public PendingFrame Source { get; }

        public double QueueMs { get; }

        public double ReadMs { get; }

        public double StageMs { get; }

        /// <summary>UI-thread TryLock retries before this frame was presented.</summary>
        public int LockRetries { get; set; }

        public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(StagedFrame));

        public ReadOnlySpan<byte> Pixels => _buffer ?? throw new ObjectDisposedException(nameof(StagedFrame));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
            {
                FrameBuffers.Return(buffer);
            }
        }
    }
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

    /// <summary>
    /// EV-07: the last pair the supervisor reported, plus the pair the record's
    /// image package already names. A stopped guest's status carries no
    /// <c>abi</c>/<c>sdkLevel</c>, so the identity row falls back to these
    /// instead of going blank.
    /// </summary>
    private string _cachedAbi = "";
    private string _cachedSdkLevel = "";

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
    /// UX-04b: "x86_64 · 35" for the inspector's ABI / API row. EV-07: the row
    /// reads the current report first, then the cached pair, and finally one dash
    /// - never an empty string - so a stopped guest still names its ABI and API.
    /// </summary>
    public string AbiApi =>
        InspectorSessionRows.AbiApi(Abi, SdkLevel, _cachedAbi, _cachedSdkLevel);

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

        // EV-07: the image package names the ABI and API before the guest ever
        // runs, so the identity row has a cached pair for a stopped guest.
        var image = ImageNaming.ParseImage(device.ImagePackage);
        device._cachedAbi = image.Abi;
        device._cachedSdkLevel = image.Api > 0
            ? image.Api.ToString(CultureInfo.InvariantCulture)
            : "";
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
        var abi = status["abi"]?.GetValue<string>() ?? "";
        var sdkLevel = status["sdkLevel"]?.GetValue<string>() ?? "";
        if (abi.Trim().Length > 0)
        {
            _cachedAbi = abi.Trim();
        }

        if (sdkLevel.Trim().Length > 0)
        {
            _cachedSdkLevel = sdkLevel.Trim();
        }

        Abi = abi;
        SdkLevel = sdkLevel;
        LastError = status["lastError"]?.GetValue<string>() ?? "";
        if (status["readiness"] is JsonObject readiness)
        {
            Readiness = readiness;
        }

        State = status["state"]?.GetValue<string>() ?? State;

        // EV-06: readiness is a session-only measurement. A guest that is no
        // longer Running or Starting keeps no boot figure from the run it was
        // in, so the next run cannot show the previous run's number.
        if (!InspectorSessionRows.IsReported(Lifecycle.Phase))
        {
            Readiness = null;
        }

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
