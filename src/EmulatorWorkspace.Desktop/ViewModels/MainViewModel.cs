using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private readonly List<double> _frameIntervals = new();
    private readonly List<double> _inputLatencies = new();
    private readonly Channel<PendingFrame> _frames = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly SemaphoreSlim _subscribeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private SupervisorConnection? _connection;
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
    private bool _statusPollInFlight;
    private bool _frameStale;
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

    private readonly bool _halfCapture;

    public MainViewModel(bool halfCapture = false)
    {
        _halfCapture = halfCapture;
        _paths = WorkspacePaths.Resolve();
        _dispatcher = Application.Current.Dispatcher;

        PrimaryActionCommand = new AsyncRelayCommand(PrimaryActionAsync, () => !IsBusy);
        CreateDeviceCommand = new AsyncRelayCommand(CreateAndStartAsync, () => !IsBusy && !HasBlockingIssue);
        StartCommand = new AsyncRelayCommand(() => StartDeviceAsync(), () => SelectedDevice is not null && !IsBusy);
        StopCommand = new AsyncRelayCommand(() => StopDeviceAsync(), () => SelectedDevice?.IsRunning == true && !IsBusy);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => SelectedDevice is not null && !IsBusy);
        ColdBootCommand = new AsyncRelayCommand(ColdBootAsync, () => SelectedDevice is not null && !IsBusy);
        BackCommand = new AsyncRelayCommand(() => KeyAsync("BACK"), () => CanSendInput);
        HomeCommand = new AsyncRelayCommand(() => KeyAsync("HOME"), () => CanSendInput);
        RecentsCommand = new AsyncRelayCommand(() => KeyAsync("RECENTS"), () => CanSendInput);
        ScreenshotCommand = new AsyncRelayCommand(ScreenshotAsync, () => SelectedDevice?.IsRunning == true);
        InstallApkCommand = new AsyncRelayCommand(InstallApkDialogAsync, () => SelectedDevice?.IsRunning == true && !IsBusy);
        BuildApkSetCommand = new AsyncRelayCommand(BuildApkSetDialogAsync, () => IsConnected && !IsBusy);
        LaunchSampleCommand = new AsyncRelayCommand(
            () => LaunchAsync("dev.emulatorworkspace.sample"),
            () => SelectedDevice?.IsRunning == true);
        RotateCommand = new AsyncRelayCommand(ToggleRotationAsync, () => CanSendInput);
        RefreshCommand = new AsyncRelayCommand(InitializeAsync);
        ToggleLogcatCommand = new RelayCommand(() => IsLogcatOpen = !IsLogcatOpen);
        ClearLogCommand = new RelayCommand(ClearLog);
        ExportLogCommand = new AsyncRelayCommand(ExportLogAsync);
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

        Palette.Add(new PaletteEntryViewModel("Create device", "Create a new managed guest", CreateDeviceCommand));
        Palette.Add(new PaletteEntryViewModel("Device library", "Browse, search and manage the device library", OpenLibraryCommand));
        Palette.Add(new PaletteEntryViewModel("Start device", "Start the selected guest", StartCommand));
        Palette.Add(new PaletteEntryViewModel("Stop device", "Stop the selected guest", StopCommand));
        Palette.Add(new PaletteEntryViewModel("Restart device", "Restart the selected guest", RestartCommand));
        Palette.Add(new PaletteEntryViewModel("Cold boot", "Cold boot the selected guest", ColdBootCommand));
        Palette.Add(new PaletteEntryViewModel("Install APK", "Install an APK on the selected guest", InstallApkCommand));
        Palette.Add(new PaletteEntryViewModel("Build APK set", "Convert an Android App Bundle using test signing", BuildApkSetCommand));
        Palette.Add(new PaletteEntryViewModel("Launch sample", "Launch the sample application", LaunchSampleCommand));
        Palette.Add(new PaletteEntryViewModel("Screenshot", "Capture a PNG of the selected guest", ScreenshotCommand));
        Palette.Add(new PaletteEntryViewModel("Toggle Logcat", "Show or hide the Logcat drawer", ToggleLogcatCommand));
        Palette.Add(new PaletteEntryViewModel("ADB root", "Run the guest ADB daemon as root", AdbRootCommand));
        Palette.Add(new PaletteEntryViewModel("ADB unroot", "Return the guest ADB daemon to the shell user", AdbUnrootCommand));
        Palette.Add(new PaletteEntryViewModel("Refresh ADB state", "Re-read the ADB daemon identity", RefreshAdbStateCommand));
        Palette.Add(new PaletteEntryViewModel("Export diagnostics", "Write the diagnostic bundle", CopyDiagnosticsCommand));

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshStatusAsync(), _dispatcher);
        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => DrainLog(), _dispatcher);
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
    /// "warning" when the stream stalled on its own, "info" when a supervised
    /// operation that owns the guest's ADB channel is running. The tone only
    /// styles the chip; both sentences say what happened and what is blocked.
    /// </summary>
    public string StaleFrameTone => _frameHold.Length > 0 ? "info" : "warning";

    public string StaleFrameTitle
    {
        get
        {
            if (!_frameStale)
            {
                return "";
            }

            if (_frameHold.Length > 0)
            {
                return $"Paused while {_frameHold} runs";
            }

            var age = _lastFramePresented == default
                ? 0
                : Math.Max(0, (DateTimeOffset.UtcNow - _lastFramePresented).TotalSeconds);
            return $"Frame stalled · last frame {age:N0} s ago";
        }
    }

    /// <summary>
    /// Kept short and split from the title so the chip wraps instead of clipping
    /// at the narrow layouts (1024 px) the design matrix requires.
    /// </summary>
    public string StaleFrameDetail => _frameStale
        ? "The frame stream resumes with the next frame; input is paused until then."
        : "";

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
    public string AdbUidValue => AdbUid is "" or "n/a" ? "—" : $"uid {AdbUid}";

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

    public bool CanSendInput => SelectedDevice?.IsRunning == true;

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
        set => SetProperty(ref _statusMessage, value);
    }

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
                if (!HasFrame)
                {
                    OnPropertyChanged(nameof(FrameInfo));
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
                OnPropertyChanged(nameof(LogcatDrawerHeight));
                if (value)
                {
                    _ = EnsureLogcatAsync();
                }
                else
                {
                    _ = StopLogcatAsync();
                }
            }
        }
    }

    public GridLength LogcatDrawerHeight => IsLogcatOpen ? new GridLength(230) : new GridLength(0);

    private string _logSearch = "";

    public string LogSearch
    {
        get => _logSearch;
        set
        {
            if (SetProperty(ref _logSearch, value))
            {
                LogView.Refresh();
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
            }
        }
    }

    private string _logLevel = "All";

    public string LogLevel
    {
        get => _logLevel;
        set
        {
            if (SetProperty(ref _logLevel, value))
            {
                LogView.Refresh();
            }
        }
    }

    public string[] LogLevels { get; } = { "All", "V", "D", "I", "W", "E" };

    private bool _logPaused;

    public bool LogPaused
    {
        get => _logPaused;
        set => SetProperty(ref _logPaused, value);
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

    public string PrimaryActionText => SelectedDevice switch
    {
        null => "Create device",
        { IsRunning: true } => "Stop",
        { State: "Starting" or "Stopping" } => "Working...",
        _ => "Start",
    };

    public async Task InitializeAsync()
    {
        IsBusy = true;
        BusyText = "Connecting to the local supervisor...";
        ClearError();

        try
        {
            _connection ??= await SupervisorConnection.ConnectAsync(_paths.Root, TimeSpan.FromSeconds(25))
                .ConfigureAwait(true);
            _connection.EventReceived -= OnSupervisorEvent;
            _connection.EventReceived += OnSupervisorEvent;
            _connection.Trace = DesktopLog.Write;

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
                DoctorText = "No supervisor report available. The pinned image and emulator are never substituted automatically.";
            }
            else
            {
                SetupSummary = "The pinned Android SDK could not be used. Fix the items below, then choose Refresh.";
                SetupIssues.Add("Android SDK incomplete or missing: " + ex.Message);
                SetupIssues.Add("Install cmdline-tools, platform-tools, emulator 37.2.12, and system-images;android-35;default;x86_64 revision 2 from the official SDK Manager.");
                SetupIssues.Add("Accept the SDK licenses, set ANDROID_HOME to that SDK, then restart the workspace supervisor.");
                DoctorText = "No SDK report available. The pinned image and emulator are never substituted automatically.";
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            UpdateStage();
        }
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
        DoctorText = string.Join(Environment.NewLine, new[]
        {
            $"SDK: {sdk["root"]}",
            $"Emulator: {emulatorVersion} build {emulatorBuild ?? "unknown"}",
            $"Image: {pinned} rev {pinnedImage?["revision"] ?? "missing"}{systemImageSize}",
            $"ADB: {sdk["adbVersion"]}",
            $"Acceleration: {acceleration}",
            $"GPU: {gpu["name"]} driver {gpu["driverVersion"]}",
            $"Workspace: {doctor["workspaceRoot"]} ({freeGiB:N1} GiB free)",
        });
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

            if (device.IsRunning)
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

            device.ApplyStatus(status);
            ApplyBootEvidence(status);
            UpdateFrameWait(device);
            UpdateFrameHealth();
            UpdateStage();
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(ReadinessText));
            OnPropertyChanged(nameof(BootValueText));
            OnPropertyChanged(nameof(BootTooltip));
            OnPropertyChanged(nameof(AbiApiText));
            if (device.State == "Running" && _frameSubscription is null)
            {
                await SubscribeFramesAsync(device).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Status refresh failed: " + ex.Message;
        }
        finally
        {
            _statusPollInFlight = false;
        }
    }

    private async Task PrimaryActionAsync()
    {
        if (SelectedDevice is null || !SelectedDevice.IsRunning)
        {
            if (SelectedDevice is null)
            {
                await CreateAndStartAsync().ConfigureAwait(true);
            }
            else
            {
                await StartDeviceAsync().ConfigureAwait(true);
            }

            return;
        }

        await StopDeviceAsync().ConfigureAwait(true);
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
        if (SelectedDevice is not { } device || !device.IsRunning)
        {
            return;
        }

        if (IsFrameStale)
        {
            // UX-03: a stalled frame must not accept coordinates. The mapping is
            // unchanged; only the freshness gate is new.
            StatusMessage = "Input paused: " + StaleFrameTitle + ". " + StaleFrameDetail;
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
        if (SelectedDevice is not { } device || !device.IsRunning)
        {
            return;
        }

        if (IsFrameStale)
        {
            StatusMessage = "Input paused: " + StaleFrameTitle + ". " + StaleFrameDetail;
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
        ThemeManager.Apply(ThemeManager.IsDark ? Services.ThemeMode.Light : Services.ThemeMode.Dark);
    }

    private void ClearLog()
    {
        LogEntries.Clear();
        _logInbox.Clear();
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
            // supervisor reports as Running. Starting one while Android boots
            // holds the guest's ADB channel, which starves the status reads the
            // boot stepper and the state pill depend on.
            if (_frameSubscription is not null || device.State != "Running" || SelectedDevice?.Name != device.Name)
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
            if (device.State == "Starting")
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
        if (_logcatSubscription is not null || SelectedDevice is null || !SelectedDevice.IsRunning)
        {
            return;
        }

        try
        {
            var data = await RequestAsync(
                OperationNames.LogcatSubscribe,
                new JsonObject { ["name"] = SelectedDevice.Name },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            Volatile.Write(ref _logcatSubscription, data["subscription"]!.GetValue<string>());
        }
        catch (Exception ex)
        {
            ShowError(ex, "logcat.subscribe", SelectedDevice?.Name);
        }
    }

    private async Task StopLogcatAsync()
    {
        if (_logcatSubscription is null)
        {
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
                    && envelope.Data is JsonObject entry && !LogPaused)
                {
                    _logInbox.Enqueue(LogEntryViewModel.From(entry));
                }

                break;

            case EventNames.SubscriptionClosed:
                _dispatcher.BeginInvoke(() =>
                {
                    var reason = envelope.Data?["reason"]?.GetValue<string>() ?? "closed";
                    StatusMessage = "Subscription closed: " + reason;
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
            LogEntries.Add(entry);
            added++;
        }

        while (LogEntries.Count > 2500)
        {
            LogEntries.RemoveAt(0);
        }
    }

    private bool FilterLog(object item)
    {
        if (item is not LogEntryViewModel entry)
        {
            return false;
        }

        if (LogLevel != "All")
        {
            var order = "VDIWEA";
            var min = order.IndexOf(LogLevel[0]);
            var current = entry.Level.Length > 0 ? order.IndexOf(entry.Level[0]) : 0;
            if (min >= 0 && (current < min || current < 0))
            {
                return false;
            }
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

        if (device.State == "Failed")
        {
            SetStage("error", "Guest failed",
                string.IsNullOrWhiteSpace(device.LastError)
                    ? "The supervisor reported a failed state for this guest."
                    : device.LastError,
                "Inspect Diagnostics and the instance log, then retry. A cold boot is only safe once the cause is known.");
            ShowBootStages = true;
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

        if (device.IsRunning && HasFrame)
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

        if (device.State == "Starting")
        {
            var elapsed = ElapsedSinceLaunch();
            SetStage("info", "Starting Android",
                string.IsNullOrEmpty(elapsed)
                    ? "The engine is running. Waiting for the supervisor's boot stages."
                    : $"The engine is running. Elapsed since launch: {elapsed}.",
                "");
            ShowBootStages = true;
            return;
        }

        if (device.State == "Stopping")
        {
            SetStage("info", "Stopping the guest",
                "The supervisor is shutting the engine down. The window can stay open; the guest is not tied to it.",
                "");
            ShowBootStages = false;
            return;
        }

        if (device.IsRunning)
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
    }

    /// <summary>
    /// Maps instance.status evidence onto the three boot steps. A stage the
    /// supervisor cannot report yet is worded "not reported", never "done".
    /// </summary>
    private void ApplyBootEvidence(JsonObject status)
    {
        var state = status["state"]?.GetValue<string>() ?? "";
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

        var booting = state is "Starting" or "Running";

        BootStages[0].Set(
            engineAlive ? "done" : state == "Starting" ? "active" : "pending",
            engineAlive ? "process alive" : state == "Starting" ? "starting process" : "not started");
        BootStages[1].Set(
            bootCompleted ? "done" : adbOnline ? "active" : state == "Starting" ? "active" : booting ? "unknown" : "pending",
            bootCompleted ? "boot_completed reported"
                : adbOnline ? "ADB transport online"
                : state == "Starting" ? (adbState == "offline" ? "ADB transport offline" : "waiting for ADB")
                : booting ? "not reported" : "not started");
        BootStages[2].Set(
            interactive ? "done" : bootCompleted ? "active" : "pending",
            interactive ? "launcher interactive"
                : bootCompleted ? "waiting for the launcher"
                : "not reached");
    }

    private void ResetBootStages()
    {
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
    /// </summary>
    private void UpdateFrameHealth()
    {
        var running = SelectedDevice?.IsRunning == true;
        var stale = running && HasFrame && _lastFramePresented != default
                       && (DateTimeOffset.UtcNow - _lastFramePresented).TotalSeconds > StaleFrameSeconds;
        IsFrameStale = stale;
        if (stale)
        {
            // The age is part of the sentence, so refresh it while it holds.
            OnPropertyChanged(nameof(StaleFrameTitle));
            OnPropertyChanged(nameof(StaleFrameDetail));
            OnPropertyChanged(nameof(StaleFrameTone));
        }
    }

    /// <summary>
    /// One-second presentation tick: refresh only the sentences that contain a
    /// timestamp. It cannot promote a stage, clear an error or declare readiness.
    /// </summary>
    private void TickClock()
    {
        if (StageVisible && SelectedDevice is { } device
            && (device.State == "Starting" || (device.IsRunning && !HasFrame)))
        {
            UpdateStage();
        }

        if (IsFrameStale)
        {
            OnPropertyChanged(nameof(StaleFrameTitle));
        }
    }

    private string ElapsedSinceLaunch()
    {
        if (_launchedUtc is not { } launched)
        {
            return "";
        }

        var seconds = Math.Max(0, (DateTimeOffset.UtcNow - launched).TotalSeconds);
        return $"{seconds:N0}s";
    }

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

        if (device.State != "Running")
        {
            DropAccepted = false;
            DropTitle = device.State == "Starting"
                ? $"{device.Name} is still starting"
                : $"Start {device.Name} to install";
            DropDetail = device.State == "Starting"
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
        ToastTitle = title;
        ToastText = text;
        _toastPath = path;
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
        OnPropertyChanged(nameof(AdbUidTooltip));
        OnPropertyChanged(nameof(AdbRootCapable));
        OnPropertyChanged(nameof(AdbBuildType));
        OnPropertyChanged(nameof(AdbTransportId));
        OnPropertyChanged(nameof(AdbRootState));
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
        catch (SupervisorRequestException ex)
        {
            DesktopLog.Write($"Magisk lab state unavailable: {ex.Message}");
        }
    }

    private void ApplyMagiskStatus(DeviceItemViewModel device, JsonObject lab)
    {
        if (SelectedDevice?.Name != device.Name)
        {
            return;
        }

        var phase = lab["phase"]?.GetValue<string>() ?? "unknown";
        var recoveryRequired = lab["recoveryRequired"]?.GetValue<bool>() == true;
        MagiskState = "Magisk lab: " + phase + (recoveryRequired ? " (recovery required)" : "");
        _labRecoveryRequired = recoveryRequired;
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
        if (MessageBox.Show($"Install the base-only Magisk lab on {device.Name}?\n\n" +
            "This permits temporary ADB root for provisioning, then returns adbd to UID 2000. " +
            "While patched, host ADB shell can obtain Magisk root. Agents have observation access only. " +
            "Zygisk stays off; no modules are installed. Stock and guest-data backups are preserved. " +
            "Two cold boots verify app root. Snapshots are fenced for this generation.\n\n" +
            $"Ramdisk: {patch}\nManager: {apk}\nProbe: {probe}", "Approve Magisk lab installation",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
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
            if (MessageBox.Show($"Restore stock boot for {device.Name}?\n\n" +
                "The supervisor can stop an unbootable guest from Windows. It preserves current guest data " +
                "when possible; if stock cannot boot, it restores the verified pre-install data backup. " +
                "Failed overlays remain available as evidence.\n\nTransaction: " + state["transaction"],
                "Approve stock recovery", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await ApprovedMagiskAction(device.Name, state, "recover");
        });
    }

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
        catch (SupervisorUnavailableException)
        {
            // The pipe is gone. Drop the client so Refresh can reconnect instead
            // of reusing a dead stream forever.
            var dead = _connection;
            _connection = null;
            IsConnected = false;
            if (dead is not null)
            {
                _ = dead.DisposeAsync();
            }

            throw;
        }
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

    public int ConsolePort { get; init; }

    public int GrpcPort { get; init; }

    public string Gpu { get; init; } = "";

    private string _state = "Defined";

    public string State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(StateLabel));
            }
        }
    }

    public string StateLabel => State;

    public bool IsRunning => State is "Running" or "Starting";

    private string _foreground = "";

    public string Foreground
    {
        get => _foreground;
        private set => SetProperty(ref _foreground, value);
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
            }
        }
    }

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

    public static DeviceItemViewModel From(JsonObject node) => new()
    {
        Name = node["name"]!.GetValue<string>(),
        Serial = node["serial"]!.GetValue<string>(),
        ImagePackage = node["imagePackage"]?.GetValue<string>() ?? "",
        ConsolePort = node["consolePort"]?.GetValue<int>() ?? 0,
        GrpcPort = node["grpcPort"]?.GetValue<int>() ?? 0,
        Gpu = node["gpu"]?.GetValue<string>() ?? "",
        State = node["state"]?.GetValue<string>() ?? "Unknown",
    };

    public void ApplyStatus(JsonObject status)
    {
        State = status["state"]?.GetValue<string>() ?? State;
        Foreground = status["foreground"]?.GetValue<string>() ?? "";
        Abi = status["abi"]?.GetValue<string>() ?? "";
        SdkLevel = status["sdkLevel"]?.GetValue<string>() ?? "";
        LastError = status["lastError"]?.GetValue<string>() ?? "";
        if (status["readiness"] is JsonObject readiness)
        {
            Readiness = readiness;
        }
    }
}

/// <summary>One command palette entry.</summary>
public sealed class PaletteEntryViewModel
{
    public PaletteEntryViewModel(string label, string hint, System.Windows.Input.ICommand command)
    {
        Label = label;
        Hint = hint;
        Command = command;
    }

    public string Label { get; }

    public string Hint { get; }

    public System.Windows.Input.ICommand Command { get; }

    public void Invoke()
    {
        if (Command.CanExecute(null))
        {
            Command.Execute(null);
        }
    }
}

public sealed class LogEntryViewModel
{
    public required string Time { get; init; }

    public required string Level { get; init; }

    public required string Tag { get; init; }

    public required string Message { get; init; }

    public required string Raw { get; init; }

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
