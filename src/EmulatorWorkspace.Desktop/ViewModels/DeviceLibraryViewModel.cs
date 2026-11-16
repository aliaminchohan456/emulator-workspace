using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
using EmulatorWorkspace.Desktop.Services;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// UX-04 device library. Everything on this surface is a supervisor response:
/// <c>instance.list</c> for identity and state, <c>instance.profileExport</c>
/// for the configured profile, <c>doctor</c> for image/resource/disk
/// readiness, <c>instance.diskEstimate</c> for the create pre-flight,
/// <c>instance.create</c>, <c>instance.duplicate</c> and the <c>snapshot.*</c>
/// family for recovery points. A field the supervisor does not report is shown
/// as "not reported"; no value here is invented, and no control exists for an
/// operation the supervisor does not implement.
/// </summary>
public sealed class DeviceLibraryViewModel : ObservableObject, IAsyncDisposable
{
    private const string DesiredDataPartition = "2048M";
    private static readonly Regex InstanceName = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
    private static readonly Regex SnapshotTag = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly WorkspacePaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _stateTimer;
    private readonly List<LibraryCard> _cards = new();
    private readonly HashSet<string> _bootWaits = new(StringComparer.Ordinal);
    // One request at a time: the pipe client writes framed messages without an
    // internal write lock, so concurrent requests would interleave on the wire
    // and end the session. The library loads four profiles at once and they
    // queue here instead.
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private SupervisorConnection? _connection;
    private JsonObject? _doctor;
    private bool _refreshing;
    private bool _disposed;

    private string _searchText = "";
    // UX-05 fix pass (igr-feedback-ux04b item 3): the library opens on State,
    // which reads Running first, then the guests that need attention, then the
    // rest. There is no per-workspace settings store to persist a choice in, so
    // the default is the only persisted order.
    private string _sortKey = "State";
    private bool _isLoading;
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "Connecting to the supervisor...";
    private bool _isConnected;

    private string _errorTitle = "";
    private string _errorText = "";
    private string _errorAction = "";
    private string _errorDetails = "";

    private bool _isCreateOpen;
    private string _createName = "dev";
    private string _createNameError = "";
    private string _createBlockedReason = "";
    private string _createImageText = "not reported";
    private string _createImageTone = "info";
    private string _createWhpxText = "not reported";
    private string _createWhpxTone = "info";
    private string _createDiskReserveText = "not reported";
    private string _createDiskFreeText = "not reported";
    private string _createDiskTone = "info";
    private string _createDiskVerdict = "not reported";

    private bool _isCloneOpen;
    private string _cloneSource = "";
    private string _cloneSourceState = "";
    private string _cloneSourceImage = "";
    private string _cloneName = "";
    private string _cloneNameError = "";
    private string _cloneBlockedReason = "";
    private string _cloneNote = "";

    private bool _isRecoveryOpen;
    private string _recoveryDevice = "";
    private string _recoverySubtitle = "";
    private string _recoveryMessage = "";
    private bool _isRecoveryBusy;
    private string _savePointTag = "";
    private string _savePointError = "";
    private string _savePointHint = "";
    private bool _canSavePoint;

    private bool _isConfirmOpen;
    private string _confirmTitle = "";
    private string _confirmText = "";
    private string _confirmActionText = "Continue";
    private Func<Task>? _confirmAction;

