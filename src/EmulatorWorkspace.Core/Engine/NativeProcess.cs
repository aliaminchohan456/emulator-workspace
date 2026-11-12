using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>
/// Launches a process with stdout/stderr redirected to a file using inherited
/// handles. Unlike a pipe pump this keeps the child alive and logging even if
/// the supervisor exits unexpectedly, which is what makes safe reattachment
/// possible.
/// </summary>
public static class NativeProcess
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static LaunchedProcess Start(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        string logPath,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInputPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var attributes = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true,
        };

        var logHandle = CreateFileW(
            logPath,
            GenericWrite,
            FileShareRead | FileShareWrite | FileShareDelete,
            ref attributes,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (logHandle == IntPtr.Zero || logHandle == new IntPtr(-1))
        {
            logHandle = CreateFileW(
                logPath,
                GenericWrite,
                FileShareRead | FileShareWrite | FileShareDelete,
                ref attributes,
                2 /* CREATE_ALWAYS */,
                FileAttributeNormal,
                IntPtr.Zero);
        }

        if (logHandle == IntPtr.Zero || logHandle == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the engine log file.");
        }

        var nullHandle = CreateFileW(
            "NUL",
            0x80000000 | GenericWrite,
            FileShareRead | FileShareWrite,
            ref attributes,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (nullHandle == IntPtr.Zero || nullHandle == new IntPtr(-1))
        {
            nullHandle = CreateFileW("NUL", 0x80000000 | GenericWrite, FileShareRead | FileShareWrite,
                ref attributes, OpenExisting, 0, IntPtr.Zero);
        }

        var startup = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = StartfUseStdHandles,
            hStdOutput = logHandle,
            hStdError = logHandle,
            hStdInput = nullHandle,
        };

        var commandLine = BuildCommandLine(executable, arguments);
        var environmentBlock = BuildEnvironmentBlock(environment);

        try
        {
            var created = CreateProcessW(
                executable,
                new StringBuilder(commandLine),
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateNoWindow | CreateUnicodeEnvironment,
                environmentBlock,
                workingDirectory,
                ref startup,
                out var processInfo);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{executable}'.");
            }

            var pid = checked((int)processInfo.dwProcessId);
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            return new LaunchedProcess(pid, commandLine, logPath);
        }
        finally
        {
            CloseHandle(logHandle);
            CloseHandle(nullHandle);
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    public static bool TryGetStartTimeUtc(int pid, out DateTimeOffset start)
    {
        start = default;
        try
        {
            using var process = Process.GetProcessById(pid);
            start = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static bool IsAlive(int? pid, string? recordedStartUtc)
    {
        if (pid is null or <= 0)
        {
            return false;
        }

        if (!TryGetStartTimeUtc(pid.Value, out var actual))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(recordedStartUtc))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(recordedStartUtc, out var recorded))
        {
            return false;
        }

        return Math.Abs((actual - recorded).TotalSeconds) < 2;
    }

    public static void KillTree(int pid, string? recordedStartUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || !DateTimeOffset.TryParse(recordedStartUtc, out var recorded)
                || Math.Abs((process.StartTime.ToUniversalTime() - recorded.UtcDateTime).TotalSeconds) >= 2)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    public static string BuildCommandLine(string executable, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder(Quote(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return value;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            values[entry.Key!.ToString()!] = entry.Value?.ToString() ?? string.Empty;
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }

        var builder = new StringBuilder();
        foreach (var (key, value) in values)
        {
            builder.Append(key).Append('=').Append(value).Append('\0');
        }

        builder.Append('\0');
        var text = builder.ToString();
        var pointer = Marshal.StringToHGlobalUni(text);
        return pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
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
        string lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SecurityAttributes lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed record LaunchedProcess(int Pid, string CommandLine, string LogPath);
