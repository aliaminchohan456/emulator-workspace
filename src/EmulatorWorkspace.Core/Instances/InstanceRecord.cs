namespace EmulatorWorkspace.Core.Instances;

public enum InstanceState
{
    Defined,
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
    Unknown,
}

/// <summary>
/// Durable description of one managed emulator instance. Everything needed to
/// re-attach to or safely stop the instance lives here; nothing is inferred from
/// the global device list.
/// </summary>
public sealed class InstanceRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required string Name { get; set; }

    public required string AvdName { get; set; }

    public required string ImagePackage { get; set; }

    public required string DeviceProfile { get; set; }

    public required string AvdHome { get; set; }

    /// <summary>Emulator console port, always even. ADB uses ConsolePort + 1.</summary>
    public required int ConsolePort { get; set; }

    /// <summary>Dedicated gRPC controller port for this instance.</summary>
    public required int GrpcPort { get; set; }

    public InstanceState State { get; set; } = InstanceState.Defined;

    public int? EnginePid { get; set; }

    /// <summary>UTC creation timestamp of the engine process, used to reject PID reuse.</summary>
    public string? EngineStartTimeUtc { get; set; }

    /// <summary>Process id of the engine host that owns the engine's stdio.</summary>
    public int? HostPid { get; set; }

    /// <summary>UTC creation timestamp of the engine host, used to reject PID reuse.</summary>
    public string? HostStartTimeUtc { get; set; }

    public string? EnginePath { get; set; }

    public string? EngineVersion { get; set; }

    public string? EngineArguments { get; set; }

    public string? GpuMode { get; set; }

    /// <summary>
    /// Optional replacement ramdisk passed to the engine with <c>-ramdisk</c>.
    /// This is the insertion point for the Magisk lab: the engine regenerates
    /// its per-AVD initrd from this file on every launch.
    /// </summary>
    public string? RamdiskPath { get; set; }

    public bool Headless { get; set; } = true;

    public int? LastExitCode { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string AdbSerial => $"emulator-{ConsolePort}";
}
