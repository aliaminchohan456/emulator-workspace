using System.Text;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Storage;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Supervisor;

internal static class Program
{
    private static readonly object LogGate = new();
    private static string? _logPath;

    private static async Task<int> Main(string[] args)
    {
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
            return 2;
        }

        _logPath = Path.Combine(paths.LogsDirectory, "supervisor.log");
        Log($"Supervisor starting (pid {Environment.ProcessId}, session {System.Diagnostics.Process.GetCurrentProcess().SessionId}).");
        Log($"Root: {paths.Root}");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Log("Shutdown requested from console.");
            shutdown.Cancel();
        };

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
            var adopted = await manager.AdoptAsync(shutdown.Token).ConfigureAwait(false);
            Log("Adoption: " + adopted.ToJsonString());

            OperationDispatcher dispatcher;
            SupervisorPipeServer server;
            dispatcher = new OperationDispatcher(paths, sdk, store, manager, journal, agents, library, () =>
            {
                Log("Shutdown requested by client.");
                shutdown.Cancel();
            });
            server = new SupervisorPipeServer(RootIdentity.PipeName(rootLock.CanonicalRoot), dispatcher, manager, Log);

            Log($"Ready. Database: {store.DatabasePath}");
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
            return 1;
        }
        finally
        {
            rootLock.Dispose();
        }

        return 0;
    }

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
}
