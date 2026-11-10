using EmulatorWorkspace.Contracts;
namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Free-space guard for create/start/snapshot. Tests may inject a fixed value;
/// production always reads the real volume.
/// </summary>
public static class DiskSpaceGuard
{
    private static readonly object Gate = new();
    private static Func<string, long>? _override;

    public static void SetOverrideForTests(Func<string, long>? provider)
    {
        lock (Gate)
        {
            _override = provider;
        }
    }

    public static void Ensure(string workspaceRoot, long requiredBytes, string action)
    {
        var root = Path.GetPathRoot(workspaceRoot) ?? "C:\\";
        long available;
        lock (Gate)
        {
            available = _override?.Invoke(root) ?? new DriveInfo(root).AvailableFreeSpace;
        }

        if (available < requiredBytes)
        {
            throw new SupervisorOperationException(
                ErrorCodes.LowDisk,
                $"Not enough free space on {root} to {action}: " +
                $"{available / 1024d / 1024 / 1024:N1} GiB free, " +
                $"{requiredBytes / 1024d / 1024 / 1024:N0} GiB required. " +
                "Free space or move the workspace root.");
        }
    }
}