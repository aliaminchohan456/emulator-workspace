using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
using EmulatorWorkspace.Desktop.Services;
using EmulatorWorkspace.Desktop.ViewModels;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// UX-13: the Agent Sessions surface, hosted as a shell page instead of its own
/// window. Every collection, supervisor call, handler and decision is the UX-09
/// one; only the chrome moved into the surrounding shell. The page owns the
/// one-second ticker for the countdowns and publishes the real counts the shell
/// prints (status bar and the rail's pending badge).
/// </summary>
public partial class AgentSessionsPage : UserControl
{
    /// <summary>Total paired sessions the last refresh returned.</summary>
    public static readonly DependencyProperty SessionCountProperty = DependencyProperty.Register(
        nameof(SessionCount), typeof(int), typeof(AgentSessionsPage), new PropertyMetadata(0));

    /// <summary>Total pairing requests the last refresh returned.</summary>
    public static readonly DependencyProperty PairingCountProperty = DependencyProperty.Register(
        nameof(PairingCount), typeof(int), typeof(AgentSessionsPage), new PropertyMetadata(0));

    /// <summary>Requests still inside their approval window: the rail badge.</summary>
    public static readonly DependencyProperty PendingPairingCountProperty = DependencyProperty.Register(
        nameof(PendingPairingCount), typeof(int), typeof(AgentSessionsPage), new PropertyMetadata(0));

    /// <summary>Live sessions (active and unexpired): the status bar's "Agents N".</summary>
    public static readonly DependencyProperty ActiveSessionCountProperty = DependencyProperty.Register(
        nameof(ActiveSessionCount), typeof(int), typeof(AgentSessionsPage), new PropertyMetadata(0));

    /// <summary>"3 sessions · 1 pairing", from the same presentation rule as before.</summary>
    public static readonly DependencyProperty SummaryTextProperty = DependencyProperty.Register(
        nameof(SummaryText), typeof(string), typeof(AgentSessionsPage),
        new PropertyMetadata(AgentSessionsPresentation.Summary(0, 0)));

    /// <summary>True while a session exists, so "Take over · revoke all" can run.</summary>
    public static readonly DependencyProperty HasSessionsProperty = DependencyProperty.Register(
        nameof(HasSessions), typeof(bool), typeof(AgentSessionsPage), new PropertyMetadata(false));

    public static readonly DependencyProperty TakeoverTipProperty = DependencyProperty.Register(
        nameof(TakeoverTip), typeof(string), typeof(AgentSessionsPage),
        new PropertyMetadata("Revoke every agent session on the selected guest and cancel new agent input"));

    /// <summary>The refresh failed; the page shows the err-soft band with Retry.</summary>
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register(
        nameof(HasError), typeof(bool), typeof(AgentSessionsPage), new PropertyMetadata(false));

    public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.Register(
        nameof(ErrorText), typeof(string), typeof(AgentSessionsPage), new PropertyMetadata(""));

    private readonly ObservableCollection<SessionRow> _sessions = new();
    private readonly ObservableCollection<PairingRow> _pairings = new();
    private readonly ObservableCollection<OperationRow> _operations = new();
    private readonly DispatcherTimer _countdown;
    private readonly DispatcherTimer _badgePoll;
    private readonly PendingPairingPoll _pollState = new();
    private SupervisorConnection? _connection;
    private Window? _shellWindow;
    private MainViewModel? _shellModel;
    private bool _busy;
    private bool _pollBusy;
    private bool _loadedOnce;
    private int _lastPolledPending = -1;

    public AgentSessionsPage()
    {
        InitializeComponent();
        SessionsGrid.ItemsSource = _sessions;
        PairingsGrid.ItemsSource = _pairings;
        OperationsGrid.ItemsSource = _operations;
        ScopesBox.Text = string.Join(", ", AgentService.AllScopes);

        // One ticker for every live value on the surface (expiry countdowns and
        // the Expiring chip); it only runs while the page is on screen.
        _countdown = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _countdown.Tick += (_, _) => Tick();

        // UX-15 item 0: the rail badge's own light poll. It reads only the
        // pending count, only while the shell is connected, and only while the
        // window is not minimised; the page's ticker above is unrelated and
        // still stops whenever the page is hidden.
        _badgePoll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PendingPairingPoll.Interval,
        };
        _badgePoll.Tick += async (_, _) => await PollBadgeAsync();

