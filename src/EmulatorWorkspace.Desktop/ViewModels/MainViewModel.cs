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

        LogView = CollectionViewSource.GetDefaultView(LogEntries);
        LogView.Filter = FilterLog;

        Palette.Add(new PaletteEntryViewModel("Create device", "Create a new managed guest", CreateDeviceCommand));
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

    /// <summary>Effective ADB daemon user of the selected guest ("n/a" when unknown).</summary>
    public string AdbUid { get; private set; } = "n/a";

    /// <summary>Whether the image of the selected guest can run adbd as root.</summary>
    public bool AdbRootCapable { get; private set; }

    public string AdbBuildType { get; private set; } = "";

    public string AdbTransportId { get; private set; } = "";

    public string AdbRootState => AdbRootCapable
        ? $"adb daemon uid {AdbUid} (build {AdbBuildType}, transport {AdbTransportId})"
        : $"adb daemon uid {AdbUid} - this image cannot run adbd as root (build {AdbBuildType})";

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
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

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
                return "not measured this session";
            }

            return $"boot {readiness["launchedToBootSeconds"]}s  -  interactive {readiness["launchedToInteractiveSeconds"]}s";
        }
    }

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

    private bool _isLogcatOpen = true;

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
        ErrorMessage = "";

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
            ErrorMessage = ex.Message;
            StatusMessage = "Supervisor or SDK unavailable.";
            ShowSetup = true;
            HasBlockingIssue = true;
            SetupSummary = "The pinned Android SDK could not be used. Fix the items below, then choose Refresh.";
            SetupIssues.Clear();
            SetupIssues.Add("Android SDK incomplete or missing: " + ex.Message);
            SetupIssues.Add("Install cmdline-tools, platform-tools, emulator 37.2.12, and system-images;android-35;default;x86_64 revision 2 from the official SDK Manager.");
            SetupIssues.Add("Accept the SDK licenses, set ANDROID_HOME to that SDK, then restart the workspace supervisor.");
            DoctorText = "No SDK report available. The pinned image and emulator are never substituted automatically.";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
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
        ErrorMessage = "";
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
            OnPropertyChanged(nameof(ReadinessText));
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
            ErrorMessage = ex.Message;
        }
    }

    private async Task RefreshStatusAsync()
    {
        DesktopLog.Write("refresh tick");
        if (SelectedDevice is null || _connection is null || IsBusy)
        {
            return;
        }

        try
        {
            var status = await RequestAsync(
                OperationNames.InstanceStatus,
                new JsonObject { ["name"] = SelectedDevice.Name },
                TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            SelectedDevice.ApplyStatus(status);
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(ReadinessText));
            OnPropertyChanged(nameof(AbiApiText));
            if (SelectedDevice.IsRunning && _frameSubscription is null)
            {
                await SubscribeFramesAsync(SelectedDevice).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Status refresh failed: " + ex.Message;
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
        if (SelectedDevice is null)
        {
            return;
        }

        await RunBusyAsync("Starting engine...", async () =>
        {
            await RequestAsync(
                OperationNames.InstanceStart,
                new JsonObject { ["name"] = SelectedDevice.Name, ["coldBoot"] = true },
                TimeSpan.FromMinutes(2)).ConfigureAwait(true);
            await RefreshDevicesAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

        await RunBusyAsync("Waiting for Android to reach interactive readiness...", async () =>
        {
            var readiness = await RequestAsync(
                OperationNames.InstanceWait,
                new JsonObject { ["name"] = SelectedDevice!.Name, ["timeoutSeconds"] = 600 },
                TimeSpan.FromMinutes(11)).ConfigureAwait(true);
            SelectedDevice!.Readiness = readiness;
            OnPropertyChanged(nameof(ReadinessText));
            var status = await RequestAsync(
                OperationNames.InstanceStatus,
                new JsonObject { ["name"] = SelectedDevice.Name },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            SelectedDevice.ApplyStatus(status);
            await SubscribeFramesAsync(SelectedDevice).ConfigureAwait(true);
            if (IsLogcatOpen)
            {
                await EnsureLogcatAsync().ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
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
        }).ConfigureAwait(true);
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
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            await RequestAsync(
                OperationNames.InputKey,
                new JsonObject { ["name"] = SelectedDevice.Name, ["key"] = key },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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
        if (SelectedDevice is null || !SelectedDevice.IsRunning)
        {
            return;
        }

        var baseline = SampleMarkerColor();
        _pendingInput = new PendingInput(DateTimeOffset.UtcNow, baseline);
        try
        {
            await RequestAsync(
                OperationNames.InputTap,
                new JsonObject { ["name"] = SelectedDevice.Name, ["x"] = x, ["y"] = y },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _pendingInput = null;
            ErrorMessage = "Input failed: " + ex.Message;
        }
    }

    public async Task SendSwipeAsync(int x1, int y1, int x2, int y2)
    {
        if (SelectedDevice is null || !SelectedDevice.IsRunning)
        {
            return;
        }

        try
        {
            await RequestAsync(
                OperationNames.InputSwipe,
                new JsonObject
                {
                    ["name"] = SelectedDevice.Name,
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
            ErrorMessage = "Swipe failed: " + ex.Message;
        }
    }

    private async Task ToggleRotationAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var next = SelectedDevice.Rotation == 1 ? 0 : 1;
        try
        {
            await RequestAsync(
                OperationNames.DisplayRotate,
                new JsonObject { ["name"] = SelectedDevice.Name, ["rotation"] = next },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            SelectedDevice.Rotation = next;
            StatusMessage = next == 1 ? "Rotation: landscape." : "Rotation: portrait.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task ScreenshotAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            var data = await RequestAsync(
                OperationNames.FrameSnapshot,
                new JsonObject { ["name"] = SelectedDevice.Name, ["width"] = 1080 },
                TimeSpan.FromSeconds(60)).ConfigureAwait(true);
            var bytes = Convert.FromBase64String(data["pngBase64"]!.GetValue<string>());
            var directory = Path.Combine(_paths.Root, "screenshots");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{SelectedDevice.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            ScreenshotPath = path;
            StatusMessage = "Screenshot saved: " + path;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Screenshot failed: " + ex.Message;
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
        }).ConfigureAwait(true);
    }

    private async Task LaunchAsync(string package)
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            await RequestAsync(
                OperationNames.AppLaunch,
                new JsonObject { ["name"] = SelectedDevice.Name, ["package"] = package },
                TimeSpan.FromSeconds(60)).ConfigureAwait(true);
            StatusMessage = "Launched " + package;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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
            ErrorMessage = "Could not copy diagnostics: " + ex.Message;
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
            if (_frameSubscription is not null || !device.IsRunning || SelectedDevice?.Name != device.Name)
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
            StartFrameLoop();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Frame stream unavailable: " + ex.Message;
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
            ErrorMessage = "Logcat unavailable: " + ex.Message;
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
        Frame = bitmap;
        var widthChanged = FrameWidth != bitmap.PixelWidth || FrameHeight != bitmap.PixelHeight;
        FrameWidth = bitmap.PixelWidth;
        FrameHeight = bitmap.PixelHeight;

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
        ErrorMessage = "";
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Operation failed.";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
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
            DesktopLog.Write($"ADB daemon state unavailable: {ex.Message}");
        }

        OnPropertyChanged(nameof(AdbUid));
        OnPropertyChanged(nameof(AdbRootCapable));
        OnPropertyChanged(nameof(AdbBuildType));
        OnPropertyChanged(nameof(AdbTransportId));
        OnPropertyChanged(nameof(AdbRootState));
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
        }

        await RefreshAdbStateAsync().ConfigureAwait(true);
    }

    private async Task<JsonObject> RequestAsync(string op, JsonObject? args, TimeSpan deadline)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("The supervisor is not connected.");
        }

        var response = await _connection.RequestAsync(op, args, deadline).ConfigureAwait(true);
        if (!response.Ok)
        {
            throw new SupervisorRequestException(response.Code, response.Message);
        }

        return response.Data as JsonObject ?? new JsonObject();
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
        private set => SetProperty(ref _abi, value);
    }

    private string _sdkLevel = "";

    public string SdkLevel
    {
        get => _sdkLevel;
        private set => SetProperty(ref _sdkLevel, value);
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
