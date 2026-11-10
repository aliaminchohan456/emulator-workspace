using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Verifies that a pipe client is a live process in the supervisor's own logon
/// session and runs as the same Windows user. Remote or cross-session clients
/// are rejected before any request is read.
/// </summary>
public static class PipePeer
{
    public static PeerInfo Verify(Microsoft.Win32.SafeHandles.SafePipeHandle handle)
    {
        if (!GetNamedPipeClientProcessId(handle, out var pid))
        {
            throw new UnauthorizedAccessException(
                new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the pipe client process.").Message);
        }

        if (!ProcessIdToSessionId((uint)pid, out var session))
        {
            throw new UnauthorizedAccessException("Could not resolve the pipe client session.");
        }

        var currentSession = Process.GetCurrentProcess().SessionId;
        if (session != currentSession)
        {
            throw new UnauthorizedAccessException(
                $"Pipe client session {session} does not match supervisor session {currentSession}.");
        }

        var clientSid = GetProcessUserSid((uint)pid);
        var currentSid = WindowsIdentity.GetCurrent().User;
        if (currentSid is null || clientSid is null || !string.Equals(clientSid, currentSid.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Pipe client runs as a different Windows user.");
        }

        string name;
        try
        {
            name = Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            name = "unknown";
        }

        return new PeerInfo((int)pid, session, currentSid.Value, name);
    }

    private static string? GetProcessUserSid(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(handle, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                return ReadTokenSid(token);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? ReadTokenSid(IntPtr token)
    {
        GetTokenInformation(token, TokenUserInformation, IntPtr.Zero, 0, out var size);
        if (size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetTokenInformation(token, TokenUserInformation, buffer, size, out _))
            {
                return null;
            }

            var user = Marshal.PtrToStructure<TokenUser>(buffer);
            return new SecurityIdentifier(user.User.Sid).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserInformation = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed record PeerInfo(int ProcessId, uint SessionId, string UserSid, string ProcessName);
