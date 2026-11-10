using System.Diagnostics;
using System.IO.Pipes;

namespace EmulatorWorkspace.Core.Supervisor;

public static class SupervisorLocator
{
    public const string ExecutableEnvironmentVariable = "EMULATORWORKSPACE_SUPERVISOR_EXE";

    public static string PipeNameFor(string root) => RootIdentity.PipeName(RootIdentity.Canonicalize(root));

    public static NamedPipeClientStream CreateClient(string root) =>
        new(".", PipeNameFor(root), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public static async Task<NamedPipeClientStream?> TryConnectAsync(
        string root,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var client = CreateClient(root);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(400));
                await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                last = ex;
                client.Dispose();
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }

        _ = last;
        return null;
    }

    /// <summary>Starts the supervisor as a detached, hidden process.</summary>
    public static Process? StartDetached(string root, string? executable = null)
    {
        var (fileName, prefixArgs) = ResolveLaunchTarget(executable);
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var argument in prefixArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(RootIdentity.Canonicalize(root));

        return Process.Start(startInfo);
    }

    public static async Task EnsureRunningAsync(
        string root,
        TimeSpan timeout,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await TryConnectAsync(root, TimeSpan.FromMilliseconds(300), cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.Dispose();
            return;
        }

        var process = StartDetached(root);
        log?.Invoke(process is null
            ? "Supervisor process did not start."
            : $"Started supervisor pid {process.Id} for {RootIdentity.Canonicalize(root)}.");

        var connected = await TryConnectAsync(root, timeout, cancellationToken).ConfigureAwait(false);
        if (connected is null)
        {
            throw new SupervisorUnavailableException(
                $"The supervisor for '{RootIdentity.Canonicalize(root)}' did not become reachable within {timeout.TotalSeconds:N0}s. " +
                "Check supervisor.log in the workspace root, or start it manually.");
        }

        connected.Dispose();
    }

    public static (string FileName, IReadOnlyList<string> Args) ResolveLaunchTarget(string? explicitPath = null)
    {
        var candidate = explicitPath
                        ?? Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
        {
            return (candidate, Array.Empty<string>());
        }

        var baseDirectory = AppContext.BaseDirectory;
        var sameDirectoryExe = Path.Combine(baseDirectory, "EmulatorWorkspace.Supervisor.exe");
        if (File.Exists(sameDirectoryExe))
        {
            return (sameDirectoryExe, Array.Empty<string>());
        }

        var repo = FindRepositoryRoot(baseDirectory);
        if (repo is not null)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var exe = Path.Combine(repo, "src", "EmulatorWorkspace.Supervisor", "bin", configuration,
                    "net10.0-windows", "EmulatorWorkspace.Supervisor.exe");
                if (File.Exists(exe))
                {
                    return (exe, Array.Empty<string>());
                }

                var dll = Path.Combine(repo, "src", "EmulatorWorkspace.Supervisor", "bin", configuration,
                    "net10.0-windows", "EmulatorWorkspace.Supervisor.dll");
                if (File.Exists(dll))
                {
                    return ("dotnet", new[] { dll });
                }
            }
        }

        throw new SupervisorUnavailableException(
            "Could not locate EmulatorWorkspace.Supervisor. Build the solution or set " +
            ExecutableEnvironmentVariable + ".");
    }

    public static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmulatorWorkspace.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

public sealed class SupervisorUnavailableException(string message) : Exception(message);
