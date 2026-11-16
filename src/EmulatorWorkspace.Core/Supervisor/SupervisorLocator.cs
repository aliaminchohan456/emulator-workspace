using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using EmulatorWorkspace.Core.Engine;

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

    /// <summary>
    /// Starts the supervisor as a detached, hidden process.
    ///
    /// EV-11b: the launch is CreateProcess with CREATE_BREAKAWAY_FROM_JOB,
    /// CREATE_NEW_PROCESS_GROUP and CREATE_NO_WINDOW, so a process-tree kill of
    /// whatever shell asked for the supervisor (an editor's tool host, an agent
    /// runner, a build step) does not take the supervisor and its guests with
    /// it. Windows refuses that flag when the caller's job forbids breakaway;
    /// the launch is then retried once without it, exactly as before, and the
    /// caller is told once through <paramref name="log"/>. No shell is used, so
    /// the command line is the only thing that decides what runs.
    /// </summary>
    public static Process? StartDetached(string root, string? executable = null, Action<string>? log = null)
    {
        var (fileName, prefixArgs) = ResolveLaunchTarget(executable);
        var arguments = new List<string>(prefixArgs)
        {
            "--root",
            RootIdentity.Canonicalize(root),
        };

        var breakaway = true;
        while (true)
        {
            var pid = CreateDetached(fileName, arguments, SupervisorLaunch.Flags(breakaway), out var error);
            if (pid > 0)
            {
                try
                {
                    return Process.GetProcessById(pid);
                }
                catch (ArgumentException)
                {
                    // The child was created and exited before we could open it.
                    return null;
                }
            }

            if (SupervisorLaunch.NextAttempt(error, breakaway) != LaunchRetry.WithoutBreakaway)
            {
                throw new Win32Exception(error, $"Could not start '{fileName}' (error {error}).");
            }

            log?.Invoke(SupervisorLaunch.FallbackNote(error));
            breakaway = false;
        }
    }

    /// <summary>
    /// One CreateProcess attempt. Returns the child's pid, or 0 with the Win32
    /// error code. The child inherits the caller's environment and standard
    /// handles are not passed, because the supervisor owns its own log file.
    /// </summary>
    private static int CreateDetached(
        string fileName,
        IReadOnlyList<string> arguments,
        uint flags,
        out int error)
    {
        var commandLine = new StringBuilder(NativeProcess.BuildCommandLine(fileName, arguments));
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var created = CreateProcessW(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            flags,
            IntPtr.Zero,
            AppContext.BaseDirectory,
            ref startup,
            out var process);

        if (!created)
        {
            error = Marshal.GetLastWin32Error();
            return 0;
        }

        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        error = 0;
        return checked((int)process.dwProcessId);
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

        var process = StartDetached(root, log: log);
        log?.Invoke(process is null
            ? "Supervisor process did not start."
            : $"Started supervisor pid {process.Id} for {RootIdentity.Canonicalize(root)}.");
        process?.Dispose();

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
        => ResolveLaunchTargetFrom(AppContext.BaseDirectory,
            explicitPath ?? Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable));

    public static (string FileName, IReadOnlyList<string> Args) ResolveLaunchTargetFrom(string baseDirectory, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            if (!File.Exists(candidate))
                throw new SupervisorUnavailableException("Explicit supervisor path does not exist: " + candidate);
            return candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? ("dotnet", new[] { candidate }) : (candidate, Array.Empty<string>());
        }

        var sameDirectoryExe = Path.Combine(baseDirectory, "EmulatorWorkspace.Supervisor.exe");
        if (File.Exists(sameDirectoryExe))
        {
            return (sameDirectoryExe, Array.Empty<string>());
        }

        var repo = FindRepositoryRoot(baseDirectory);
        if (repo is not null)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed class SupervisorUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
