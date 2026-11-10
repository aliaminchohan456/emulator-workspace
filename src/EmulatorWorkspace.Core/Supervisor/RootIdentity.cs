using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Resolves the one canonical identity for a workspace root. Junction points,
/// subst drives, trailing separators, and case differences must all collapse to
/// the same supervisor owner and the same named pipe.
/// </summary>
public static class RootIdentity
{
    public static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            using var handle = CreateFileW(
                full,
                0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return Normalize(full);
            }

            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                return Normalize(full);
            }

            var final = buffer.ToString();
            const string uncPrefix = @"\\?\";
            if (final.StartsWith(uncPrefix, StringComparison.Ordinal))
            {
                final = final[uncPrefix.Length..];
            }

            return Normalize(final);
        }
        catch (Exception)
        {
            return Normalize(full);
        }
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();

    public static string Hash(string canonicalRoot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string PipeName(string canonicalRoot) =>
        "emulatorworkspace-" + Hash(canonicalRoot);

    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        StringBuilder path,
        int pathLength,
        uint flags);
}

public sealed class RootLock : IDisposable
{
    private readonly FileStream _stream;

    private RootLock(FileStream stream, string canonicalRoot)
    {
        _stream = stream;
        CanonicalRoot = canonicalRoot;
    }

    public string CanonicalRoot { get; }

    public static RootLock Acquire(string root)
    {
        var canonical = RootIdentity.Canonicalize(root);
        Directory.CreateDirectory(canonical);
        var lockPath = Path.Combine(canonical, "supervisor.lock");

        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);

            var owner = new
            {
                pid = Environment.ProcessId,
                session = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                user = Environment.UserDomainName + "\\" + Environment.UserName,
                startedUtc = DateTimeOffset.UtcNow.ToString("O"),
                canonicalRoot = canonical,
            };
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(owner, new JsonSerializerOptions { WriteIndented = true }));
            stream.SetLength(0);
            stream.Write(payload);
            stream.Flush(true);

            return new RootLock(stream, canonical);
        }
        catch (IOException ex)
        {
            var owner = ReadOwner(lockPath);
            throw new SupervisorOwnedException(
                $"Workspace root '{canonical}' already has a supervisor owner ({owner}).",
                ex);
        }
    }

    public static string ReadOwner(string lockPath)
    {
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? "unknown owner" : text.Replace(Environment.NewLine, " ");
        }
        catch (IOException)
        {
            return "unknown owner";
        }
    }

    public void Dispose() => _stream.Dispose();
}

public sealed class SupervisorOwnedException(string message, Exception? inner = null)
    : Exception(message, inner);
