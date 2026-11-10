using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Desktop;

/// <summary>
/// Operator surface for local agent sessions: pairings, scoped grants,
/// revocation, human takeover and the latest agent operations. All calls go
/// through the same supervisor pipe and policy as the CLI and MCP clients.
/// </summary>
public partial class AgentSessionsWindow : Window
{
    private readonly ObservableCollection<SessionRow> _sessions = new();
    private readonly ObservableCollection<PairingRow> _pairings = new();
    private readonly ObservableCollection<OperationRow> _operations = new();
    private SupervisorConnection? _connection;
    private bool _busy;

    public AgentSessionsWindow()
    {
        InitializeComponent();
        SessionsGrid.ItemsSource = _sessions;
        PairingsGrid.ItemsSource = _pairings;
        OperationsGrid.ItemsSource = _operations;
        ScopesBox.Text = string.Join(',', AgentService.AllScopes);
        Loaded += async (_, _) => await RefreshAsync();
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

            _sessions.Clear();
            foreach (var node in sessions.OfType<JsonObject>())
            {
                _sessions.Add(new SessionRow(
                    Text(node, "clientName"),
                    Text(node, "session"),
                    Text(node, "instance"),
                    Text(node, "scopes"),
                    FormatTime(node, "expiresUtc"),
                    Bool(node, "revoked") ? "revoked" : Bool(node, "expired") ? "expired" : "active"));
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
                    FormatTime(node, "expiresUtc")));
            }

            _operations.Clear();
            foreach (var node in operations.OfType<JsonObject>())
            {
                _operations.Add(new OperationRow(
                    FormatTime(node, "startedUtc"),
                    Text(node, "operation"),
                    Text(node, "caller"),
                    Text(node, "outcome"),
                    Text(node, "code"),
                    node["durationMs"]?.GetValue<long>().ToString(CultureInfo.InvariantCulture) ?? ""));
            }

            await LoadTemplatesAsync(connection);
            StatusText.Text = $"{_sessions.Count} sessions, {_pairings.Count} pairings";
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

        var scopes = ScopesBox.Text.Trim();
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

    private static string FormatTime(JsonObject node, string name)
    {
        var raw = Text(node, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : raw;
    }

    public sealed record SessionRow(
        string Client, string Session, string Instance, string Scopes, string Expires, string State);

    public sealed record PairingRow(
        string Client, string Pairing, string Status, string Template, string Instance, string Expires);

    public sealed record OperationRow(
        string Time, string Operation, string Caller, string Outcome, string Code, string Duration);

    public sealed record TemplateRow(string Name, string State)
    {
        public string Display => $"{Name} ({State})";
    }
}