        Loaded += (_, _) =>
        {
            ApplyLayout(ActualWidth);
            HookShell();
            if (IsVisible)
            {
                ActivatePage();
            }

            SyncBadgePoll();
        };
        Unloaded += (_, _) => UnhookShell();
        DataContextChanged += (_, _) => HookShell();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                ActivatePage();
            }
            else
            {
                _countdown.Stop();
            }
        };
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
    }

    public int SessionCount
    {
        get => (int)GetValue(SessionCountProperty);
        private set => SetValue(SessionCountProperty, value);
    }

    public int PairingCount
    {
        get => (int)GetValue(PairingCountProperty);
        private set => SetValue(PairingCountProperty, value);
    }

    public int PendingPairingCount
    {
        get => (int)GetValue(PendingPairingCountProperty);
        private set => SetValue(PendingPairingCountProperty, value);
    }

    public int ActiveSessionCount
    {
        get => (int)GetValue(ActiveSessionCountProperty);
        private set => SetValue(ActiveSessionCountProperty, value);
    }

    public string SummaryText
    {
        get => (string)GetValue(SummaryTextProperty);
        private set => SetValue(SummaryTextProperty, value);
    }

    public bool HasSessions
    {
        get => (bool)GetValue(HasSessionsProperty);
        private set => SetValue(HasSessionsProperty, value);
    }

    public string TakeoverTip
    {
        get => (string)GetValue(TakeoverTipProperty);
        private set => SetValue(TakeoverTipProperty, value);
    }

    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        private set => SetValue(HasErrorProperty, value);
    }

    public string ErrorText
    {
        get => (string)GetValue(ErrorTextProperty);
        private set => SetValue(ErrorTextProperty, value);
    }

    /// <summary>
    /// The shell calls this when the page becomes the visible destination. The
    /// first activation loads the three lists; later ones only restart the
    /// ticker, so the operator's selection and scroll position survive a page
    /// switch. A stale list is what the Refresh action is for.
    /// </summary>
    public void ActivatePage()
    {
        _countdown.Start();

        // UX-15 item 0: a stopped poll comes back when the shell is connected
        // again and the operator uses the page; plain re-entry while running
        // changes nothing.
        if (_shellModel?.IsConnected == true)
        {
            ResumeBadgePoll();
        }

        if (!_loadedOnce)
        {
            _loadedOnce = true;
            _ = RefreshAsync();
        }
    }

    /// <summary>The command bar's Refresh action: the same reload as before.</summary>
    public void RefreshFromShell() => _ = RefreshAsync();

    /// <summary>The command bar's "Take over · revoke all": the same revocation.</summary>
    public void TakeOverFromShell() => TakeOver();

    /// <summary>
    /// The mockup puts the approval card beside the tables at 1180 DIP and wider
    /// and stacks it above them below that. The switch only moves the three
    /// panels; no content changes. UX-15: at wide the page body no longer
    /// scrolls as a whole - each column owns a scroll viewer that shows its bar
    /// only when that column is taller than the window - and the approval
    /// primary keeps its content width at 1024 instead of spanning the card.
    /// </summary>
    private void ApplyLayout(double width)
    {
        // The breakpoint is the shell window's width, the same 1180 DIP the
        // inspector uses, not the narrower body cell. A hosted control can read
        // its window before it is measured on first load.
        if (Window.GetWindow(this) is { } window && window.ActualWidth > 0)
        {
            width = window.ActualWidth;
        }

        var stacked = width < 1180;
        if (stacked)
        {
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            GapColumn.Width = new GridLength(0);
            RightColumn.Width = new GridLength(0);
            TopRow.Height = GridLength.Auto;
            GapRow.Height = new GridLength(12);
            BodyRow.Height = GridLength.Auto;
            Grid.SetRow(LeftScroll, 0);
            Grid.SetColumn(LeftScroll, 0);
            Grid.SetColumnSpan(LeftScroll, 3);
            Grid.SetColumn(RightScroll, 0);
            Grid.SetColumnSpan(RightScroll, 3);
            Grid.SetRow(RightScroll, 2);
            BodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            LeftScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            RightScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

            // The caption keeps the left of one line and the primary sits at its
            // own content width (136 minimum) on the right.
            Grid.SetRow(ApproveButton, 0);
            Grid.SetColumn(ApproveButton, 1);
            Grid.SetColumnSpan(ApproveButton, 1);
            ApproveButton.HorizontalAlignment = HorizontalAlignment.Right;
            ApproveButton.MinWidth = 136;
            ApproveButton.Padding = new Thickness(16, 6, 16, 6);
            Grid.SetRow(ApproveCaption, 0);
            Grid.SetColumn(ApproveCaption, 0);
            Grid.SetColumnSpan(ApproveCaption, 1);
            ApproveCaption.VerticalAlignment = VerticalAlignment.Center;
            ApproveCaption.Margin = new Thickness(0, 0, 12, 0);
        }
        else
        {
            LeftColumn.Width = new GridLength(320);
            GapColumn.Width = new GridLength(12);
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            GapRow.Height = new GridLength(0);
            BodyRow.Height = new GridLength(0);
            Grid.SetRow(LeftScroll, 0);
            Grid.SetColumn(LeftScroll, 0);
            Grid.SetColumnSpan(LeftScroll, 1);
            Grid.SetColumn(RightScroll, 2);
            Grid.SetColumnSpan(RightScroll, 1);
            Grid.SetRow(RightScroll, 0);
            BodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            LeftScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            RightScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            // The 320 DIP card keeps the full-width primary with the caption
            // beneath it, as the mockup draws it.
            Grid.SetRow(ApproveButton, 0);
            Grid.SetColumn(ApproveButton, 0);
            Grid.SetColumnSpan(ApproveButton, 2);
            ApproveButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApproveButton.MinWidth = 0;
            ApproveButton.Padding = new Thickness(14, 6, 14, 6);
            Grid.SetRow(ApproveCaption, 1);
            Grid.SetColumn(ApproveCaption, 0);
            Grid.SetColumnSpan(ApproveCaption, 2);
            ApproveCaption.VerticalAlignment = VerticalAlignment.Top;
            ApproveCaption.Margin = new Thickness(0, 8, 0, 0);
        }
    }

    /// <summary>Refreshes every live value once a second without rebuilding rows.</summary>
    private void Tick()
    {
        var now = DateTimeOffset.Now;
        foreach (var row in _sessions)
        {
            row.Tick(now);
        }

        foreach (var row in _pairings)
        {
            row.Tick(now);
        }

        PublishCounts();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnRetry(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>
    /// UX-15 item 0: follows the shell's supervisor connection and the window's
    /// state so the badge poll never outlives either. The page is hosted, so the
    /// main window and its view model are the shell's; both are observed, never
    /// changed.
    /// </summary>
    private void HookShell()
    {
        if (_shellWindow is null && Window.GetWindow(this) is { } window)
        {
            _shellWindow = window;
            window.StateChanged += OnShellStateChanged;
            ApplyShellState(window.WindowState == WindowState.Minimized);
        }

        if (DataContext is MainViewModel model && !ReferenceEquals(model, _shellModel))
        {
            if (_shellModel is not null)
            {
                _shellModel.PropertyChanged -= OnShellModelPropertyChanged;
            }

            _shellModel = model;
            model.PropertyChanged += OnShellModelPropertyChanged;
            ApplyShellConnection(model.IsConnected);
        }
    }

    private void UnhookShell()
    {
        if (_shellWindow is not null)
        {
            _shellWindow.StateChanged -= OnShellStateChanged;
            _shellWindow = null;
        }

        if (_shellModel is not null)
        {
            _shellModel.PropertyChanged -= OnShellModelPropertyChanged;
            _shellModel = null;
        }
    }

    private void OnShellStateChanged(object? sender, EventArgs e)
    {
        if (_shellWindow is { } window)
        {
            ApplyShellState(window.WindowState == WindowState.Minimized);
        }
    }

    private void OnShellModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConnected) && _shellModel is { } model)
        {
            ApplyShellConnection(model.IsConnected);
        }
    }

    private void ApplyShellConnection(bool connected)
    {
        var was = _pollState.State;
        if (connected)
        {
            _pollState.OnConnected();
        }
        else
        {
            _pollState.OnDisconnected();
        }

        if (was != _pollState.State)
        {
            DesktopLog.Write($"badge poll: {_pollState.State}");
        }

        SyncBadgePoll();
    }

    /// <summary>
    /// A successful read proves a supervisor is reachable again, so a poll that
    /// stopped on a broken pipe may start again - unless the window is
    /// minimised, which the poll's own rule still honours.
    /// </summary>
    private void ResumeBadgePoll()
    {
        var was = _pollState.State;
        _pollState.OnConnected();
        if (was != _pollState.State)
        {
            DesktopLog.Write($"badge poll: {_pollState.State}");
        }

        SyncBadgePoll();
    }

    private void ApplyShellState(bool minimised)
    {
        var was = _pollState.State;
        if (minimised)
        {
            _pollState.OnMinimised();
        }
        else
        {
            _pollState.OnRestored();
        }

        if (was != _pollState.State)
        {
            DesktopLog.Write($"badge poll: {_pollState.State}");
        }

        SyncBadgePoll();
    }

    /// <summary>Starts or stops the timer to match the poll's own lifetime rule.</summary>
    private void SyncBadgePoll()
    {
        if (_pollState.ShouldPoll)
        {
            if (!_badgePoll.IsEnabled)
            {
                _badgePoll.Start();
            }

            return;
        }

        _badgePoll.Stop();
    }

    /// <summary>
    /// One light read of the pairing list every ten seconds. Only the pending
    /// count leaves this method: the tables, the selection and the counts the
    /// last full refresh published stay exactly as they were.
    /// </summary>
    private async Task PollBadgeAsync()
    {
        if (_pollBusy || !_pollState.ShouldPoll)
        {
            return;
        }

        _pollBusy = true;
        try
        {
            var connection = _connection ?? await EnsureConnectionAsync(TimeSpan.FromSeconds(5));
            var response = await connection.RequestAsync(
                OperationNames.AgentSessionList, null, TimeSpan.FromSeconds(5));
            if (!response.Ok)
            {
                // A typed refusal (a scope problem, a bad request) is not a lost
                // pipe; the badge simply keeps its last real value.
                DesktopLog.Write($"badge poll: {response.Code} {response.Message}");
                return;
            }

            var data = response.Data as JsonObject ?? new JsonObject();
            var pairings = data["pairings"] as JsonArray ?? new JsonArray();
            var now = DateTimeOffset.Now;
            var pending = pairings
                .OfType<JsonObject>()
                .Count(node => AgentSessionsPresentation.IsPendingPairing(
                    Text(node, "status"), Time(node, "expiresUtc"), now));

            PendingPairingCount = pending;
            if (pending != _lastPolledPending)
            {
                _lastPolledPending = pending;
                DesktopLog.Write($"badge poll: {pending} pending pairing(s)");
            }
        }
        catch (SupervisorUnavailableException ex)
        {
            StopBadgePollOnDisconnect(ex.Message);
        }
        catch (IOException ex)
        {
            // A vanished supervisor surfaces on the write side as a broken
            // pipe, not as the reader's typed exception: both mean the same
            // thing to the badge, so both stop it.
            StopBadgePollOnDisconnect(ex.Message);
        }
        catch (Exception ex)
        {
            // A slow or busy supervisor is not a disconnect: keep the schedule.
            DesktopLog.Write("badge poll: transient " + ex.Message);
        }
        finally
        {
            _pollBusy = false;
        }
    }

    /// <summary>The pipe is gone: drop it and stop the poll until the shell reconnects.</summary>
    private void StopBadgePollOnDisconnect(string reason)
    {
        _connection = null;
        _pollState.OnDisconnected();
        _badgePoll.Stop();
        DesktopLog.Write($"badge poll: stopped (disconnected) {reason}");
    }

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var connection = await EnsureConnectionAsync();
            var data = await RequestAsync(connection, OperationNames.AgentSessionList, null);
            var sessions = data["sessions"] as JsonArray ?? new JsonArray();
            var pairings = data["pairings"] as JsonArray ?? new JsonArray();
            var operations = data["recentOperations"] as JsonArray ?? new JsonArray();
            var now = DateTimeOffset.Now;

            // The approval card prints the selected request, so the choice is read
            // before the collections are repopulated (clearing them drops the
            // ListBox selection). A refresh then keeps the operator's request; a
            // first load shows the first pending one.
            var previousPairing = (PairingsGrid.SelectedItem as PairingRow)?.Pairing;

            _sessions.Clear();
            foreach (var node in sessions.OfType<JsonObject>())
            {
                _sessions.Add(new SessionRow(
                    Text(node, "clientName"),
                    Text(node, "session"),
                    Text(node, "instance"),
                    Text(node, "guestName"),
                    Text(node, "scopes"),
                    Time(node, "expiresUtc"),
                    Bool(node, "revoked") ? "revoked" : Bool(node, "expired") ? "expired" : "active",
                    now));
            }

            // UX-15 item 1: waiting requests first, answered ones under the
            // "Past" rule, each group in the supervisor's own newest-first order.
            // Only the first past row carries the divider, so a list with no past
            // requests draws no divider at all.
            var built = pairings
                .OfType<JsonObject>()
                .Select(node => new PairingRow(
                    Text(node, "clientName"),
                    Text(node, "pairingId"),
                    Text(node, "status"),
                    Text(node, "template"),
                    Text(node, "instance"),
                    Time(node, "expiresUtc"),
                    now))
                .ToList();
            var (livePairings, pastPairings) = AgentSessionsPresentation.PartitionPairings(
                built, row => row.Status, row => row.ExpiresUtc, now);

            _pairings.Clear();
            foreach (var row in livePairings)
            {
                _pairings.Add(row);
            }

            if (pastPairings.Count > 0)
            {
                pastPairings[0].MarkFirstPast();
                foreach (var row in pastPairings)
                {
                    _pairings.Add(row);
                }
            }

            // The approval card prints the selected request, so it keeps the
            // operator's choice when that request still exists and otherwise
            // falls back to the first pending one so the card is never blank.
            PairingsGrid.SelectedItem =
                _pairings.FirstOrDefault(row => row.Pairing == previousPairing)
                ?? _pairings.FirstOrDefault(row => row.IsPending)
                ?? _pairings.FirstOrDefault();

            _operations.Clear();
            foreach (var node in operations.OfType<JsonObject>())
            {
                _operations.Add(new OperationRow(
                    FormatClock(node, "startedUtc"),
                    Text(node, "operation"),
                    Text(node, "caller"),
                    Text(node, "outcome"),
                    Text(node, "code"),
                    node["durationMs"]?.GetValue<long>().ToString(CultureInfo.InvariantCulture) ?? ""));
            }

            await LoadTemplatesAsync(connection);
            PublishCounts();
            HasError = false;
            ErrorText = "";

            // The read just succeeded, so a supervisor is reachable again: a
            // poll that stopped on a broken pipe may resume, unless the window
            // is minimised (the poll's own rule keeps it paused then).
            ResumeBadgePoll();
        }
        catch (Exception ex)
        {
            // A failed read keeps the last known rows and says so on the page,
            // with the same Retry the shell's typed error surfaces offer.
            HasError = true;
            ErrorText = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Publishes the real counts to the header, status bar and rail badge.</summary>
    private void PublishCounts()
    {
        SessionCount = _sessions.Count;
        PairingCount = _pairings.Count;
        PendingPairingCount = _pairings.Count(row => row.IsPending);
        ActiveSessionCount = _sessions.Count(row => row.CanRevoke);
        SummaryText = AgentSessionsPresentation.Summary(SessionCount, PairingCount);
        HasSessions = SessionCount > 0;
        TakeoverTip = HasSessions
            ? "Revoke every agent session on the selected guest and cancel new agent input"
            : "No paired sessions; there is nothing to take over";
    }

    private async Task LoadTemplatesAsync(SupervisorConnection connection)
    {
        var selected = (TemplateBox.SelectedItem as TemplateRow)?.Name;
        var data = await RequestAsync(connection, OperationNames.InstanceList, null);
        var instances = data["instances"] as JsonArray ?? new JsonArray();
        var templates = instances
            .OfType<JsonObject>()
            .Select(node => new TemplateRow(Text(node, "name"), Text(node, "state")))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        TemplateBox.ItemsSource = templates;
        // UX-15: a pending request is approved against a stopped template, and
        // the supervisor names every agent clone "ag-…". Defaulting to the
        // first stopped guest would offer a clone of a clone, so a real
        // template wins when one exists; otherwise the old first-stopped rule
        // still answers.
        TemplateBox.SelectedItem = templates.FirstOrDefault(row => row.Name == selected)
                                   ?? templates.FirstOrDefault(row => IsTemplate(row) && !IsAgentClone(row))
                                   ?? templates.FirstOrDefault(IsTemplate);
    }

    /// <summary>
    /// UX-18: a template is a guest that is not up. The state word is the
    /// supervisor's own, read through the one lifecycle, so "Defined" (the
    /// record's word for a guest that was created and never started) and
    /// "Stopped" mean the same thing here as everywhere else.
    /// </summary>
    private static bool IsTemplate(TemplateRow row) =>
        DeviceLifecycle.From(row.State, null, false, false).Phase == DevicePhase.Stopped;

    /// <summary>The supervisor's own name for a guest an approval cloned.</summary>
    private static bool IsAgentClone(TemplateRow row) =>
        row.Name.StartsWith("ag-", StringComparison.OrdinalIgnoreCase);

    private async void OnApprove(object sender, RoutedEventArgs e)
    {
        if (PairingsGrid.SelectedItem is not PairingRow pairing)
        {
            Warn("Select a pairing request first.", "Agent sessions");
            return;
        }

        if (TemplateBox.SelectedItem is not TemplateRow template)
        {
            Warn("Select the stopped template guest.", "Agent sessions");
            return;
        }

        // The field displays the grant as ", "-separated words so WPF wraps at a
        // comma instead of mid-scope; the supervisor gets the canonical
        // comma-only grant.
        var scopes = string.Join(
            ',',
            ScopesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (scopes.Length == 0)
        {
            Warn("Enter at least one scope.", "Agent sessions");
            return;
        }

        try
        {
            var connection = await EnsureConnectionAsync();
            await RequestAsync(connection, OperationNames.AgentPairApprove, new JsonObject
            {
                ["pairingId"] = pairing.Pairing,
                ["scopes"] = scopes,
                ["template"] = template.Name,
            }, TimeSpan.FromMinutes(5));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Warn(ex.Message, "Approve pairing");
        }
    }

    private async void OnRevoke(object sender, RoutedEventArgs e)
    {
        // The row action selects its own row first, so the header action and the
        // row action run the same code and the same confirmation.
        if (sender is FrameworkElement { DataContext: SessionRow row })
        {
            SessionsGrid.SelectedItem = row;
        }

        if (SessionsGrid.SelectedItem is not SessionRow session)
        {
            Warn("Select a session first.", "Agent sessions");
            return;
        }

        if (!Confirm($"Revoke {session.Session} for {session.Client}?", "Revoke session"))
        {
            return;
        }

        try
        {
            var connection = await EnsureConnectionAsync();
            await RequestAsync(connection, OperationNames.AgentSessionRevoke, new JsonObject
            {
                ["session"] = session.Session,
            });
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Warn(ex.Message, "Revoke session");
        }
    }

    private async void TakeOver()
    {
        if (SessionsGrid.SelectedItem is not SessionRow session)
        {
            Warn("Select the agent guest first.", "Agent sessions");
            return;
        }

        if (!Confirm($"Take over {session.Instance} and revoke all agent sessions on it?", "Human takeover"))
        {
            return;
        }

        try
        {
            var connection = await EnsureConnectionAsync();
            await RequestAsync(connection, OperationNames.AgentTakeover, new JsonObject
            {
                ["name"] = session.Instance,
            });
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Warn(ex.Message, "Human takeover");
        }
    }

    private async Task<SupervisorConnection> EnsureConnectionAsync(TimeSpan? startup = null)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        _connection = await SupervisorConnection.ConnectAsync(
            WorkspacePaths.Resolve().Root, startup ?? TimeSpan.FromSeconds(20));
        return _connection;
    }

    private static async Task<JsonObject> RequestAsync(
        SupervisorConnection connection, string op, JsonObject? args, TimeSpan? deadline = null)
    {
        var response = await connection.RequestAsync(op, args, deadline ?? TimeSpan.FromSeconds(30));
        if (!response.Ok)
        {
            throw new InvalidOperationException($"[{response.Code}] {response.Message}");
        }

        return response.Data as JsonObject ?? new JsonObject();
    }

    private static string Text(JsonObject node, string name) =>
        node[name]?.GetValue<string>() ?? "";

    private static bool Bool(JsonObject node, string name) =>
        node[name]?.GetValue<bool>() ?? false;

    /// <summary>The supervisor's UTC stamp, parsed once so the countdown can use it.</summary>
    private static DateTimeOffset? Time(JsonObject node, string name)
    {
        var raw = Text(node, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToLocalTime()
            : null;
    }

    /// <summary>
    /// The operation log's Time column: the local clock only, like the mockup.
    /// </summary>
    private static string FormatClock(JsonObject node, string name)
    {
        var raw = Text(node, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : raw;
    }

    /// <summary>An informational box, owned by the shell when the page is hosted.</summary>
    private void Warn(string message, string caption)
    {
        if (Window.GetWindow(this) is { } owner)
        {
            MessageBox.Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool Confirm(string message, string caption)
    {
        var result = Window.GetWindow(this) is { } owner
            ? MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>The automation probe's summary; the surface and counts it prints.</summary>
    public string ProbeSummary() => JsonSerializer.Serialize(new
    {
        page = "Agent Sessions",
        sessions = SessionsGrid.Items.Count,
        pairings = PairingsGrid.Items.Count,
        pendingPairings = PendingPairingCount,
        templates = TemplateBox.Items.Count,
        operations = OperationsGrid.Items.Count,
        status = SummaryText,
    });

    /// <summary>
    /// A paired session as the table prints it. The display members are the pure
    /// rules of <see cref="AgentSessionsPresentation"/>; the countdown and the
    /// derived "Expiring" chip are refreshed once a second by the page.
    /// </summary>
    public sealed class SessionRow : ObservableObject
    {
        private string _countdown = "";
        private string _stateLabel = "";
        private string _tone = "neutral";
        private bool _canRevoke;
        private string _automationName = "";

        public SessionRow(
            string client, string session, string instance, string guestName, string scopes,
            DateTimeOffset? expiresUtc, string state, DateTimeOffset now)
        {
            Client = client;
            Session = session;
            Instance = instance;
            // UX-15 item 6: the supervisor reports the guest's own id, not a
            // friendly alias. Only a value that really arrived is printed as a
            // name; otherwise the raw id stands alone in mono text3.
            GuestName = guestName.Length > 0 ? guestName : instance;
            HasGuestName = guestName.Length > 0;
            Scopes = scopes;
            ExpiresUtc = expiresUtc;
            State = state;
            ExpiresText = expiresUtc?.ToString("dd MMM HH:mm", CultureInfo.CurrentCulture) ?? "—";
            var (visible, hidden) = AgentSessionsPresentation.VisibleScopes(scopes);
            ScopeChips = visible;
            HiddenScopes = hidden;
            HiddenScopeText = hidden > 0 ? $"+{hidden}" : "";
            ScopeTip = scopes.Length == 0 ? "No scopes granted" : scopes;
            Tick(now);
        }

        public string Client { get; }

        public string Session { get; }

        public string Instance { get; }

        /// <summary>The friendly guest name when the supervisor reports one, else the raw id.</summary>
        public string GuestName { get; }

        /// <summary>True only when <see cref="GuestName"/> came from the supervisor, not from the id.</summary>
        public bool HasGuestName { get; }

        /// <summary>The guest's raw id, printed in mono text3 under a friendly name.</summary>
        public string GuestId => Instance;

        public string GuestTip => HasGuestName ? $"{GuestName} · {Instance}" : Instance;

        public string Scopes { get; }

        public string ExpiresText { get; }

        public DateTimeOffset? ExpiresUtc { get; }

        public string State { get; }

        public IReadOnlyList<string> ScopeChips { get; }

        public int HiddenScopes { get; }

        public string HiddenScopeText { get; }

        public string ScopeTip { get; }

        public string Countdown
        {
            get => _countdown;
            private set => SetProperty(ref _countdown, value);
        }

        public string StateLabel
        {
            get => _stateLabel;
            private set => SetProperty(ref _stateLabel, value);
        }

        public string Tone
        {
            get => _tone;
            private set => SetProperty(ref _tone, value);
        }

        /// <summary>A revoked or expired grant cannot be revoked again.</summary>
        public bool CanRevoke
        {
            get => _canRevoke;
            private set => SetProperty(ref _canRevoke, value);
        }

        public string RevokeTip => CanRevoke
            ? $"Revoke this session ({Session})"
            : $"This session is {StateLabel.ToLowerInvariant()}; there is nothing to revoke";

        public string AccessibleName => $"{StateLabel} session {Session} for {Client} on {Instance}";

        /// <summary>
        /// EV-02: the human name UI Automation reads for this row ("opencode-cli,
        /// session Active, guest ag-1a2b3c"). The session table's ListBoxItem binds
        /// this through its container style; ToString is the backstop for a
        /// container that is asked for a name before the binding runs.
        /// </summary>
        public string AutomationName
        {
            get => _automationName;
            private set => SetProperty(ref _automationName, value);
        }

        public override string ToString() => AutomationName;

        public void Tick(DateTimeOffset now)
        {
            Countdown = AgentSessionsPresentation.Countdown(ExpiresUtc, now);
            var (label, tone) = AgentSessionsPresentation.SessionChip(State, ExpiresUtc, now);
            if (label != StateLabel)
            {
                StateLabel = label;
                OnPropertyChanged(nameof(RevokeTip));
            }

            if (tone != Tone)
            {
                Tone = tone;
            }

            CanRevoke = State == "active" && (ExpiresUtc is null || ExpiresUtc > now);

            // The state word is part of the row's name, so the name is refreshed
            // on the same second as the chip. SetProperty only raises when the
            // word really changed, so a steady row stays silent.
            AutomationName = AgentSessionsPresentation.SessionRowName(Client, StateLabel, GuestName);
        }
    }

    public sealed class PairingRow : ObservableObject
    {
        private string _countdown = "";
        private string _statusLabel = "";
        private string _tone = "neutral";
        private bool _expiring;
        private bool _isPending;
        private bool _isFirstPast;
        private string _automationName = "";

        public PairingRow(
            string client, string pairing, string status, string template,
            string instance, DateTimeOffset? expiresUtc, DateTimeOffset now)
        {
            Client = client;
            Pairing = pairing;
            Status = status;
            Template = template;
            Instance = instance;
            ExpiresUtc = expiresUtc;
            ExpiresText = expiresUtc?.ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture) ?? "—";
            IsPast = AgentSessionsPresentation.IsPastPairing(status, expiresUtc, now);
            Tick(now);
        }

        public string Client { get; }

        public string Pairing { get; }

        public string Status { get; }

        public string Template { get; }

        public string Instance { get; }

        public DateTimeOffset? ExpiresUtc { get; }

        public string ExpiresText { get; }

        /// <summary>The card draws its accent edge only while a real request waits.</summary>
        public bool IsPending
        {
            get => _isPending;
            private set => SetProperty(ref _isPending, value);
        }

        public string Countdown
        {
            get => _countdown;
            private set => SetProperty(ref _countdown, value);
        }

        public string StatusLabel
        {
            get => _statusLabel;
            private set => SetProperty(ref _statusLabel, value);
        }

        public string Tone
        {
            get => _tone;
            private set => SetProperty(ref _tone, value);
        }

        /// <summary>True inside the last minute, when the countdown takes the warn tone.</summary>
        public bool Expiring
        {
            get => _expiring;
            private set => SetProperty(ref _expiring, value);
        }

        /// <summary>True when the request sits in the Past group at 85 % opacity.</summary>
        public bool IsPast { get; }

        /// <summary>Row opacity: past requests read quieter than the live one.</summary>
        public double RowOpacity => IsPast ? 0.85 : 1.0;

        /// <summary>The row that draws the thin "Past" rule: only the first past one.</summary>
        public bool IsFirstPast
        {
            get => _isFirstPast;
            private set => SetProperty(ref _isFirstPast, value);
        }

        public void MarkFirstPast() => IsFirstPast = true;

        public string AccessibleName => IsPast
            ? $"{StatusLabel} pairing request from {Client}, past"
            : $"{StatusLabel} pairing request from {Client}";

        /// <summary>
        /// EV-02: the human name UI Automation reads for this row ("opencode-cli,
        /// pairing Pending, expires in 1m 58s"). The countdown is part of the name,
        /// so the property is refreshed in Tick; ToString is the backstop.
        /// </summary>
        public string AutomationName
        {
            get => _automationName;
            private set => SetProperty(ref _automationName, value);
        }

        public override string ToString() => AutomationName;

        public void Tick(DateTimeOffset now)
        {
            Countdown = AgentSessionsPresentation.Countdown(ExpiresUtc, now);
            var (label, tone) = AgentSessionsPresentation.PairingChip(Status, ExpiresUtc, now);
            if (label != StatusLabel)
            {
                StatusLabel = label;
            }

            if (tone != Tone)
            {
                Tone = tone;
            }

            IsPending = string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase)
                         && (ExpiresUtc is null || ExpiresUtc > now);
            Expiring = IsPending && ExpiresUtc is { } expiry
                       && expiry - now <= AgentSessionsPresentation.PairingWarningWindow;

            // The countdown changes every second, so the name does too; the
            // setter only raises when the string really moved.
            AutomationName = AgentSessionsPresentation.PairingRowName(Client, StatusLabel, Countdown);
        }
    }

    public sealed class OperationRow
    {
        public OperationRow(string time, string operation, string caller, string outcome, string code, string duration)
        {
            Time = time;
            Operation = operation;
            Caller = caller;
            Outcome = outcome;
            Code = code;
            Duration = duration;
        }

        public string Time { get; }

        public string Operation { get; }

        public string Caller { get; }

        public string Outcome { get; }

        public string Code { get; }

        public string Duration { get; }

        public string CallerLabel => AgentSessionsPresentation.CallerLabel(Caller);

        public string OutcomeLabel => AgentSessionsPresentation.OutcomeLabel(Outcome);

        /// <summary>"Denied · forbidden_scope": the word plus the supervisor's reason.</summary>
        public string OutcomeChipText => AgentSessionsPresentation.OutcomeChipText(Outcome, Code);

        public string Tone => AgentSessionsPresentation.OutcomeTone(Outcome);

        public string AccessibleName => $"{OutcomeChipText}: {Operation} by {CallerLabel} at {Time}";

        /// <summary>
        /// EV-02: the human name UI Automation reads for this row
        /// ("instance.start, OK"). The operation log never changes after it is
        /// built, so the name is read-only; ToString is the container backstop.
        /// </summary>
        public string AutomationName => AgentSessionsPresentation.OperationRowName(Operation, OutcomeLabel);

        public override string ToString() => AutomationName;
    }

    public sealed record TemplateRow(string Name, string State)
    {
        public string Display => $"{Name} ({AgentSessionsPresentation.InstanceLabel(State)})";
    }
}
