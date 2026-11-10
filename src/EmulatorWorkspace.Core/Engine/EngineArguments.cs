using System.Diagnostics;
using System.Globalization;
using EmulatorWorkspace.Core.Instances;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>
/// Pure argument construction for the pinned engine. Kept separate from process
/// launch so the exact command line is testable and reproducible.
/// </summary>
public static class EngineArguments
{
    public const string DefaultGpuMode = "host";

    public static IReadOnlyList<string> Build(
        InstanceRecord record,
        bool headless,
        string gpuMode,
        bool coldBoot,
        bool verbose = true,
        bool grpc = true)
    {
        var args = new List<string>
        {
            "-avd", record.AvdName,
            "-port", record.ConsolePort.ToString(CultureInfo.InvariantCulture),
            "-no-audio",
            "-no-boot-anim",
            "-no-metrics",
            "-accel", "on",
            "-gpu", gpuMode,
        };

        if (!string.IsNullOrWhiteSpace(record.RamdiskPath))
        {
            args.Add("-ramdisk");
            args.Add(record.RamdiskPath);
        }

        if (headless)
        {
            args.Add("-no-window");
        }

        if (coldBoot)
        {
            args.Add("-no-snapshot-load");
        }

        // Never write an implicit exit snapshot; named snapshots stay explicit
        // and budgeted so two instances cannot silently consume the disk.
        args.Add("-no-snapshot-save");

        if (grpc)
        {
            args.Add("-grpc");
            args.Add(record.GrpcPort.ToString(CultureInfo.InvariantCulture));
            args.Add("-grpc-use-token");
        }

        if (verbose)
        {
            args.Add("-verbose");
        }

        return args;
    }
}

public static class ProcessIdentity
{
    /// <summary>
    /// Verifies that the process we are about to signal is the same process we
    /// launched, by comparing PID plus its creation timestamp. PID reuse alone
    /// must never be treated as proof of identity.
    /// </summary>
    public static bool IsSameProcess(int? pid, string? startTimeUtc)
    {
        if (pid is null or <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            if (process.HasExited)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(startTimeUtc))
            {
                return true;
            }

            if (!DateTimeOffset.TryParse(
                    startTimeUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var recorded))
            {
                return false;
            }

            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return Math.Abs((actual - recorded).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static string? CaptureStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)
                .ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
