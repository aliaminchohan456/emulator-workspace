using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
using EmulatorWorkspace.Desktop.ViewModels;

namespace EmulatorWorkspace.Desktop;

/// <summary>
/// Operator surface for local agent sessions: pairings, scoped grants,
/// revocation, human takeover and the latest agent operations. All calls go
/// through the same supervisor pipe and policy as the CLI and MCP clients.
/// UX-09 restyled the surface to mockup 08; the three collections, the supervisor
/// calls and every decision are unchanged.
/// </summary>
public partial class AgentSessionsWindow : Window
{
    private readonly ObservableCollection<SessionRow> _sessions = new();
    private readonly ObservableCollection<PairingRow> _pairings = new();
    private readonly ObservableCollection<OperationRow> _operations = new();
    private readonly DispatcherTimer _countdown;
    private SupervisorConnection? _connection;
    private bool _busy;

    public AgentSessionsWindow()
    {
        InitializeComponent();
        Services.ThemeManager.Initialize();
        Services.Chrome.Initialize();
        Services.Chrome.Apply(this);
        SessionsGrid.ItemsSource = _sessions;
        PairingsGrid.ItemsSource = _pairings;
        OperationsGrid.ItemsSource = _operations;
        ScopesBox.Text = string.Join(", ", AgentService.AllScopes);
        TakeoverButton.IsEnabled = false;
        // One ticker for every live value on the surface (expiry countdowns and
        // the Expiring chip); it only runs while the window is open.
        _countdown = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _countdown.Tick += (_, _) => Tick();
        Loaded += async (_, _) =>
        {
            ApplyLayout(ActualWidth);
            _countdown.Start();
            await RefreshAsync();
        };
        Closed += (_, _) => _countdown.Stop();
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
    }

    public string ProbeSummary() => System.Text.Json.JsonSerializer.Serialize(new
    {
        window = Title,
        sessions = SessionsGrid.Items.Count,
        pairings = PairingsGrid.Items.Count,
        templates = TemplateBox.Items.Count,
        operations = OperationsGrid.Items.Count,
        status = StatusText.Text,
    });

    protected override void OnClosed(EventArgs e)
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            // Do not block the operator's window close on pipe shutdown.
            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception)
                {
                }
            });
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// The mockup puts the approval card beside the tables and stacks it above
    /// them once the window is 1024 DIP wide or narrower. The switch only moves
    /// the three panels; no content changes.
    /// </summary>
    private void ApplyLayout(double width)
    {
        var stacked = width <= 1024;
        if (stacked)
        {
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            GapColumn.Width = new GridLength(0);
            RightColumn.Width = new GridLength(0);
            TopRow.Height = GridLength.Auto;
            GapRow.Height = new GridLength(12);
            BodyRow.Height = GridLength.Auto;
            BodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Grid.SetRow(PairingCard, 0);
            Grid.SetRowSpan(PairingCard, 1);
            Grid.SetColumn(TableStack, 0);
            Grid.SetRow(TableStack, 2);
        }
        else
        {
            LeftColumn.Width = new GridLength(280);
            GapColumn.Width = new GridLength(16);
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            GapRow.Height = new GridLength(0);
            BodyRow.Height = new GridLength(0);
            BodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Grid.SetRow(PairingCard, 0);
            Grid.SetRowSpan(PairingCard, 3);
            Grid.SetColumn(TableStack, 2);
            Grid.SetRow(TableStack, 0);
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
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

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
                    Text(node, "scopes"),
                    Time(node, "expiresUtc"),
                    Bool(node, "revoked") ? "revoked" : Bool(node, "expired") ? "expired" : "active",
                    now));
            }

            // igr-feedback-ux11: Take over is meaningless with no session; it is
            // disabled rather than removed so its reason stays in the tooltip.
            TakeoverButton.IsEnabled = _sessions.Count > 0;

            _pairings.Clear();
            foreach (var node in pairings.OfType<JsonObject>())
            {
                _pairings.Add(new PairingRow(
                    Text(node, "clientName"),
                    Text(node, "pairingId"),
                    Text(node, "status"),
                    Text(node, "template"),
                    Text(node, "instance"),
                    Time(node, "expiresUtc"),
                    now));
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
            StatusText.Text = AgentSessionsPresentation.Summary(_sessions.Count, _pairings.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = "refresh failed";
            MessageBox.Show(this, ex.Message, "Agent sessions", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
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
        TemplateBox.SelectedItem = templates.FirstOrDefault(row => row.Name == selected)
                                   ?? templates.FirstOrDefault(row => row.State is "Defined" or "Stopped");
    }

    private async void OnApprove(object sender, RoutedEventArgs e)
    {
        if (PairingsGrid.SelectedItem is not PairingRow pairing)
        {
            MessageBox.Show(this, "Select a pairing request first.", "Agent sessions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (TemplateBox.SelectedItem is not TemplateRow template)
        {
            MessageBox.Show(this, "Select the stopped template guest.", "Agent sessions",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(this, "Enter at least one scope.", "Agent sessions",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(this, ex.Message, "Approve pairing", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRevoke(object sender, RoutedEventArgs e)
    {
        // The header action revokes the selected session; the row action selects
        // its own row first, so both run the same code and the same confirmation.
        if (sender is FrameworkElement { DataContext: SessionRow row })
        {
            SessionsGrid.SelectedItem = row;
        }

        if (SessionsGrid.SelectedItem is not SessionRow session)
        {
            MessageBox.Show(this, "Select a session first.", "Agent sessions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"Revoke {session.Session} for {session.Client}?", "Revoke session",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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
            MessageBox.Show(this, ex.Message, "Revoke session", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnTakeover(object sender, RoutedEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not SessionRow session)
        {
            MessageBox.Show(this, "Select the agent guest first.", "Agent sessions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"Take over {session.Instance} and revoke all agent sessions on it?",
                "Human takeover", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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
            MessageBox.Show(this, ex.Message, "Human takeover", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<SupervisorConnection> EnsureConnectionAsync()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        _connection = await SupervisorConnection.ConnectAsync(
            WorkspacePaths.Resolve().Root, TimeSpan.FromSeconds(20));
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

    /// <summary>
    /// A paired session as the table prints it. The display members are the pure
    /// rules of <see cref="AgentSessionsPresentation"/>; the countdown and the
    /// derived "Expiring" chip are refreshed once a second by the window.
    /// </summary>
    public sealed class SessionRow : ObservableObject
    {
        private string _countdown = "";
        private string _stateLabel = "";
        private string _tone = "neutral";
        private bool _canRevoke;

        public SessionRow(
            string client, string session, string instance, string scopes,
            DateTimeOffset? expiresUtc, string state, DateTimeOffset now)
        {
            Client = client;
            Session = session;
            Instance = instance;
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
        }
    }

    public sealed class PairingRow : ObservableObject
    {
        private string _countdown = "";
        private string _statusLabel = "";
        private string _tone = "neutral";
        private bool _expiring;
        private bool _isPending;

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

        public string AccessibleName => $"{StatusLabel} pairing request from {Client}";

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
    }

    public sealed record TemplateRow(string Name, string State)
    {
        public string Display => $"{Name} ({AgentSessionsPresentation.InstanceLabel(State)})";
    }
}
