using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
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
    private SupervisorConnection? _connection;
    private bool _busy;
    private bool _loadedOnce;

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

        Loaded += (_, _) =>
        {
            ApplyLayout(ActualWidth);
            if (IsVisible)
            {
                ActivatePage();
            }
        };
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
    /// panels; no content changes.
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
            Grid.SetRow(PairingCard, 0);
            Grid.SetColumn(PairingCard, 0);
            Grid.SetColumnSpan(PairingCard, 3);
            Grid.SetColumn(TableStack, 0);
            Grid.SetColumnSpan(TableStack, 3);
            Grid.SetRow(TableStack, 2);
        }
        else
        {
            LeftColumn.Width = new GridLength(320);
            GapColumn.Width = new GridLength(12);
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            TopRow.Height = GridLength.Auto;
            GapRow.Height = new GridLength(0);
            BodyRow.Height = new GridLength(0);
            Grid.SetRow(PairingCard, 0);
            Grid.SetColumn(PairingCard, 0);
            Grid.SetColumnSpan(PairingCard, 1);
            Grid.SetColumn(TableStack, 2);
            Grid.SetColumnSpan(TableStack, 1);
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

        PublishCounts();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnRetry(object sender, RoutedEventArgs e) => await RefreshAsync();

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
            PublishCounts();
            HasError = false;
            ErrorText = "";
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
        TemplateBox.SelectedItem = templates.FirstOrDefault(row => row.Name == selected)
                                   ?? templates.FirstOrDefault(row => row.State is "Defined" or "Stopped");
    }

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
