using System.Runtime.InteropServices;
using Microsoft.Win32;
using EmulatorWorkspace.Core.Engine;

namespace EmulatorWorkspace.Core.Host;

/// <summary>
/// Collects the host facts Phase 0 must record before any boot claim. The
/// engine's own <c>emulator-check accel</c> verdict is treated as authoritative
/// for acceleration; Windows feature state is reported when readable.
/// </summary>
public sealed class HostDiagnostics
{
    private readonly SdkLocator _sdk;

    public HostDiagnostics(SdkLocator sdk)
    {
        _sdk = sdk;
    }

    public async Task<HostReport> CollectAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var memory = MemoryInfo.Get();
        var os = ReadWindowsVersion();
        var gpu = ReadGpu();
        var sdk = await ReadSdkAsync(cancellationToken).ConfigureAwait(false);

        var drive = new DriveInfo(Path.GetPathRoot(workspaceRoot) ?? "C:\\");

        return new HostReport
        {
            CollectedUtc = DateTimeOffset.UtcNow,
            MachineName = Environment.MachineName,
            OperatingSystem = os,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorName = ReadProcessorName(),
            ProcessorCount = Environment.ProcessorCount,
            LogicalProcessors = Environment.ProcessorCount,
            TotalMemoryBytes = memory.TotalPhysical,
            AvailableMemoryBytes = memory.AvailablePhysical,
            WorkspaceRoot = workspaceRoot,
            WorkspaceDriveFreeBytes = drive.AvailableFreeSpace,
            WorkspaceDriveTotalBytes = drive.TotalSize,
            Gpu = gpu,
            AccelerationVerdict = await _sdk.GetAccelerationVerdictAsync(cancellationToken).ConfigureAwait(false),
            HyperV = ReadHyperVSummary(),
            AndroidSdk = sdk,
        };
    }

    private static WindowsVersion ReadWindowsVersion()
    {
        static string Read(string path, string name, string fallback)
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name)?.ToString() ?? fallback;
        }

        const string currentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        return new WindowsVersion(
            Read(currentVersion, "ProductName", "Windows"),
            Read(currentVersion, "DisplayVersion", "unknown"),
            Read(currentVersion, "CurrentBuildNumber", Environment.OSVersion.Version.Build.ToString()),
            Read(currentVersion, "UBR", "0"),
            Read(currentVersion, "EditionID", "unknown"));
    }

    private static string ReadProcessorName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "unknown";
    }

    private static GpuInfo ReadGpu()
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Registry.LocalMachine.OpenSubKey(classKey);
        if (root is null)
        {
            return new GpuInfo("unknown", "unknown", null);
        }

        foreach (var name in root.GetSubKeyNames())
        {
            if (!int.TryParse(name, out _))
            {
                continue;
            }

            using var adapter = root.OpenSubKey(name);
            var description = adapter?.GetValue("DriverDesc")?.ToString();
            if (string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            var version = adapter?.GetValue("DriverVersion")?.ToString() ?? "unknown";
            var date = adapter?.GetValue("DriverDate")?.ToString();
            return new GpuInfo(description, version, date);
        }

        return new GpuInfo("unknown", "unknown", null);
    }

    private static string ReadHyperVSummary()
    {
        // systeminfo is slow but is the only unprivileged source that reports
        // whether a hypervisor is present and which requirements are missing.
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("systeminfo.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return "unavailable";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);

            var lines = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("Virtualization", StringComparison.OrdinalIgnoreCase))
                .Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                .Distinct()
                .ToList();

            return lines.Count == 0 ? "unavailable" : string.Join(" | ", lines);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unavailable: " + ex.Message;
        }
    }

    private async Task<SdkReport> ReadSdkAsync(CancellationToken cancellationToken)
    {
        string emulatorVersion;
        try
        {
            emulatorVersion = await _sdk.GetEmulatorVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            emulatorVersion = "unavailable: " + ex.Message;
        }

        var images = new List<InstalledImage>();
        var systemImagesRoot = Path.Combine(_sdk.SdkRoot, "system-images");
        if (Directory.Exists(systemImagesRoot))
        {
            foreach (var apiDirectory in Directory.EnumerateDirectories(systemImagesRoot))
            {
                foreach (var tagDirectory in Directory.EnumerateDirectories(apiDirectory))
                {
                    foreach (var abiDirectory in Directory.EnumerateDirectories(tagDirectory))
                    {
                        var systemImage = Path.Combine(abiDirectory, "system.img");
                        if (!File.Exists(systemImage))
                        {
                            continue;
                        }

                        images.Add(new InstalledImage(
                            $"system-images;{Path.GetFileName(apiDirectory)};{Path.GetFileName(tagDirectory)};{Path.GetFileName(abiDirectory)}",
                            ReadRevision(Path.Combine(abiDirectory, "source.properties")),
                            new FileInfo(systemImage).Length));
                    }
                }
            }
        }

        string adbVersion;
        try
        {
            var result = await ProcessRunner.RunAsync(
                _sdk.AdbExe,
                new[] { "version" },
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            adbVersion = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? "unknown";
        }
        catch (Exception ex)
        {
            adbVersion = "unavailable: " + ex.Message;
        }

        return new SdkReport(
            _sdk.SdkRoot,
            _sdk.EmulatorExe,
            emulatorVersion,
            _sdk.AdbExe,
            adbVersion,
            images.OrderBy(i => i.PackageId, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string ReadRevision(string sourceProperties)
    {
        if (!File.Exists(sourceProperties))
        {
            return "unknown";
        }

        foreach (var line in File.ReadAllLines(sourceProperties))
        {
            if (line.StartsWith("Pkg.Revision=", StringComparison.Ordinal))
            {
                return line["Pkg.Revision=".Length..].Trim();
            }
        }

        return "unknown";
    }

    private static class MemoryInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        public static (ulong TotalPhysical, ulong AvailablePhysical) Get()
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status)
                ? (status.TotalPhysical, status.AvailablePhysical)
                : (0, 0);
        }
    }
}

