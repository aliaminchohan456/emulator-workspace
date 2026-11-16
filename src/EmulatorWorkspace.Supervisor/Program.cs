using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Storage;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Supervisor;

internal static class Program
{
    private const int HeartbeatSeconds = 60;

    private static readonly object LogGate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly object ExitGate = new();

    // Held in a static field so the delegate cannot be collected while Windows
    // still points at it.
    private static readonly ConsoleCtrlDelegate ConsoleControlHandler = OnConsoleControl;

    private static string? _logPath;
    private static SupervisorExitReason _exitReason = SupervisorExitReason.Normal;
    private static string? _exitDetail;
    private static int _exitCode;
    private static int _exitLogged;

    private static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteExit();
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            NoteExit(SupervisorExitReason.UnhandledException, 1, Describe(eventArgs.ExceptionObject));
            WriteExit();
        };
        SetConsoleCtrlHandler(ConsoleControlHandler, true);

        var root = ReadOption(args, "--root");
        var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

        WorkspacePaths paths;
        RootLock rootLock;
        try
        {
            var resolvedRoot = WorkspacePaths.Resolve(root).Root;
            rootLock = RootLock.Acquire(resolvedRoot);
            paths = new WorkspacePaths(rootLock.CanonicalRoot);
            paths.EnsureLayout();
        }
        catch (SupervisorOwnedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            NoteExit(SupervisorExitReason.StartupRefused, 2, ex.Message);
            WriteExit();
            return 2;
        }

        _logPath = Path.Combine(paths.LogsDirectory, "supervisor.log");
        SupervisorDiagnostics.Use(Log);
        Log($"Supervisor starting (pid {Environment.ProcessId}, session {Process.GetCurrentProcess().SessionId}).");
        Log($"Root: {paths.Root}");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Log("Shutdown requested from console (ctrl_c).");
            NoteExit(SupervisorExitReason.ConsoleCancel, 0);
            shutdown.Cancel();
        };

        SupervisorPipeServer? liveServer = null;
        var heartbeat = HeartbeatAsync(() => liveServer?.ActiveClients ?? 0, shutdown.Token);
        try
        {
            var sdk = SdkLocator.Resolve();
            using var store = new SupervisorStore(paths);
            if (store.LastMigrationError is not null)
            {
                Log("Legacy record migration failed; JSON records and disks were preserved: " + store.LastMigrationError);
            }

            using var journal = new Journal(paths.JournalPath);
            using var agentStore = new AgentStore(paths);
            agentStore.ReconcileInProgressMutations();
            var hub = new SupervisorEventHub();
            var manager = new InstanceManager(paths, sdk, store, hub);
            var agents = new AgentService(paths, sdk, manager, agentStore);
            var library = new DeviceLibraryService(paths, manager);
            var recipes = new RecipeService(paths);
            var adopted = await manager.AdoptAsync(shutdown.Token).ConfigureAwait(false);
            Log("Adoption: " + adopted.ToJsonString());

            OperationDispatcher dispatcher;
            SupervisorPipeServer server;
            dispatcher = new OperationDispatcher(paths, sdk, store, manager, journal, agents, library, recipes, () =>
            {
                Log("Shutdown requested by client.");
                NoteExit(SupervisorExitReason.ClientRequest, 0);
                shutdown.Cancel();
            });
            server = new SupervisorPipeServer(RootIdentity.PipeName(rootLock.CanonicalRoot), dispatcher, manager, Log);

            Log($"Ready. Database: {store.DatabasePath}");
            liveServer = server;
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            await manager.ShutdownAsync().ConfigureAwait(false);
            Log("Supervisor exiting; guest processes are left running for reattachment.");
        }
        catch (OperationCanceledException)
        {
            Log("Supervisor cancelled.");
        }
        catch (Exception ex)
        {
            Log("Fatal: " + ex);
            NoteExit(SupervisorExitReason.UnhandledException, 1, Describe(ex));
            return 1;
        }
        finally
        {
            shutdown.Cancel();
            await heartbeat.ConfigureAwait(false);
            WriteExit();
            rootLock.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// EV-11b: one line a minute while the supervisor is alive, so a log slice
    /// can tell a live process from one that stopped writing. It carries the
    /// uptime, the number of connected clients and the working set; nothing in
    /// it is inferred.
    /// </summary>
    private static async Task HeartbeatAsync(Func<int> clients, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HeartbeatSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Log($"Heartbeat: uptime {ExitTrail.Uptime(Uptime.Elapsed)}, clients {clients()}, " +
                    $"working set {Environment.WorkingSet / (1024 * 1024)} MiB.");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// EV-11b: Windows' console control events. Ctrl+C and Ctrl+Break normally
    /// reach the runtime's own handler first, which raises
    /// <see cref="Console.CancelKeyPress"/> and lets the normal path write the
    /// line; when one arrives here it is recorded the same way. Close, logoff
    /// and shutdown cannot be deferred, so each records its reason and writes
    /// the line before the handler returns false and the default behaviour ends
    /// the process.
    /// </summary>
    private static bool OnConsoleControl(uint controlType)
    {
        var reason = ExitTrail.FromConsoleControl((int)controlType);
        if (reason is null)
        {
            return false;
        }

        NoteExit(reason.Value, 0, $"console control event {controlType}");
        WriteExit();
        return false;
    }

    /// <summary>
    /// Records why the process is ending. The note with the highest
    /// <see cref="ExitTrail.Priority"/> wins, so a crash is never reported as a
    /// plain return and a console close is never reported as a normal shutdown.
    /// </summary>
    private static void NoteExit(SupervisorExitReason reason, int code, string? detail = null)
    {
        lock (ExitGate)
        {
            if (ExitTrail.Priority(reason) < ExitTrail.Priority(_exitReason))
            {
                return;
            }

            _exitReason = reason;
            _exitCode = code;
            _exitDetail = detail;
        }
    }

    /// <summary>
    /// Writes the single trail line and flushes it: <see cref="Log"/> appends
    /// with File.AppendAllText, which closes the handle for every line, so the
    /// line is on disk before the process ends. A second call is a no-op.
    /// </summary>
    private static void WriteExit()
    {
        if (Interlocked.Exchange(ref _exitLogged, 1) != 0)
        {
            return;
        }

        SupervisorExitReason reason;
        int code;
        string? detail;
        lock (ExitGate)
        {
            reason = _exitReason;
            code = _exitCode;
            detail = _exitDetail;
        }

        Log(ExitTrail.Format(reason, code, Uptime.Elapsed, detail));
    }

    private static string Describe(object? exception) => exception switch
    {
        Exception ex => $"{ex.GetType().Name}: {ex.Message}",
        null => "unknown exception",
        _ => exception.ToString() ?? "unknown exception",
    };

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    internal static void Log(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}";
        lock (LogGate)
        {
            Console.WriteLine(line);
            if (_logPath is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private delegate bool ConsoleCtrlDelegate(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);
}