    public DeviceLibraryViewModel()
    {
        _paths = WorkspacePaths.Resolve();
        _dispatcher = Application.Current.Dispatcher;

        SortOptions.Add("Name");
        SortOptions.Add("State");
        SortOptions.Add("RAM");
        SortOptions.Add("API level");

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(loadProfiles: true), () => !IsBusy);
        NewDeviceCommand = new AsyncRelayCommand(OpenCreateAsync, () => !IsBusy);
        CloseCreateCommand = new RelayCommand(CloseCreate);
        SubmitCreateCommand = new AsyncRelayCommand(CreateAsync, () => CanCreate && !IsBusy);
        CloseCloneCommand = new RelayCommand(CloseClone);
        SubmitCloneCommand = new AsyncRelayCommand(CloneAsync, () => CanClone && !IsBusy);
        CloseRecoveryCommand = new RelayCommand(CloseRecovery);
        SavePointCommand = new AsyncRelayCommand(SavePointAsync, () => CanSavePoint && !IsBusy);
        CancelConfirmCommand = new RelayCommand(CloseConfirm);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => !IsBusy);
        DismissErrorCommand = new RelayCommand(ClearError);
        CopyErrorDetailsCommand = new RelayCommand(CopyErrorDetails);
        OpenStudioCommand = new RelayCommand(() => CloseRequested?.Invoke());
        ClearSearchCommand = new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);

        _stateTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshStatesAsync(), _dispatcher);
    }

    /// <summary>Raised when the operator asks to open a guest in the Studio window.</summary>
    public event Action<string>? OpenInStudioRequested;

    /// <summary>Raised by the navigation rail's Studio item.</summary>
    public event Action? CloseRequested;

    public ObservableCollection<LibraryCard> Tiles { get; } = new();

    public ObservableCollection<RecoveryPoint> RecoveryPoints { get; } = new();

    public ObservableCollection<string> SortOptions { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand NewDeviceCommand { get; }

    public RelayCommand CloseCreateCommand { get; }

    public AsyncRelayCommand SubmitCreateCommand { get; }

    public RelayCommand CloseCloneCommand { get; }

    public AsyncRelayCommand SubmitCloneCommand { get; }

    public RelayCommand CloseRecoveryCommand { get; }

    public AsyncRelayCommand SavePointCommand { get; }

    public RelayCommand CancelConfirmCommand { get; }

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand DismissErrorCommand { get; }

    public RelayCommand CopyErrorDetailsCommand { get; }

    public RelayCommand OpenStudioCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    // ------------------------------------------------------------ page state

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowNoMatchState));
                OnPropertyChanged(nameof(NoMatchText));
                OnPropertyChanged(nameof(NoMatchTitle));
                ApplyFilterAndSort();
            }
        }
    }

    public string SortKey
    {
        get => _sortKey;
        set
        {
            if (SetProperty(ref _sortKey, value ?? "State"))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowNoMatchState));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(CanCreate));
                OnPropertyChanged(nameof(CanClone));
                OnPropertyChanged(nameof(CanSavePoint));
                foreach (var card in _cards)
                {
                    card.RaiseCommandStates();
                }
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionText));
            }
        }
    }

    public string ConnectionText => IsConnected ? "Supervisor connected" : "Supervisor unavailable";

    public string WorkspaceRoot => _paths.Root;

    public string InstanceCountText => _cards.Count.ToString(CultureInfo.InvariantCulture);

    public string RunningCountText =>
        _cards.Count(c => c.IsRunning).ToString(CultureInfo.InvariantCulture);

    public string RunningOfText => $"of {_cards.Count}";

    public string RamText
    {
        get
        {
            if (_cards.Count == 0)
            {
                return "0 GB";
            }

            var known = _cards.Where(c => c.RamMb > 0).ToList();
            if (known.Count == 0)
            {
                return "not reported";
            }

            var totalMb = known.Sum(c => c.RamMb);
            return $"{totalMb / 1024d:N1} GB";
        }
    }

    public string RamOfText
    {
        get
        {
            var host = _doctor?["totalMemoryGiB"]?.GetValue<double>();
            var known = _cards.Count(c => c.RamMb > 0);
            var hostText = host is > 0 ? $"of {host:N0} GB host" : "host total not reported";
            return $"{hostText} · {known} profile{(known == 1 ? "" : "s")}";
        }
    }

    /// <summary>
    /// UX-18: the guests that need attention are the ones the lifecycle says
    /// failed or lost their engine - the same two states the card's chip and
    /// its sort order use.
    /// </summary>
    public string AttentionCountText =>
        _cards.Count(NeedsAttention).ToString(CultureInfo.InvariantCulture);

    public string AttentionText
    {
        get
        {
            var failed = _cards.Count(NeedsAttention);
            return failed == 1 ? "guest to review" : "guests to review";
        }
    }

    private static bool NeedsAttention(LibraryCard card) =>
        card.Lifecycle.Phase is DevicePhase.Failed or DevicePhase.EngineGone;

    /// <summary>
    /// Header count, printed inline after the page title as a mono text3 suffix
    /// ("Devices  3", item 5): the unfiltered total, or "shown of total" while a
    /// search narrows the grid. Both numbers come from the supervisor's
    /// instance.list, never from a literal.
    /// </summary>
    public string CardCountText
    {
        get
        {
            var total = _cards.Count;
            var shown = Tiles.Count(tile => !tile.IsPlaceholder);
            return shown == total
                ? total.ToString(CultureInfo.InvariantCulture)
                : $"{shown} of {total}";
        }
    }

    public bool ShowEmptyState => !IsLoading && _cards.Count == 0;

    public bool ShowNoMatchState =>
        !IsLoading && _cards.Count > 0 && Tiles.Count(tile => !tile.IsPlaceholder) == 0;

    public string NoMatchText =>
        "Search matches the name, image, ABI, API level and serial. Clear the search to see every managed device.";

    public string NoMatchTitle => $"No device matches “{SearchText.Trim()}”";

    public bool HasError => _errorText.Length > 0;

    public string ErrorTitle
    {
        get => _errorTitle;
        private set => SetProperty(ref _errorTitle, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string ErrorAction
    {
        get => _errorAction;
        private set => SetProperty(ref _errorAction, value);
    }

    private string ErrorDetails
    {
        get => _errorDetails;
        set => SetProperty(ref _errorDetails, value);
    }

    // ---------------------------------------------------------- create flow

    public bool IsCreateOpen
    {
        get => _isCreateOpen;
        private set => SetProperty(ref _isCreateOpen, value);
    }

    public string CreateName
    {
        get => _createName;
        set
        {
            if (SetProperty(ref _createName, value ?? ""))
            {
                CreateNameError = ValidateName(_createName);
                OnPropertyChanged(nameof(CanCreate));
                SubmitCreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CreateNameError
    {
        get => _createNameError;
        private set
        {
            if (SetProperty(ref _createNameError, value))
            {
                OnPropertyChanged(nameof(HasCreateNameError));
            }
        }
    }

    public bool HasCreateNameError => CreateNameError.Length > 0;

    public string CreateBlockedReason
    {
        get => _createBlockedReason;
        private set
        {
            if (SetProperty(ref _createBlockedReason, value))
            {
                OnPropertyChanged(nameof(CanCreate));
            }
        }
    }

    public string CreateImageText
    {
        get => _createImageText;
        private set => SetProperty(ref _createImageText, value);
    }

    public string CreateImageTone
    {
        get => _createImageTone;
        private set => SetProperty(ref _createImageTone, value);
    }

    public string CreateWhpxText
    {
        get => _createWhpxText;
        private set => SetProperty(ref _createWhpxText, value);
    }

    public string CreateWhpxTone
    {
        get => _createWhpxTone;
        private set
        {
            if (SetProperty(ref _createWhpxTone, value))
            {
                OnPropertyChanged(nameof(CreateWhpxChip));
                OnPropertyChanged(nameof(CreateWhpxChipTone));
            }
        }
    }

    /// <summary>
    /// Item 7: the acceleration row is a short chip ("WHPX ready") whose tone
    /// comes from the same readiness the raw sentence reports; the raw sentence
    /// stays in the tooltip, so nothing is lost and nothing is invented.
    /// </summary>
    public string CreateWhpxChip => CreateWhpxTone switch
    {
        "ok" => "WHPX ready",
        "error" => "WHPX unavailable",
        _ => "Not reported",
    };

    public string CreateWhpxChipTone => CreateWhpxTone switch
    {
        "ok" => "ok",
        "error" => "error",
        _ => "warning",
    };

    public string CreateDiskReserveText
    {
        get => _createDiskReserveText;
        private set => SetProperty(ref _createDiskReserveText, value);
    }

    public string CreateDiskFreeText
    {
        get => _createDiskFreeText;
        private set => SetProperty(ref _createDiskFreeText, value);
    }

    public string CreateDiskTone
    {
        get => _createDiskTone;
        private set
        {
            if (SetProperty(ref _createDiskTone, value))
            {
                OnPropertyChanged(nameof(CreateDiskChip));
                OnPropertyChanged(nameof(CreateDiskChipTone));
            }
        }
    }

    /// <summary>
    /// Item 7: the disk verdict is a chip too ("Enough space"); the supervisor's
    /// own wording stays in the tooltip so the two never disagree.
    /// </summary>
    public string CreateDiskChip => CreateDiskTone switch
    {
        "ok" => "Enough space",
        "error" => "Not enough space",
        _ => "Not checked",
    };

    public string CreateDiskChipTone => CreateDiskTone switch
    {
        "ok" => "ok",
        "error" => "error",
        _ => "warning",
    };

    public string CreateDiskVerdict
    {
        get => _createDiskVerdict;
        private set => SetProperty(ref _createDiskVerdict, value);
    }

    /// <summary>
    /// The fields <c>instance.create</c> accepts are name, image, device, gpu,
    /// headless and the two ports. Resolution, RAM and core count from
    /// <c>instance.presets</c> are reference values the create path cannot
    /// apply, so they are stated rather than offered as controls.
    /// </summary>
    public string CreateProfileText => "pixel_6 (the supervisor default profile)";

    /// <summary>
    /// Item 8: one plain sentence in the drawer. The technical detail (which
    /// operation accepts which field) lives in <see cref="CreateNoteDetail"/> and
    /// is shown only from the info glyph's tooltip, so the form is not a wall of
    /// API prose.
    /// </summary>
    public string CreateNote => "Resolution, RAM and cores come from the device profile.";

    public string CreateNoteDetail =>
        "instance.create accepts a name, the pinned image, a device profile, the GPU mode and headless mode. "
        + "The resolution, RAM and core count in instance.presets cannot be applied by that operation, so they are "
        + "not offered as controls here; the AVD is built from the device profile.";

    public bool CanCreate =>
        !IsBusy
        && IsConnected
        && CreateNameError.Length == 0
        && CreateBlockedReason.Length == 0
        && CreateName.Trim().Length > 0;

    // ----------------------------------------------------------- clone flow

    public bool IsCloneOpen
    {
        get => _isCloneOpen;
        private set => SetProperty(ref _isCloneOpen, value);
    }

    public string CloneSource
    {
        get => _cloneSource;
        private set => SetProperty(ref _cloneSource, value);
    }

    public string CloneSourceState
    {
        get => _cloneSourceState;
        private set => SetProperty(ref _cloneSourceState, value);
    }

    public string CloneSourceImage
    {
        get => _cloneSourceImage;
        private set => SetProperty(ref _cloneSourceImage, value);
    }

    public string CloneName
    {
        get => _cloneName;
        set
        {
            if (SetProperty(ref _cloneName, value ?? ""))
            {
                CloneNameError = ValidateName(_cloneName);
                OnPropertyChanged(nameof(CanClone));
                SubmitCloneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CloneNameError
    {
        get => _cloneNameError;
        private set
        {
            if (SetProperty(ref _cloneNameError, value))
            {
                OnPropertyChanged(nameof(HasCloneNameError));
            }
        }
    }

    public bool HasCloneNameError => CloneNameError.Length > 0;

    public string CloneBlockedReason
    {
        get => _cloneBlockedReason;
        private set
        {
            if (SetProperty(ref _cloneBlockedReason, value))
            {
                // The blocked reason is part of CanClone, so the primary action
                // must re-query or it stays accent-bright while disabled.
                OnPropertyChanged(nameof(CanClone));
                SubmitCloneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanClone =>
        !IsBusy && IsConnected && CloneNameError.Length == 0 && CloneBlockedReason.Length == 0;

    public string CloneNote
    {
        get => _cloneNote;
        private set => SetProperty(ref _cloneNote, value);
    }

    private string _cloneSourceName = "";

    // -------------------------------------------------------- recovery flow

    public bool IsRecoveryOpen
    {
        get => _isRecoveryOpen;
        private set => SetProperty(ref _isRecoveryOpen, value);
    }

    public string RecoveryDevice
    {
        get => _recoveryDevice;
        private set => SetProperty(ref _recoveryDevice, value);
    }

    public string RecoverySubtitle
    {
        get => _recoverySubtitle;
        private set => SetProperty(ref _recoverySubtitle, value);
    }

    public string RecoveryMessage
    {
        get => _recoveryMessage;
        private set
        {
            if (SetProperty(ref _recoveryMessage, value))
            {
                OnPropertyChanged(nameof(HasRecoveryMessage));
            }
        }
    }

    public bool HasRecoveryMessage => RecoveryMessage.Length > 0;

    public bool IsRecoveryBusy
    {
        get => _isRecoveryBusy;
        private set
        {
            if (SetProperty(ref _isRecoveryBusy, value))
            {
                OnPropertyChanged(nameof(ShowRecoveryEmpty));
            }
        }
    }

    public bool HasRecoveryPoints => RecoveryPoints.Count > 0;

    public bool ShowRecoveryEmpty => !IsRecoveryBusy && RecoveryPoints.Count == 0;

    public string SavePointTag
    {
        get => _savePointTag;
        set
        {
            if (SetProperty(ref _savePointTag, value ?? ""))
            {
                SavePointError = ValidateTag(_savePointTag);
                OnPropertyChanged(nameof(CanSavePoint));
                SavePointCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SavePointError
    {
        get => _savePointError;
        private set
        {
            if (SetProperty(ref _savePointError, value))
            {
                OnPropertyChanged(nameof(HasSavePointError));
            }
        }
    }

    public bool HasSavePointError => SavePointError.Length > 0;

    public string SavePointHint
    {
        get => _savePointHint;
        private set => SetProperty(ref _savePointHint, value);
    }

    public bool CanSavePoint
    {
        get => _canSavePoint;
        private set
        {
            if (SetProperty(ref _canSavePoint, value))
            {
                SavePointCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // --------------------------------------------------------- confirmation

    public bool IsConfirmOpen
    {
        get => _isConfirmOpen;
        private set => SetProperty(ref _isConfirmOpen, value);
    }

    public string ConfirmTitle
    {
        get => _confirmTitle;
        private set => SetProperty(ref _confirmTitle, value);
    }

    public string ConfirmText
    {
        get => _confirmText;
        private set => SetProperty(ref _confirmText, value);
    }

    public string ConfirmActionText
    {
        get => _confirmActionText;
        private set => SetProperty(ref _confirmActionText, value);
    }

    // ------------------------------------------------------------- lifecycle

    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "Connecting to the local supervisor...";
        try
        {
            await EnsureConnectionAsync().ConfigureAwait(true);
            IsConnected = true;
            DesktopLog.Write("library connected");
            await RefreshAsync(loadProfiles: true).ConfigureAwait(true);
            _stateTimer.Start();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ShowError(ex, "library.initialize");
            StatusMessage = "The supervisor is unavailable.";
            IsLoading = false;
        }
    }

    private async Task RefreshAsync(bool loadProfiles)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        IsLoading = true;
        try
        {
            var data = await RequestAsync(OperationNames.InstanceList, null, TimeSpan.FromSeconds(30))
                .ConfigureAwait(true);
            var nodes = data["instances"] as JsonArray ?? new JsonArray();
            Merge(nodes.OfType<JsonObject>().Select(LibraryCard.From).ToList());

            await LoadDoctorAsync().ConfigureAwait(true);
            if (loadProfiles)
            {
                await LoadProfilesAsync().ConfigureAwait(true);
            }

            ApplyFilterAndSort();
            RaiseCounts();
            await VerifyLiveStatesAsync().ConfigureAwait(true);
            RaiseCounts();
            StatusMessage = _cards.Count == 0
                ? "No managed instance yet. Create one from the pinned AOSP profile."
                : "";
        }
        catch (Exception ex)
        {
            ShowError(ex, "instance.list");
            StatusMessage = "The device list could not be read.";
        }
        finally
        {
            IsLoading = false;
            _refreshing = false;
        }
    }

    /// <summary>
    /// The 2 s tick only re-reads <c>instance.list</c> (cheap) so a boot that
    /// finishes while the library is open shows its real state. Profiles and
    /// readiness are not re-read here.
    /// </summary>
    private async Task RefreshStatesAsync()
    {
        if (_disposed || _refreshing || IsBusy || IsCreateOpen || IsCloneOpen || IsConfirmOpen)
        {
            return;
        }

        if (_connection is null)
        {
            return;
        }

        try
        {
            var data = await RequestAsync(OperationNames.InstanceList, null, TimeSpan.FromSeconds(20))
                .ConfigureAwait(true);
            var nodes = data["instances"] as JsonArray ?? new JsonArray();
            var incoming = nodes.OfType<JsonObject>().Select(LibraryCard.From).ToList();
            if (incoming.Count != _cards.Count || incoming.Any(card => _cards.All(existing => existing.Name != card.Name)))
            {
                Merge(incoming);
                await LoadProfilesAsync().ConfigureAwait(true);
                ApplyFilterAndSort();
                RaiseCounts();
                return;
            }

            foreach (var fresh in incoming)
            {
                _cards.FirstOrDefault(card => card.Name == fresh.Name)?.ApplyState(fresh.State);
            }

            RaiseCounts();

            // A guest that reports boot completion but is still worded
            // "Starting" needs the supervisor's own wait to become "Running".
            // The wait is issued only when instance.status already says the
            // boot finished, so it returns quickly and the state word stays the
            // supervisor's.
            foreach (var starting in _cards.Where(card => card.State == "Starting").ToList())
            {
                _ = EnsureBootCompletionAsync(starting);
            }

            await VerifyLiveStatesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DesktopLog.Write("library state tick failed: " + ex.Message);
        }
    }

    private bool _statusTickBusy;

    /// <summary>
    /// instance.list is the persisted record; only instance.status says whether
    /// the engine process is alive. Cards that claim Running or Starting, and
    /// cards whose record still points at an engine process, are checked against
    /// that field so the chip never repeats a state the supervisor cannot back
    /// with a live process.
    /// </summary>
    private async Task VerifyLiveStatesAsync()
    {
        if (_statusTickBusy || _connection is null)
        {
            return;
        }

        var candidates = _cards
            .Where(card => card.State is "Running" or "Starting" || card.HasEnginePid)
            .Take(4)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        _statusTickBusy = true;
        try
        {
            foreach (var card in candidates)
            {
                try
                {
                    var status = await RequestAsync(
                        OperationNames.InstanceStatus,
                        new JsonObject { ["name"] = card.Name },
                        TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                    card.ApplyLive(
                        status["engineAlive"]?.GetValue<bool>(),
                        status["pid"]?.GetValue<int>() is > 0,
                        status["bootCompleted"]?.GetValue<string>()?.Trim() == "1",
                        status["lastError"]?.GetValue<string>() ?? "");
                }
                catch (Exception ex)
                {
                    DesktopLog.Write($"library live check {card.Name} failed: {ex.Message}");
                    card.ApplyLive(null, false, false, "");
                }
            }
        }
        finally
        {
            _statusTickBusy = false;
        }
    }

    private async Task EnsureBootCompletionAsync(LibraryCard card)
    {
        if (_disposed || !_bootWaits.Add(card.Name))
        {
            return;
        }

        try
        {
            var status = await RequestAsync(
                OperationNames.InstanceStatus,
                new JsonObject { ["name"] = card.Name },
                TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            var booted = status["bootCompleted"]?.GetValue<string>() == "1";
            var adbOnline = status["adbState"]?.GetValue<string>() == "device";
            if (!booted || !adbOnline)
            {
                return;
            }

            var readiness = await RequestAsync(
                OperationNames.InstanceWait,
                new JsonObject { ["name"] = card.Name, ["timeoutSeconds"] = 120 },
                TimeSpan.FromMinutes(3)).ConfigureAwait(true);
            var seconds = readiness["launchedToInteractiveSeconds"]?.GetValue<double>();
            StatusMessage = seconds is > 0
                ? $"{card.Name}: Android is interactive after {seconds:N1} s."
                : $"{card.Name}: Android is interactive.";
            await RefreshAsync(loadProfiles: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"library boot wait {card.Name} failed: {ex.Message}");
        }
        finally
        {
            _bootWaits.Remove(card.Name);
        }
    }

    private void Merge(IReadOnlyList<LibraryCard> incoming)
    {
        foreach (var card in incoming)
        {
            var existing = _cards.FirstOrDefault(c => c.Name == card.Name);
            if (existing is null)
            {
                card.Attach(this);
                _cards.Add(card);
            }
            else
            {
                existing.ApplyIdentity(card);
            }
        }

        foreach (var stale in _cards.Where(c => incoming.All(i => i.Name != c.Name)).ToList())
        {
            _cards.Remove(stale);
        }

        _cards.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadDoctorAsync()
    {
        try
        {
            _doctor = await RequestAsync(OperationNames.Doctor, null, TimeSpan.FromMinutes(2)).ConfigureAwait(true);
            ApplyDoctor(_doctor);
        }
        catch (Exception ex)
        {
            DesktopLog.Write("library doctor failed: " + ex.Message);
            CreateImageText = "not reported";
            CreateImageTone = "warning";
            CreateWhpxText = "not reported";
            CreateWhpxTone = "warning";
            SetCreateBlocked("The supervisor did not report SDK readiness, so a create cannot be pre-flighted.");
        }
    }

    private void ApplyDoctor(JsonObject doctor)
    {
        var sdk = doctor["androidSdk"] as JsonObject;
        var pins = doctor["pinned"] as JsonObject;
        var pinned = pins?["image"]?.GetValue<string>() ?? "";
        var requiredRevision = pins?["requiredImageRevision"]?.GetValue<string>() ?? "2";
        var images = sdk?["images"] as JsonArray ?? new JsonArray();
        var installed = images.OfType<JsonObject>()
            .FirstOrDefault(image => image["packageId"]?.GetValue<string>() == pinned);
        var revision = installed?["revision"]?.GetValue<string>();

        if (pinned.Length == 0)
        {
            CreateImageText = "The supervisor reported no pinned image.";
            CreateImageTone = "error";
        }
        else if (installed is null)
        {
            CreateImageText = $"{pinned} is not installed.";
            CreateImageTone = "error";
        }
        else if (revision != requiredRevision)
        {
            CreateImageText = $"{pinned} revision {revision} is installed; {requiredRevision} is required.";
            CreateImageTone = "warning";
        }
        else
        {
            var bytes = installed["systemImageBytes"]?.GetValue<long>() ?? 0;
            CreateImageText = bytes > 0
                ? $"{pinned} · revision {revision} · {FormatBytes(bytes)} image"
                : $"{pinned} · revision {revision}";
            CreateImageTone = "ok";
        }

        var whpx = doctor["whpxUsable"]?.GetValue<bool>() == true;
        var verdict = doctor["accelerationVerdict"]?.GetValue<string>() ?? "not reported";
        CreateWhpxText = whpx ? $"WHPX usable · {verdict}" : $"WHPX is not usable · {verdict}";
        CreateWhpxTone = whpx ? "ok" : "error";

        // Disk space is judged by the supervisor's own instance.diskEstimate, not
        // by a second rule in the window: two different thresholds disagreeing in
        // one dialog would be worse than one honest verdict.
        var blocked = new List<string>();
        if (CreateImageTone == "error")
        {
            blocked.Add("the pinned system image is not installed");
        }

        if (!whpx)
        {
            blocked.Add("WHPX is not usable");
        }

        SetCreateBlocked(blocked.Count == 0
            ? ""
            : "Blocked: " + string.Join("; ", blocked) + ".");
    }

    private void SetCreateBlocked(string reason)
    {
        CreateBlockedReason = reason;
        OnPropertyChanged(nameof(CanCreate));
        SubmitCreateCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadProfilesAsync()
    {
        var pending = _cards.Where(card => !card.ProfileLoaded).ToList();
        if (pending.Count == 0 || _connection is null)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4);
        var tasks = pending.Select(async card =>
        {
            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                var data = await RequestAsync(
                    OperationNames.InstanceProfileExport,
                    new JsonObject { ["name"] = card.Name },
                    TimeSpan.FromSeconds(15)).ConfigureAwait(true);
                card.ApplyProfile(data["profile"] as JsonObject);
            }
            catch (Exception ex)
            {
                DesktopLog.Write($"library profile {card.Name} failed: {ex.Message}");
                card.ApplyProfile(null);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
        RaiseCounts();
    }

    private async Task LoadDiskEstimateAsync()
    {
        try
        {
            var estimate = await RequestAsync(
                OperationNames.InstanceDiskEstimate,
                new JsonObject { ["dataPartition"] = DesiredDataPartition },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            var required = estimate["requiredBytes"]?.GetValue<long>() ?? 0;
            var reserve = estimate["withSnapshotReserveBytes"]?.GetValue<long>() ?? 0;
            var free = estimate["freeBytes"]?.GetValue<long>() ?? 0;
            var sufficient = estimate["sufficient"]?.GetValue<bool>() == true;
            CreateDiskReserveText = FormatBytes(reserve);
            CreateDiskFreeText = FormatBytes(free);
            CreateDiskTone = sufficient ? "ok" : "error";
            CreateDiskVerdict = sufficient
                ? $"sufficient for {DesiredDataPartition} plus one snapshot"
                : "not enough free space for the data partition plus one snapshot";
        }
        catch (Exception ex)
        {
            DesktopLog.Write("library disk estimate failed: " + ex.Message);
            CreateDiskTone = "warning";
            CreateDiskVerdict = "the supervisor did not report a disk estimate";
        }
    }

    // ---------------------------------------------------------------- create

    private async Task OpenCreateAsync()
    {
        ClearError();
        CreateName = NextDeviceName();
        CreateNameError = "";
        IsCreateOpen = true;
        await LoadDiskEstimateAsync().ConfigureAwait(true);
        if (_doctor is not null)
        {
            ApplyDoctor(_doctor);
        }
    }

    private string NextDeviceName()
    {
        var index = 1;
        var name = "dev";
        while (_cards.Any(card => card.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            index++;
            name = "dev" + index;
        }

        return name;
    }

    private void CloseCreate()
    {
        IsCreateOpen = false;
        CreateNameError = "";
        ClearError();
    }

    private async Task CreateAsync()
    {
        var name = CreateName.Trim();
        CreateNameError = ValidateName(name);
        if (CreateNameError.Length > 0 || CreateBlockedReason.Length > 0)
        {
            return;
        }

        await RunBusyAsync($"Creating the AVD from the pinned image ({name})...", async () =>
        {
            var created = await RequestAsync(
                OperationNames.InstanceCreate,
                new JsonObject { ["name"] = name },
                TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            IsCreateOpen = false;
            await RefreshAsync(loadProfiles: true).ConfigureAwait(true);
            var serial = created["serial"]?.GetValue<string>() ?? "";
            var console = created["consolePort"]?.GetValue<int>() ?? 0;
            var grpc = created["grpcPort"]?.GetValue<int>() ?? 0;
            StatusMessage = serial.Length > 0
                ? $"Created {name} · {serial} · console {console} · controller {grpc}. It is not started."
                : $"Created {name}. It is not started.";
        }).ConfigureAwait(true);
    }

    // ----------------------------------------------------------------- clone

    private async Task OpenCloneAsync(LibraryCard card)
    {
        ClearError();
        _cloneSourceName = card.Name;
        CloneSource = card.Name;
        CloneSourceState = card.DisplayState;
        CloneSourceImage = card.ImagePackage;
        CloneName = NextCloneName(card.Name);
        CloneNameError = "";
        CloneBlockedReason = card.CanManage
            ? ""
            : $"{card.Name} is {card.DisplayState}. The supervisor copies the writable disk, so the source must be stopped.";
        CloneNote =
            "The copy gets its own AVD directory, a new port pair and a new disk lineage whose parent is the source "
            + "lineage. The source AVD is only read; the supervisor verifies it afterwards. The copied byte count is "
            + "reported by instance.duplicate when it finishes.";
        IsCloneOpen = true;
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private string NextCloneName(string source)
    {
        var candidate = source + "-copy";
        var index = 2;
        while (_cards.Any(card => card.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{source}-copy{index}";
            index++;
        }

        if (candidate.Length > 32)
        {
            candidate = source[..Math.Min(source.Length, 27)] + "-copy";
        }

        return candidate;
    }

    private void CloseClone()
    {
        IsCloneOpen = false;
        CloneNameError = "";
        ClearError();
    }

    private async Task CloneAsync()
    {
        var target = CloneName.Trim();
        CloneNameError = ValidateName(target);
        if (CloneNameError.Length > 0 || CloneBlockedReason.Length > 0)
        {
            return;
        }

        var source = _cloneSourceName;
        await RunBusyAsync($"Copying {source} to {target}...", async () =>
        {
            var result = await RequestAsync(
                OperationNames.InstanceDuplicate,
                new JsonObject { ["name"] = source, ["target"] = target },
                TimeSpan.FromMinutes(10)).ConfigureAwait(true);
            IsCloneOpen = false;
            await RefreshAsync(loadProfiles: true).ConfigureAwait(true);
            var bytes = result["copiedBytes"]?.GetValue<long>() ?? 0;
            var files = result["copiedFiles"] as JsonArray;
            StatusMessage =
                $"Cloned {source} to {target}: {files?.Count ?? 0} disk file(s), {FormatBytes(bytes)} copied, "
                + $"serial {result["serial"]?.GetValue<string>() ?? "not reported"}.";
        }).ConfigureAwait(true);
    }

    // ----------------------------------------------------------- card actions

    /// <summary>Opens the selected guest in the Studio window (raised to the shell).</summary>
    public void OpenInStudio(LibraryCard card) => OpenInStudioRequested?.Invoke(card.Name);

    public Task OpenCloneForAsync(LibraryCard card) => OpenCloneAsync(card);

    public Task OpenRecoveryForAsync(LibraryCard card) => OpenRecoveryAsync(card);

    public Task CopyProfileForAsync(LibraryCard card) => CopyProfileAsync(card);

    public void ConfirmResetFor(LibraryCard card) => ConfirmReset(card);

    public void ConfirmDeleteFor(LibraryCard card) => ConfirmDelete(card);

    public void ConfirmDeleteFor(RecoveryPoint point) => ConfirmDeletePoint(point);

    public Task RestoreForAsync(RecoveryPoint point) => RestorePointAsync(point);

    public Task StartCardAsync(LibraryCard card) => StartAsync(card);

    public Task StopCardAsync(LibraryCard card) => StopAsync(card);

    private async Task StartAsync(LibraryCard card)
    {
        ClearError();
        await RunBusyAsync($"Starting {card.Name}...", async () =>
        {
            await RequestAsync(
                OperationNames.InstanceStart,
                new JsonObject { ["name"] = card.Name, ["coldBoot"] = true },
                TimeSpan.FromMinutes(2)).ConfigureAwait(true);
            StatusMessage = $"{card.Name}: the engine start was accepted. The state below is the supervisor's.";
            await RefreshAsync(loadProfiles: false).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task StopAsync(LibraryCard card)
    {
        ClearError();
        await RunBusyAsync($"Stopping {card.Name}...", async () =>
        {
            await RequestAsync(
                OperationNames.InstanceStop,
                new JsonObject { ["name"] = card.Name },
                TimeSpan.FromMinutes(2)).ConfigureAwait(true);
            StatusMessage = $"{card.Name}: stop accepted.";
            await RefreshAsync(loadProfiles: false).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void ConfirmReset(LibraryCard card) =>
        ShowConfirm(
            $"Reset {card.Name} to its baseline?",
            "The supervisor deletes the writable disk artifacts of this guest (userdata*). Installed apps, accounts and "
            + "guest files are lost. The recovery points and the configuration are kept, and the lineage is preserved so "
            + "the existing points stay measurable.",
            "Reset the device",
            async () =>
            {
                var result = await RequestAsync(
                    OperationNames.InstanceReset,
                    new JsonObject { ["name"] = card.Name },
                    TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                var files = result["removedFiles"] as JsonArray;
                var freed = result["freedBytes"]?.GetValue<long>() ?? 0;
                await RefreshAsync(loadProfiles: false).ConfigureAwait(true);
                StatusMessage = files is null or { Count: 0 }
                    ? $"{card.Name} had no writable disk to reset."
                    : $"Reset {card.Name}: {files.Count} file(s), {FormatBytes(freed)} freed.";
            });

    private void ConfirmDelete(LibraryCard card) =>
        ShowConfirm(
            $"Delete {card.Name}?",
            "The supervisor stops the guest and deletes its managed instance and AVD directory, including recovery "
            + "points. This cannot be undone from the app.",
            "Delete the device",
            async () =>
            {
                await RequestAsync(
                    OperationNames.InstanceDelete,
                    new JsonObject { ["name"] = card.Name },
                    TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                if (_recoveryDevice == card.Name)
                {
                    CloseRecovery();
                }

                await RefreshAsync(loadProfiles: true).ConfigureAwait(true);
                StatusMessage = $"Deleted {card.Name}.";
            });

    private async Task CopyProfileAsync(LibraryCard card)
    {
        try
        {
            var data = await RequestAsync(
                OperationNames.InstanceProfileExport,
                new JsonObject { ["name"] = card.Name },
                TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            Clipboard.SetText(data.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            StatusMessage = $"{card.Name}: profile JSON copied to the clipboard.";
        }
        catch (Exception ex)
        {
            ShowError(ex, "instance.profileExport", card.Name);
        }
    }

    // -------------------------------------------------------------- recovery

    private async Task OpenRecoveryAsync(LibraryCard card)
    {
        ClearError();
        RecoveryDevice = card.Name;
        RecoverySubtitle =
            "Compatibility is checked against the instance fingerprint the supervisor stored with each point. "
            + "Save and restore need a running guest; the supervisor answers with its own reason when they do not.";
        RecoveryMessage = "";
        RecoveryPoints.Clear();
        IsRecoveryOpen = true;
        SavePointTag = "baseline-" + DateTimeOffset.Now.ToString("MMdd-HHmm", CultureInfo.InvariantCulture);
        SavePointError = "";
        await LoadRecoveryAsync(card).ConfigureAwait(true);
    }

    private async Task LoadRecoveryAsync(LibraryCard card)
    {
        IsRecoveryBusy = true;
        try
        {
            var data = await RequestAsync(
                OperationNames.SnapshotList,
                new JsonObject { ["name"] = card.Name },
                TimeSpan.FromSeconds(45)).ConfigureAwait(true);
            RecoveryPoints.Clear();
            var snapshots = data["snapshots"] as JsonArray ?? new JsonArray();
            foreach (var node in snapshots.OfType<JsonObject>())
            {
                RecoveryPoints.Add(RecoveryPoint.From(this, card.Name, node, card.IsRunning));
            }

            OnPropertyChanged(nameof(HasRecoveryPoints));
            OnPropertyChanged(nameof(ShowRecoveryEmpty));
            UpdateSavePointState(card);
        }
        catch (Exception ex)
        {
            ShowError(ex, "snapshot.list", card.Name);
        }
        finally
        {
            IsRecoveryBusy = false;
        }
    }

    private void UpdateSavePointState(LibraryCard card)
    {
        CanSavePoint = card.IsRunning;
        SavePointHint = card.IsRunning
            ? ""
            : card.StateKey == "starting"
                ? $"{card.Name} is still starting; a point can be saved once Android is interactive."
                : $"Saving a point needs a running guest; {card.Name} is {card.DisplayState}.";
    }

    private void CloseRecovery()
    {
        IsRecoveryOpen = false;
        RecoveryPoints.Clear();
        RecoveryMessage = "";
        ClearError();
    }

    private async Task SavePointAsync()
    {
        var tag = SavePointTag.Trim();
        SavePointError = ValidateTag(tag);
        if (SavePointError.Length > 0)
        {
            return;
        }

        var device = RecoveryDevice;
        await RunBusyAsync($"Saving recovery point {tag} on {device}...", async () =>
        {
            var result = await RequestAsync(
                OperationNames.SnapshotSave,
                new JsonObject { ["name"] = device, ["tag"] = tag },
                TimeSpan.FromMinutes(10)).ConfigureAwait(true);
            var seconds = result["seconds"]?.GetValue<double>() ?? 0;
            RecoveryMessage = $"Saved {tag} in {seconds:N1} s (the supervisor's own timing).";
            var card = _cards.FirstOrDefault(c => c.Name == device);
            if (card is not null)
            {
                await LoadRecoveryAsync(card).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    private async Task RestorePointAsync(RecoveryPoint point)
    {
        var device = RecoveryDevice;
        await RunBusyAsync($"Restoring {point.Tag} on {device}...", async () =>
        {
            var result = await RequestAsync(
                OperationNames.SnapshotLoad,
                new JsonObject { ["name"] = device, ["tag"] = point.Tag },
                TimeSpan.FromMinutes(15)).ConfigureAwait(true);
            var seconds = result["seconds"]?.GetValue<double>() ?? 0;
            var recovery = result["recovery"]?.GetValue<string>() ?? "not reported";
            var readiness = result["readinessSeconds"]?.GetValue<double>();
            RecoveryMessage = readiness is > 0
                ? $"Restored {point.Tag} in {seconds:N1} s; the guest answered again after {readiness:N1} s ({recovery})."
                : $"Restored {point.Tag} in {seconds:N1} s ({recovery}).";
            await RefreshAsync(loadProfiles: false).ConfigureAwait(true);
            if (_cards.FirstOrDefault(c => c.Name == device) is { } card)
            {
                await LoadRecoveryAsync(card).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Deletion is offered as two steps: the supervisor refuses to delete the
    /// newest compatible point without <c>force</c>, and that refusal is shown
    /// as a typed error. Only the explicit second confirmation re-sends it with
    /// <c>force = true</c>.
    /// </summary>
    private async Task DeletePointAsync(RecoveryPoint point, bool force)
    {
        var device = RecoveryDevice;
        await RunBusyAsync($"Deleting {point.Tag}...", async () =>
        {
            JsonObject result;
            try
            {
                result = await RequestAsync(
                    OperationNames.SnapshotDelete,
                    new JsonObject { ["name"] = device, ["tag"] = point.Tag, ["force"] = force },
                    TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }
            catch (SupervisorRequestException ex) when (ex.Code == "approval_required" && !force)
            {
                // The supervisor refused because this is the last verified
                // recovery point. Turn that refusal into the explicit second
                // confirmation instead of a dead-end error band.
                ConfirmForceDeletePoint(point);
                return;
            }

            var freed = result["freedBytes"]?.GetValue<long>() ?? 0;
            RecoveryMessage = $"Deleted {point.Tag}; {FormatBytes(freed)} freed.";
            if (_cards.FirstOrDefault(c => c.Name == device) is { } card)
            {
                await LoadRecoveryAsync(card).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    private void ConfirmDeletePoint(RecoveryPoint point) =>
        ShowConfirm(
            $"Delete recovery point {point.Tag}?",
            "The supervisor deletes this point's snapshot directory. It cannot be restored afterwards.",
            "Delete the point",
            () => DeletePointAsync(point, force: false));

    private void ConfirmForceDeletePoint(RecoveryPoint point) =>
        ShowConfirm(
            $"Delete the last verified recovery point {point.Tag}?",
            "The supervisor refused the first attempt: this is the newest compatible point, so deleting it leaves the "
            + "guest without a verified recovery point. Only continue if you accept that.",
            "Delete it anyway",
            () => DeletePointAsync(point, force: true));

    // ----------------------------------------------------- confirmation flow

    private void ShowConfirm(string title, string text, string action, Func<Task> run)
    {
        ConfirmTitle = title;
        ConfirmText = text;
        ConfirmActionText = action;
        _confirmAction = run;
        IsConfirmOpen = true;
    }

    private void CloseConfirm()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    private async Task ConfirmAsync()
    {
        var action = _confirmAction;
        IsConfirmOpen = false;
        _confirmAction = null;
        if (action is null)
        {
            return;
        }

        ClearError();
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError(ex, "confirm");
        }
    }

    // ------------------------------------------------------------- utilities

    private void ApplyFilterAndSort()
    {
        var query = SearchText.Trim();
        var filtered = _cards.Where(card => Matches(card, query)).ToList();
        switch (SortKey)
        {
            case "State":
                // Fix pass item 3: Running first, then the guests that need
                // attention (failed, or a Running record whose engine is gone),
                // then the rest in the existing state order.
                filtered = filtered
                    .OrderBy(card => card.StateRank)
                    .ThenBy(card => card.StateOrder)
                    .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                break;
            case "RAM":
                // Configured RAM comes from instance.profileExport; a device whose
                // profile was not reported sorts last instead of pretending to be
                // the smallest.
                filtered = filtered
                    .OrderByDescending(card => card.RamMb > 0)
                    .ThenByDescending(card => card.RamMb)
                    .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                break;
            case "API level":
                filtered = filtered
                    .OrderByDescending(card => card.ApiLevel)
                    .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                break;
            default:
                filtered = filtered
                    .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                break;
        }

        Tiles.Clear();
        foreach (var card in filtered)
        {
            Tiles.Add(card);
        }

        // The dashed "New device" tile is the last tile of a populated grid, and
        // is replaced by the full empty/no-match state when the grid is empty.
        if (filtered.Count > 0)
        {
            Tiles.Add(LibraryCard.Placeholder());
        }

        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoMatchState));
        OnPropertyChanged(nameof(CardCountText));
    }

    private static bool Matches(LibraryCard card, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return card.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || card.ImagePackage.Contains(query, StringComparison.OrdinalIgnoreCase)
               || card.Serial.Contains(query, StringComparison.OrdinalIgnoreCase)
               || card.DeviceProfile.Contains(query, StringComparison.OrdinalIgnoreCase)
               || card.ApiText.Contains(query, StringComparison.OrdinalIgnoreCase)
               || card.AbiChip.Contains(query, StringComparison.OrdinalIgnoreCase)
               || card.State.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(InstanceCountText));
        OnPropertyChanged(nameof(RunningCountText));
        OnPropertyChanged(nameof(RunningOfText));
        OnPropertyChanged(nameof(RamText));
        OnPropertyChanged(nameof(RamOfText));
        OnPropertyChanged(nameof(AttentionCountText));
        OnPropertyChanged(nameof(AttentionText));
        OnPropertyChanged(nameof(CardCountText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoMatchState));
    }

    private string ValidateName(string raw)
    {
        var name = raw.Trim();
        if (name.Length == 0)
        {
            return "Enter a name for the new device.";
        }

        if (name.Length > 32)
        {
            return "Names are limited to 32 characters.";
        }

        if (!InstanceName.IsMatch(name))
        {
            return "Use lowercase letters, digits and hyphens, starting with a letter or digit (for example qa-checkout).";
        }

        var clash = _cards.FirstOrDefault(card => card.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            return $"A managed device named “{clash.Name}” already exists. Names must be unique across the workspace.";
        }

        if (name is "con" or "prn" or "aux" or "nul")
        {
            return "That name is reserved by Windows.";
        }

        return "";
    }

    private static string ValidateTag(string raw)
    {
        var tag = raw.Trim();
        if (tag.Length == 0)
        {
            return "Enter a tag for the recovery point.";
        }

        if (tag.Length > 64)
        {
            return "Tags are limited to 64 characters.";
        }

        return SnapshotTag.IsMatch(tag)
            ? ""
            : "Use letters, digits, dot, dash and underscore only.";
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
            StatusMessage = "The operation failed; nothing else was changed.";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private void ShowError(Exception ex, string operation, string? device = null)
    {
        var code = ex switch
        {
            SupervisorRequestException supervisor => supervisor.Code,
            TimeoutException => "timeout",
            OperationCanceledException => "cancelled",
            SupervisorUnavailableException => "supervisor_unavailable",
            InvalidOperationException when ex.Message.Contains("supervisor is not connected", StringComparison.OrdinalIgnoreCase)
                => "supervisor_unavailable",
            _ => "error",
        };

        var (title, action) = DescribeError(code);
        ErrorTitle = title;
        ErrorText = ex.Message;
        ErrorAction = action;
        ErrorDetails = string.Join(Environment.NewLine, new[]
        {
            $"code: {code}",
            $"operation: {operation}",
            $"device: {device ?? "(library)"}",
            $"time: {DateTimeOffset.Now:O}",
            $"workspace: {_paths.Root}",
            $"message: {ex.Message}",
        });
    }

    private void ClearError()
    {
        ErrorTitle = "";
        ErrorText = "";
        ErrorAction = "";
        ErrorDetails = "";
    }

    private void CopyErrorDetails()
    {
        try
        {
            if (ErrorDetails.Length == 0)
            {
                return;
            }

            Clipboard.SetText(ErrorDetails);
            StatusMessage = "Error details copied to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not copy the error details: " + ex.Message;
        }
    }

    private static (string Title, string Action) DescribeError(string code) => code switch
    {
        "supervisor_unavailable" => ("The supervisor is not connected",
            "Start the workspace supervisor for this root, then choose Refresh."),
        "invalid_request" => ("The supervisor rejected the request",
            "Fix the highlighted field; names are lowercase letters, digits and hyphens."),
        "image_missing" => ("The pinned system image is missing",
            "Install the pinned image with sdkmanager, then Refresh before creating a device."),
        "low_disk" => ("Not enough disk space",
            "Free space on the workspace volume. Verified recovery points are never deleted automatically."),
        "port_conflict" => ("A required port is in use",
            "Close whatever owns the port, then retry. The supervisor never reuses another guest's ports."),
        "instance_not_running" => ("The guest is not running",
            "Start the guest first; the supervisor saves and restores points only for a live guest."),
        "approval_required" => ("The supervisor refused without approval",
            "This point is the last verified recovery point. Use the explicit second confirmation if you accept that."),
        "snapshot_failed" => ("The snapshot operation failed",
            "Check Logcat and the instance log; the guest state is unchanged."),
        "not_found" or "instance_not_found" => ("The device no longer exists",
            "Refresh the library; another window or client may have deleted it."),
        "timeout" => ("The operation timed out",
            "The supervisor may still be finishing. Refresh and re-read the state before retrying."),
        "cancelled" => ("The operation was cancelled", "Nothing else changed. Retry when ready."),
        _ => ("The operation failed",
            "Copy the details into a report. The guest state was not changed silently."),
    };

    private async Task<SupervisorConnection> EnsureConnectionAsync()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        _connection = await SupervisorConnection.ConnectAsync(_paths.Root, TimeSpan.FromSeconds(25))
            .ConfigureAwait(true);
        _connection.Trace = DesktopLog.Write;
        IsConnected = true;
        return _connection;
    }

    private async Task<JsonObject> RequestAsync(string op, JsonObject? args, TimeSpan deadline)
    {
        var connection = await EnsureConnectionAsync().ConfigureAwait(true);
        await _requestGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var response = await connection.RequestAsync(op, args, deadline).ConfigureAwait(true);
            if (!response.Ok)
            {
                throw new SupervisorRequestException(response.Code, response.Message);
            }

            return response.Data as JsonObject ?? new JsonObject();
        }
        catch (SupervisorUnavailableException)
        {
            // The pipe is gone (supervisor restarted or the session ended); drop
            // the client so the next request reconnects instead of reusing a
            // dead stream.
            _connection = null;
            IsConnected = false;
            _ = connection.DisposeAsync();
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        NewDeviceCommand.RaiseCanExecuteChanged();
        SubmitCreateCommand.RaiseCanExecuteChanged();
        SubmitCloneCommand.RaiseCanExecuteChanged();
        SavePointCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024d:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):N1} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):N2} GB",
    };

    internal static string AndroidVersion(int api) => ImageNaming.AndroidVersion(api);

    internal static (int Api, string Abi, string Tag) ParseImage(string? package) => ImageNaming.ParseImage(package);

    internal static string TagLabel(string tag) => ImageNaming.TagLabel(tag);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateTimer.Stop();
        var connection = _connection;
        _connection = null;
        _requestGate.Dispose();
        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}

/// <summary>
/// One managed instance as a library card. Identity and state mirror
/// <c>instance.list</c>; the chips come from <c>instance.profileExport</c> and
/// print "not reported" when the supervisor returns no profile.
/// </summary>
public sealed class LibraryCard : ObservableObject
{
    private string _state = "Unknown";
    private bool? _engineAlive;
    private bool _enginePidKnown;
    private bool _bootCompleted;
    private string _lastErrorText = "";
    private int _ramMb;
    private int _width;
    private int _height;
    private int _cores;
    private string _dataPartition = "";
    private bool _profileLoaded;
    private string _profileError = "";

    public required string Name { get; init; }

    public required string Serial { get; init; }

    public required string ImagePackage { get; init; }

    public required string DeviceProfile { get; init; }

    public required string Gpu { get; init; }

    public bool IsPlaceholder { get; set; }

    public string State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                RaiseDerived();
            }
        }
    }

    /// <summary>
    /// UX-18: the one lifecycle every surface reads. The card resolves the same
    /// guest through it as the Studio does, so the chip, the primary action and
    /// the enabled commands can never disagree with each other.
    /// </summary>
    public DeviceLifecycle Lifecycle =>
        DeviceLifecycle.From(_state, _engineAlive, _enginePidKnown, _bootCompleted);

    public int RamMb
    {
        get => _ramMb;
        private set
        {
            if (SetProperty(ref _ramMb, value))
            {
                RaiseDerived();
            }
        }
    }

    public bool ProfileLoaded => _profileLoaded;

    public int ApiLevel => DeviceLibraryViewModel.ParseImage(ImagePackage).Api;

    public string ApiText => DeviceLibraryViewModel.AndroidVersion(ApiLevel);

    public string TagText => DeviceLibraryViewModel.TagLabel(DeviceLibraryViewModel.ParseImage(ImagePackage).Tag);

    public string AbiChip
    {
        get
        {
            var abi = DeviceLibraryViewModel.ParseImage(ImagePackage).Abi;
            return abi.Length > 0 ? abi : "ABI not reported";
        }
    }

    public string RamChip => _ramMb > 0 ? $"{_ramMb / 1024d:N0} GB" : "RAM not reported";

    public string ResolutionChip
    {
        get
        {
            if (_width <= 0 || _height <= 0)
            {
                return "resolution not reported";
            }

            var shortSide = Math.Min(_width, _height);
            return $"{shortSide}p";
        }
    }

    /// <summary>
    /// Full pixel size as <c>instance.profileExport</c> reported it. The chip
    /// keeps the short side; the card subtitle prints both, like the mockup.
    /// </summary>
    public string ResolutionText => _width > 0 && _height > 0
        ? $"{_width}×{_height}"
        : "resolution not reported";

    /// <summary>
    /// Card subtitle: "Android 15 · x86_64 · 1080×2400", the exact shape the
    /// device-manager feedback asked for. Every field comes from the supervisor
    /// (image package for the API level and ABI, profileExport for the size).
    /// </summary>
    public string Subtitle => $"{ApiText} · {AbiChip} · {ResolutionText}";

    /// <summary>
    /// The record points at an engine process. A guest that was started once
    /// keeps that fact until it is stopped or deleted, so the card knows a
    /// guest that "stopped" really lost its engine.
    /// </summary>
    internal bool HasEnginePid => _enginePidKnown;

    /// <summary>
    /// UX-18: the card's state key comes from the one lifecycle, so a record
    /// that says Running while the engine process is gone reads "dead" and not
    /// a fake Running - the same word the Studio shows for that guest.
    /// </summary>
    public string StateKey => Lifecycle.Key;

    public string DisplayState => Lifecycle.Label;

    public string StateTooltip
    {
        get
        {
            var engine = _engineAlive switch
            {
                true => "engine process alive",
                false => "engine process not alive",
                null => "engine liveness not reported",
            };
            return _lastErrorText.Length > 0
                ? $"Supervisor state: {DisplayState} · {engine} · last error: {_lastErrorText}"
                : $"Supervisor state: {DisplayState} · {engine}";
        }
    }

    public string NameTooltip => $"{Name}\n{ImagePackage}\nprofile {DeviceProfile} · {Serial}";

    /// <summary>
    /// Item 3: the card's Stop affordance is an icon, so its tooltip and its
    /// accessible name both have to say which guest it stops.
    /// </summary>
    public string StopTooltip => $"Stop {Name}";

    public string ChipTooltip => _profileLoaded
        ? $"{_width}×{_height} @ {_cores} cores · data partition {_dataPartition} · from instance.profileExport"
        : "instance.profileExport did not return a profile for this device";

    public string ProfileNote => _profileError;

    public int StateOrder => Lifecycle.Phase switch
    {
        DevicePhase.Running => 0,
        DevicePhase.Failed or DevicePhase.EngineGone => 1,
        DevicePhase.Starting => 2,
        DevicePhase.Stopping => 3,
        DevicePhase.Stopped => 4,
        _ => 5,
    };

    /// <summary>
    /// Fix pass item 3: the State sort's first key — Running (0), then the guests
    /// that need attention (1: failed, or a Running record whose engine process is
    /// gone), then everything else (2).
    /// </summary>
    public int StateRank => Lifecycle.Phase switch
    {
        DevicePhase.Running => 0,
        DevicePhase.Failed or DevicePhase.EngineGone => 1,
        _ => 2,
    };

    /// <summary>"open" | "start" | "starting" | "stopping" | "recover" | "unknown".</summary>
    public string PrimaryKind => Lifecycle.Phase switch
    {
        DevicePhase.Running => "open",
        DevicePhase.Starting => "starting",
        DevicePhase.Stopping => "stopping",
        DevicePhase.Failed => "recover",
        DevicePhase.EngineGone => "start",
        DevicePhase.Stopped => "start",
        _ => "unknown",
    };

    public bool CanManage => Lifecycle.Phase is not (DevicePhase.Running or DevicePhase.Starting or DevicePhase.Stopping);

    public bool IsRunning => Lifecycle.IsRunning;

    public bool CanOpen => Lifecycle.Phase is DevicePhase.Running or DevicePhase.Starting;

    public RelayCommand OpenCommand { get; private set; } = new(() => { });

    public AsyncRelayCommand StartCommand { get; private set; } = new(() => Task.CompletedTask);

    public AsyncRelayCommand StopCommand { get; private set; } = new(() => Task.CompletedTask);

    public AsyncRelayCommand CloneCommand { get; private set; } = new(() => Task.CompletedTask);

    public AsyncRelayCommand RecoveryCommand { get; private set; } = new(() => Task.CompletedTask);

    public RelayCommand ResetCommand { get; private set; } = new(() => { });

    public RelayCommand DeleteCommand { get; private set; } = new(() => { });

    public AsyncRelayCommand CopyProfileCommand { get; private set; } = new(() => Task.CompletedTask);

    public static LibraryCard From(JsonObject node)
    {
        var card = new LibraryCard
        {
            Name = node["name"]?.GetValue<string>() ?? "",
            Serial = node["serial"]?.GetValue<string>() ?? "",
            ImagePackage = node["imagePackage"]?.GetValue<string>() ?? "",
            DeviceProfile = node["deviceProfile"]?.GetValue<string>() ?? "",
            Gpu = node["gpu"]?.GetValue<string>() ?? "",
            State = node["state"]?.GetValue<string>() ?? "Unknown",
        };

        // The list carries the record's engine pid: a record that points at a
        // process is a guest that was started, which the lifecycle needs to
        // tell "never started" from "the process is gone".
        card._enginePidKnown = node["enginePid"]?.GetValue<int>() is > 0;
        return card;
    }

    public static LibraryCard Placeholder() => new()
    {
        Name = "",
        Serial = "",
        ImagePackage = "",
        DeviceProfile = "",
        Gpu = "",
        IsPlaceholder = true,
    };

    internal void Attach(DeviceLibraryViewModel owner)
    {
        OpenCommand = new RelayCommand(() => owner.OpenInStudio(this));
        StartCommand = new AsyncRelayCommand(() => owner.StartCardAsync(this), () => !owner.IsBusy && Lifecycle.CanStart);
        StopCommand = new AsyncRelayCommand(() => owner.StopCardAsync(this), () => !owner.IsBusy && Lifecycle.CanStop);
        CloneCommand = new AsyncRelayCommand(() => owner.OpenCloneForAsync(this), () => !owner.IsBusy);
        RecoveryCommand = new AsyncRelayCommand(() => owner.OpenRecoveryForAsync(this), () => !owner.IsBusy);
        ResetCommand = new RelayCommand(() => owner.ConfirmResetFor(this), () => !owner.IsBusy && CanManage);
        DeleteCommand = new RelayCommand(() => owner.ConfirmDeleteFor(this), () => !owner.IsBusy && CanManage);
        CopyProfileCommand = new AsyncRelayCommand(() => owner.CopyProfileForAsync(this), () => !owner.IsBusy);
    }

    internal void ApplyState(string state)
    {
        State = state;
        RaiseCommandStates();
    }

    /// <summary>
    /// UX-18: the live evidence for one card. The engine process, the record's
    /// pid and the guest's own boot answer are what turn a record's word into
    /// the lifecycle every surface reads.
    /// </summary>
    internal void ApplyLive(bool? engineAlive, bool enginePidKnown, bool bootCompleted, string lastError)
    {
        var changedChip = _engineAlive != engineAlive
            || _enginePidKnown != enginePidKnown
            || _bootCompleted != bootCompleted;
        _engineAlive = engineAlive;
        _enginePidKnown = enginePidKnown;
        _bootCompleted = bootCompleted;
        _lastErrorText = lastError;
        if (changedChip)
        {
            RaiseDerived();
            RaiseCommandStates();
        }
    }

    internal void ApplyIdentity(LibraryCard other)
    {
        State = other.State;
        _enginePidKnown = other._enginePidKnown;
        RaiseDerived();
        RaiseCommandStates();
    }

    internal void ApplyProfile(JsonObject? profile)
    {
        _profileLoaded = true;
        if (profile is null)
        {
            _profileError = "The supervisor did not report a profile for this device.";
            _ramMb = 0;
            _width = 0;
            _height = 0;
            _cores = 0;
            _dataPartition = "";
        }
        else
        {
            _profileError = "";
            RamMb = profile["ramMb"]?.GetValue<int>() ?? 0;
            _width = profile["width"]?.GetValue<int>() ?? 0;
            _height = profile["height"]?.GetValue<int>() ?? 0;
            _cores = profile["cores"]?.GetValue<int>() ?? 0;
            _dataPartition = profile["dataPartition"]?.GetValue<string>() ?? "";
        }

        RaiseDerived();
    }

    internal void RaiseCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        CloneCommand.RaiseCanExecuteChanged();
        RecoveryCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        CopyProfileCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(CanOpen));
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Lifecycle));
        OnPropertyChanged(nameof(StateKey));
        OnPropertyChanged(nameof(PrimaryKind));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StateOrder));
        OnPropertyChanged(nameof(StateRank));
        OnPropertyChanged(nameof(ApiLevel));
        OnPropertyChanged(nameof(ApiText));
        OnPropertyChanged(nameof(TagText));
        OnPropertyChanged(nameof(AbiChip));
        OnPropertyChanged(nameof(RamChip));
        OnPropertyChanged(nameof(ResolutionChip));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(DisplayState));
        OnPropertyChanged(nameof(StateTooltip));
        OnPropertyChanged(nameof(ChipTooltip));
    }
}

/// <summary>One recovery point as <c>snapshot.list</c> reported it.</summary>
public sealed class RecoveryPoint
{
    private RecoveryPoint(DeviceLibraryViewModel owner, string device, bool guestRunning)
    {
        Device = device;
        GuestRunning = guestRunning;
        RestoreCommand = new AsyncRelayCommand(() => owner.RestoreForAsync(this), () => CanRestore);
        DeleteCommand = new RelayCommand(() => owner.ConfirmDeleteFor(this), () => !owner.IsBusy);
    }

    public string Device { get; }

    /// <summary>
    /// snapshot.load needs a live engine: <c>SnapshotAsync</c> answers
    /// instance_not_running otherwise. The button is disabled with that reason
    /// instead of offering an action the supervisor will refuse.
    /// </summary>
    public bool GuestRunning { get; }

    public string Tag { get; private init; } = "";

    public long Bytes { get; private init; }

    public string CreatedText { get; private init; } = "";

    public bool Compatible { get; private init; }

    public string Reason { get; private init; } = "";

    public bool CanRestore => Compatible && GuestRunning;

    public string CompatibleText => Compatible ? "compatible" : "incompatible";

    public string SizeText => DeviceLibraryViewModel.FormatBytes(Bytes);

    public string MetaText => CreatedText.Length > 0
        ? $"{CreatedText} · {SizeText}"
        : $"created time not reported · {SizeText}";

    public AsyncRelayCommand RestoreCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public string RestoreTooltip => !Compatible
        ? $"The supervisor marked this point incompatible: {Reason}"
        : GuestRunning
            ? "Restore this point (snapshot.load)."
            : "The guest must be running to restore a point (snapshot.load).";

    public static RecoveryPoint From(DeviceLibraryViewModel owner, string device, JsonObject node, bool guestRunning)
    {
        var created = node["createdUtc"]?.GetValue<string>() ?? "";
        var createdText = DateTimeOffset.TryParse(
            created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "";
        return new RecoveryPoint(owner, device, guestRunning)
        {
            Tag = node["tag"]?.GetValue<string>() ?? "",
            Bytes = node["bytes"]?.GetValue<long>() ?? 0,
            CreatedText = createdText,
            Compatible = node["compatible"]?.GetValue<bool>() == true,
            Reason = node["incompatibleReason"]?.GetValue<string>() ?? "",
        };
    }
}