public sealed class HostReport
{
    public DateTimeOffset CollectedUtc { get; set; }

    public string MachineName { get; set; } = "";

    public WindowsVersion OperatingSystem { get; set; } = new("Windows", "unknown", "0", "0", "unknown");

    public string Architecture { get; set; } = "";

    public string ProcessArchitecture { get; set; } = "";

    public string ProcessorName { get; set; } = "";

    /// <summary>Logical processors reported by the OS.</summary>
    public int ProcessorCount { get; set; }

    public int LogicalProcessors { get; set; }

    public ulong TotalMemoryBytes { get; set; }

    public ulong AvailableMemoryBytes { get; set; }

    public string WorkspaceRoot { get; set; } = "";

    public long WorkspaceDriveFreeBytes { get; set; }

    public long WorkspaceDriveTotalBytes { get; set; }

    public GpuInfo Gpu { get; set; } = new("unknown", "unknown", null);

    public string AccelerationVerdict { get; set; } = "";

    public string HyperV { get; set; } = "";

    public SdkReport AndroidSdk { get; set; } = new("", "", "", "", "", new List<InstalledImage>());

    public double TotalMemoryGiB => TotalMemoryBytes / 1024d / 1024d / 1024d;

    public double AvailableMemoryGiB => AvailableMemoryBytes / 1024d / 1024d / 1024d;

    public double WorkspaceDriveFreeGiB => WorkspaceDriveFreeBytes / 1024d / 1024d / 1024d;
}

public sealed record WindowsVersion(string ProductName, string DisplayVersion, string Build, string Ubr, string Edition)
{
    // Windows 11 still reports ProductName "Windows 10" in the registry; the
    // build number is the reliable discriminator (22000 is the 11 baseline).
    public string Full
    {
        get
        {
            var name = ProductName;
            if (int.TryParse(Build, out var build) && build >= 22000
                && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                name = "Windows 11" + name["Windows 10".Length..];
            }

            return $"{name} {DisplayVersion} (build {Build}.{Ubr}) {Edition}";
        }
    }
}

public sealed record GpuInfo(string Name, string DriverVersion, string? DriverDate);

public sealed record InstalledImage(string PackageId, string Revision, long SystemImageBytes);

public sealed record SdkReport(
    string Root,
    string EmulatorPath,
    string EmulatorVersion,
    string AdbPath,
    string AdbVersion,
    IReadOnlyList<InstalledImage> Images);
