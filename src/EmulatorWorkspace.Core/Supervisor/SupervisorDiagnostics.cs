namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// EV-11: a process-wide sink for the few supervisor diagnostics that are worth
/// a line in supervisor.log but live below the operation layer (the shared
/// memory ring's mapping, its fallback to files). The supervisor program sets
/// the sink; a CLI host or a test leaves it null and the line is dropped.
/// </summary>
public static class SupervisorDiagnostics
{
    private static Action<string>? _sink;

    public static void Use(Action<string>? sink) => _sink = sink;

    public static void Write(string message) => _sink?.Invoke(message);
}
