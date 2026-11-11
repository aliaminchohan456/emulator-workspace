using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Controller;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Storage;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Core.Supervisor;

public sealed record ReadyStages(
    DateTimeOffset LaunchedUtc,
    DateTimeOffset? AdbOnlineUtc,
    DateTimeOffset? BootCompletedUtc,
    DateTimeOffset? InteractiveUtc)
{
    public double? LaunchedToBootSeconds =>
        BootCompletedUtc is null ? null : (BootCompletedUtc.Value - LaunchedUtc).TotalSeconds;

    public double? LaunchedToInteractiveSeconds =>
        InteractiveUtc is null ? null : (InteractiveUtc.Value - LaunchedUtc).TotalSeconds;
}

/// <summary>
/// The single lifecycle owner for every managed instance in one workspace root.
/// All engine processes, port reservations, controller credentials, and
/// shutdown paths live here; callers only see typed operations.
/// </summary>
public sealed class InstanceManager
{
    private const long StartDiskRequirementBytes = 8L * 1024 * 1024 * 1024;
    private const long SnapshotDiskRequirementBytes = 3L * 1024 * 1024 * 1024;

    private readonly WorkspacePaths _paths;
    private readonly SdkLocator _sdk;
    private readonly SupervisorStore _store;
    private readonly SupervisorEventHub _hub;
    private readonly AvdProvisioner _provisioner;
    private readonly AdbClient _adb;
    private readonly ConcurrentDictionary<string, Runtime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Instance, string Kind)> _subscriptions = new(StringComparer.Ordinal);
    private readonly string _frameDirectory;

    public InstanceManager(
        WorkspacePaths paths,
        SdkLocator sdk,
        SupervisorStore store,
        SupervisorEventHub hub)
    {
        _paths = paths;
        _sdk = sdk;
        _store = store;
        _hub = hub;
        _provisioner = new AvdProvisioner(sdk);
        _adb = new AdbClient(sdk.AdbExe);
        _frameDirectory = Path.Combine(paths.Root, "frames");
        Directory.CreateDirectory(_frameDirectory);
    }

    public SupervisorEventHub Hub => _hub;

    public string FrameDirectory => _frameDirectory;

    private sealed class Runtime(InstanceRecord record)
    {
        public InstanceRecord Record { get; set; } = record;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public FramePump? Frames { get; set; }

        public LogcatPump? Logcat { get; set; }

        public ReadyStages? Ready { get; set; }

        public DateTimeOffset? AdbOnlineSeenUtc { get; set; }
    }

    public IReadOnlyList<InstanceRecord> List() => _store.ListInstances();

    public InstanceRecord Get(string name) =>
        _store.GetInstance(name) ?? throw new SupervisorOperationException(
            ErrorCodes.NotFound, $"No managed instance named '{name}' exists in this workspace root.");

    /// <summary>
    /// Reconciles persisted records with reality at supervisor start. A guest is
    /// adopted only when the process identity and the engine's advertised
    /// instance metadata both match; otherwise it is left untouched and marked
    /// Unknown so an operator can decide.
    /// </summary>
    public async Task<JsonObject> AdoptAsync(CancellationToken cancellationToken)
    {
        var adopted = new JsonArray();
        var stopped = new JsonArray();
        var unverified = new JsonArray();

        foreach (var record in _store.ListInstances())
        {
            var runtime = _runtimes.GetOrAdd(record.Name, _ => new Runtime(record));
            runtime.Record = record;

            if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
            {
                if (record.EnginePid is not null)
                {
                    record.State = InstanceState.Stopped;
                    record.EnginePid = null;
                    record.EngineStartTimeUtc = null;
                    record.LastError = null;
                    _store.UpsertInstance(record);
                }

                stopped.Add(record.Name);
                continue;
            }

            try
            {
                var discovery = await EmulatorDiscovery.ReadAsync(_adb, record.AdbSerial, cancellationToken)
                    .ConfigureAwait(false);
                var matches = discovery.AvdName == record.AvdName
                              && discovery.AdbPort == record.ConsolePort + 1
                              && discovery.GrpcPort == record.GrpcPort;

                if (!matches)
                {
                    throw new InvalidOperationException(
                        $"Advertised avd={discovery.AvdName} adb={discovery.AdbPort} grpc={discovery.GrpcPort} " +
                        $"does not match the record (avd={record.AvdName}, adb={record.ConsolePort + 1}, grpc={record.GrpcPort}).");
                }

                var boot = await _adb.GetPropAsync(record.AdbSerial, "sys.boot_completed", cancellationToken)
                    .ConfigureAwait(false);
                record.State = boot.Trim() == "1" ? InstanceState.Running : InstanceState.Starting;
                record.LastError = null;
                _store.UpsertInstance(record);
                adopted.Add(new JsonObject
                {
                    ["name"] = record.Name,
                    ["pid"] = record.EnginePid,
                    ["state"] = record.State.ToString(),
                });
            }
            catch (Exception ex)
            {
                record.State = InstanceState.Unknown;
                record.LastError = "Process is alive but could not be verified: " + ex.Message;
                _store.UpsertInstance(record);
                unverified.Add(new JsonObject
                {
                    ["name"] = record.Name,
                    ["pid"] = record.EnginePid,
                    ["reason"] = ex.Message,
                });
            }
        }

        return new JsonObject
        {
            ["adopted"] = adopted,
            ["stopped"] = stopped,
            ["unverified"] = unverified,
        };
    }

    public async Task<InstanceRecord> CreateAsync(
        string name,
        string? image,
        string? device,
        int? consolePort,
        int? grpcPort,
        string? gpu,
        bool headless,
        CancellationToken cancellationToken)
    {
        ValidateName(name);
        if (_store.GetInstance(name) is not null)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, $"Instance '{name}' already exists.", retryable: false);
        }

        var imageId = SdkLocator.NormalizeImageId(image ?? "system-images;android-35;default;x86_64");
        if (!_sdk.IsImageInstalled(imageId))
        {
            throw new SupervisorOperationException(
                ErrorCodes.ImageMissing,
                $"System image '{imageId}' is not installed. Install it with: sdkmanager \"{imageId}\"");
        }

        EnsureDiskFree(StartDiskRequirementBytes, "create an instance");

        var existing = _store.ListInstances();
        if (consolePort is null || grpcPort is null)
        {
            var allocated = PortAllocator.Allocate(existing);
            consolePort ??= allocated.ConsolePort;
            grpcPort ??= allocated.GrpcPort;
        }

        if (consolePort % 2 != 0)
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "The console port must be even.");
        }

        if (existing.Any(r => r.ConsolePort == consolePort || r.GrpcPort == grpcPort))
        {
            throw new SupervisorOperationException(
                ErrorCodes.PortConflict, "The requested port is already owned by another managed instance.");
        }

        if (!PortAllocator.IsPortFree(consolePort.Value) || !PortAllocator.IsPortFree(consolePort.Value + 1)
            || !PortAllocator.IsPortFree(grpcPort.Value))
        {
            throw new SupervisorOperationException(
                ErrorCodes.PortConflict,
                $"Port trio {consolePort}/{consolePort + 1}/{grpcPort} is already in use by another process. " +
                "Close the process or choose different ports.");
        }

        var avdName = "ws_" + name;
        await _provisioner.CreateAsync(
            avdName, imageId, device ?? "pixel_6", _paths.AvdHome,
            gpu ?? EngineArguments.DefaultGpuMode, cancellationToken).ConfigureAwait(false);

        var record = new InstanceRecord
        {
            Name = name,
            AvdName = avdName,
            ImagePackage = imageId,
            DeviceProfile = device ?? "pixel_6",
            AvdHome = _paths.AvdHome,
            ConsolePort = consolePort.Value,
            GrpcPort = grpcPort.Value,
            State = InstanceState.Defined,
            Headless = headless,
            GpuMode = gpu ?? EngineArguments.DefaultGpuMode,
            EngineVersion = await SafeVersionAsync(cancellationToken).ConfigureAwait(false),
        };

        _store.UpsertInstance(record);
        _runtimes[record.Name] = new Runtime(record);
        return record;
    }

    public async Task<JsonObject> StartAsync(
        string name,
        string? gpu,
        bool? headless,
        bool coldBoot,
        string? ramdisk,
        CancellationToken cancellationToken)
    {
        var runtime = RuntimeFor(name);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = runtime.Record;
            if (NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
            {
                throw new SupervisorOperationException(
                    ErrorCodes.AlreadyRunning,
                    $"Instance '{name}' is already running (pid {record.EnginePid}).");
            }

            if (!_sdk.IsImageInstalled(record.ImagePackage))
            {
                throw new SupervisorOperationException(
                    ErrorCodes.ImageMissing,
                    $"System image '{record.ImagePackage}' is not installed. Reinstall the pinned image.");
            }

            var avdConfig = Path.Combine(record.AvdHome, record.AvdName + ".avd", "config.ini");
            if (!File.Exists(avdConfig))
            {
                throw new SupervisorOperationException(
                    ErrorCodes.NotFound,
                    $"AVD '{record.AvdName}' is missing from {record.AvdHome}. Recreate the instance.");
            }

            EnsureDiskFree(StartDiskRequirementBytes, "start a guest");

            foreach (var port in new[] { record.ConsolePort, record.ConsolePort + 1, record.GrpcPort })
            {
                if (!PortAllocator.IsPortFree(port))
                {
                    throw new SupervisorOperationException(
                        ErrorCodes.PortConflict,
                        $"Port {port} is already in use. Another emulator or process owns it; " +
                        "stop that process or recreate the instance with different ports.");
                }
            }

            var effectiveGpu = gpu ?? record.GpuMode ?? EngineArguments.DefaultGpuMode;
            var effectiveHeadless = headless ?? record.Headless;
            if (ramdisk is not null)
            {
                if (ramdisk.Equals("stock", StringComparison.OrdinalIgnoreCase)
                    || ramdisk.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    record.RamdiskPath = null;
                }
                else
                {
                    var full = Path.GetFullPath(ramdisk);
                    if (!File.Exists(full))
                    {
                        throw new SupervisorOperationException(
                            ErrorCodes.InvalidRequest, $"Replacement ramdisk '{full}' does not exist.");
                    }

                    record.RamdiskPath = full;
                }
            }

            record.GpuMode = effectiveGpu;
            record.Headless = effectiveHeadless;
            record.State = InstanceState.Starting;
            record.LastError = null;
            record.EnginePid = null;
            record.EngineStartTimeUtc = null;

            var arguments = EngineArguments.Build(record, effectiveHeadless, effectiveGpu, coldBoot);
            var logPath = _paths.InstanceLogPath(record.Name);
            var launched = NativeProcess.Start(
                _sdk.EmulatorExe,
                arguments,
                _sdk.SdkRoot,
                logPath,
                new Dictionary<string, string>
                {
                    ["ANDROID_AVD_HOME"] = record.AvdHome,
                    ["ANDROID_SDK_ROOT"] = _sdk.SdkRoot,
                });

            var startedUtc = NativeProcess.TryGetStartTimeUtc(launched.Pid, out var actual)
                ? actual
                : DateTimeOffset.UtcNow;

            record.EnginePid = launched.Pid;
            record.EngineStartTimeUtc = startedUtc.ToString("O", CultureInfo.InvariantCulture);
            record.EnginePath = _sdk.EmulatorExe;
            record.EngineArguments = launched.CommandLine;
            record.EngineVersion = await SafeVersionAsync(cancellationToken).ConfigureAwait(false);
            _store.UpsertInstance(record);

            runtime.Ready = new ReadyStages(startedUtc, null, null, null);
            runtime.AdbOnlineSeenUtc = null;

            await _hub.PublishAsync(name, "state", EventNames.State, new JsonObject
            {
                ["state"] = record.State.ToString(),
                ["pid"] = launched.Pid,
                ["serial"] = record.AdbSerial,
            }).ConfigureAwait(false);

            return new JsonObject
            {
                ["name"] = record.Name,
                ["pid"] = launched.Pid,
                ["serial"] = record.AdbSerial,
                ["consolePort"] = record.ConsolePort,
                ["grpcPort"] = record.GrpcPort,
                ["gpu"] = effectiveGpu,
                ["headless"] = effectiveHeadless,
                ["launchedUtc"] = startedUtc.ToString("O"),
                ["logPath"] = logPath,
            };
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    public async Task<JsonObject> WaitAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var runtime = RuntimeFor(name);
        var record = runtime.Record;
        if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
        {
            throw new SupervisorOperationException(
                ErrorCodes.NotRunning, $"Instance '{name}' has no live engine process.");
        }

        var launched = runtime.Ready?.LaunchedUtc
                       ?? (DateTimeOffset.TryParse(record.EngineStartTimeUtc, out var parsed) ? parsed : DateTimeOffset.UtcNow);
        var stages = runtime.Ready ?? new ReadyStages(launched, null, null, null);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
            {
                record.State = InstanceState.Failed;
                record.LastError = "Engine exited before Android reached boot completion.";
                _store.UpsertInstance(record);
                throw new SupervisorOperationException(
                    ErrorCodes.NotRunning, record.LastError + $" See {_paths.InstanceLogPath(name)}.");
            }

            try
            {
                var state = await _adb.GetStateAsync(record.AdbSerial, cancellationToken).ConfigureAwait(false);
                if (state.Contains("device", StringComparison.Ordinal))
                {
                    if (stages.AdbOnlineUtc is null)
                    {
                        stages = stages with { AdbOnlineUtc = DateTimeOffset.UtcNow };
                        runtime.Ready = stages;
                        runtime.AdbOnlineSeenUtc = stages.AdbOnlineUtc;
                        await PublishProgress(name, "adb_online", stages).ConfigureAwait(false);
                    }

                    var boot = await _adb.GetPropAsync(record.AdbSerial, "sys.boot_completed", cancellationToken)
                        .ConfigureAwait(false);
                    if (boot.Trim() == "1")
                    {
                        if (stages.BootCompletedUtc is null)
                        {
                            stages = stages with { BootCompletedUtc = DateTimeOffset.UtcNow };
                            runtime.Ready = stages;
                            record.State = InstanceState.Running;
                            _store.UpsertInstance(record);
                            await PublishProgress(name, "boot_completed", stages).ConfigureAwait(false);
                        }

                        if (stages.InteractiveUtc is null)
                        {
                            var focus = await ForegroundAsync(record, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(focus))
                            {
                                stages = stages with { InteractiveUtc = DateTimeOffset.UtcNow };
                                runtime.Ready = stages;
                                await PublishProgress(name, "interactive", stages).ConfigureAwait(false);
                            }
                        }

                        if (stages.InteractiveUtc is not null)
                        {
                            return await ReadinessJson(record, stages, timeout).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                // The transport is still coming up.
            }

            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
        }

        throw new SupervisorOperationException(
            ErrorCodes.Timeout,
            $"Instance '{name}' did not reach interactive readiness within {timeout.TotalSeconds:N0}s " +
            $"(adbOnline={stages.AdbOnlineUtc is not null}, bootCompleted={stages.BootCompletedUtc is not null}).",
            retryable: true);
    }

    public async Task<JsonObject> StopAsync(string name, bool force, CancellationToken cancellationToken)
    {
        var runtime = RuntimeFor(name);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = runtime.Record;
            await StopPumpsAsync(runtime).ConfigureAwait(false);

            if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
            {
                record.State = InstanceState.Stopped;
                record.EnginePid = null;
                record.EngineStartTimeUtc = null;
                record.LastError = null;
                _store.UpsertInstance(record);
                return new JsonObject { ["name"] = name, ["stopped"] = true, ["forced"] = false };
            }

            record.State = InstanceState.Stopping;
            _store.UpsertInstance(record);

            var graceful = false;
            if (!force)
            {
                try
                {
                    await _adb.EmulatorConsoleAsync(record.AdbSerial, new[] { "kill" }, cancellationToken)
                        .ConfigureAwait(false);
                    graceful = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    record.LastError = "Graceful stop request failed: " + ex.Message;
                }
            }

            var pid = record.EnginePid!.Value;
            var start = record.EngineStartTimeUtc;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(force ? 5 : 25);
            while (DateTimeOffset.UtcNow < deadline && NativeProcess.IsAlive(pid, start))
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }

            var forced = false;
            if (NativeProcess.IsAlive(pid, start))
            {
                NativeProcess.KillTree(pid);
                forced = true;
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }

            record.State = InstanceState.Stopped;
            record.EnginePid = null;
            record.EngineStartTimeUtc = null;
            _store.UpsertInstance(record);
            await _hub.PublishAsync(name, "state", EventNames.State, new JsonObject
            {
                ["state"] = record.State.ToString(),
                ["forced"] = forced,
            }).ConfigureAwait(false);

            return new JsonObject
            {
                ["name"] = name,
                ["stopped"] = true,
                ["gracefulRequested"] = graceful,
                ["forced"] = forced,
            };
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    public async Task<JsonObject> StatusAsync(string name, CancellationToken cancellationToken)
    {
        var runtime = RuntimeFor(name);
        var record = runtime.Record;
        var alive = NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc);
        string? adbState = null;
        string? boot = null;
        string? abi = null;
        string? sdk = null;
        string? fingerprint = null;
        string? foreground = null;

        if (alive)
        {
            try
            {
                adbState = await _adb.GetStateAsync(record.AdbSerial, cancellationToken).ConfigureAwait(false);
                if (adbState.Contains("device", StringComparison.Ordinal))
                {
                    boot = await _adb.GetPropAsync(record.AdbSerial, "sys.boot_completed", cancellationToken)
                        .ConfigureAwait(false);
                    abi = await _adb.GetPropAsync(record.AdbSerial, "ro.product.cpu.abi", cancellationToken)
                        .ConfigureAwait(false);
                    sdk = await _adb.GetPropAsync(record.AdbSerial, "ro.build.version.sdk", cancellationToken)
                        .ConfigureAwait(false);
                    fingerprint = await _adb.GetPropAsync(record.AdbSerial, "ro.build.fingerprint", cancellationToken)
                        .ConfigureAwait(false);
                    foreground = await ForegroundAsync(record, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                adbState = "unavailable: " + ex.Message;
            }
        }

        var stages = runtime.Ready;
        return new JsonObject
        {
            ["name"] = record.Name,
            ["avdName"] = record.AvdName,
            ["imagePackage"] = record.ImagePackage,
            ["deviceProfile"] = record.DeviceProfile,
            ["consolePort"] = record.ConsolePort,
            ["grpcPort"] = record.GrpcPort,
            ["serial"] = record.AdbSerial,
            ["adbSerial"] = record.AdbSerial,
            ["state"] = record.State.ToString(),
            ["engineAlive"] = alive,
            ["pid"] = record.EnginePid,
            ["engineStartUtc"] = record.EngineStartTimeUtc,
            ["engineVersion"] = record.EngineVersion,
            ["gpu"] = record.GpuMode,
            ["headless"] = record.Headless,
            ["ramdiskPath"] = record.RamdiskPath,
            ["adbState"] = adbState,
            ["bootCompleted"] = boot,
            ["abi"] = abi,
            ["sdkLevel"] = sdk,
            ["fingerprint"] = fingerprint,
            ["foreground"] = foreground,
            ["lastError"] = record.LastError,
            ["readiness"] = stages is null
                ? null
                : new JsonObject
                {
                    ["launchedUtc"] = stages.LaunchedUtc.ToString("O"),
                    ["adbOnlineUtc"] = stages.AdbOnlineUtc?.ToString("O"),
                    ["bootCompletedUtc"] = stages.BootCompletedUtc?.ToString("O"),
                    ["interactiveUtc"] = stages.InteractiveUtc?.ToString("O"),
                    ["launchedToBootSeconds"] = stages.LaunchedToBootSeconds,
                    ["launchedToInteractiveSeconds"] = stages.LaunchedToInteractiveSeconds,
                },
            ["frameSubscription"] = _hub.Count(record.Name, "frame"),
            ["logcatSubscription"] = _hub.Count(record.Name, "logcat"),
        };
    }

    public async Task<JsonObject> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        await StopAsync(name, force: true, cancellationToken).ConfigureAwait(false);
        var runtime = RuntimeFor(name);
        var record = runtime.Record;
        _provisioner.Delete(record.AvdName, record.AvdHome);
        _store.DeleteInstance(name);
        _runtimes.TryRemove(name, out _);
        var frame = Path.Combine(_frameDirectory, name + ".png");
        if (File.Exists(frame))
        {
            File.Delete(frame);
        }

        return new JsonObject { ["name"] = name, ["deleted"] = true };
    }

    public async Task<JsonObject> SnapshotAsync(string name, string tag, bool save, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
        {
            throw new SupervisorOperationException(ErrorCodes.NotRunning, $"Instance '{name}' is not running.");
        }

        ValidateTag(tag);
        if (save)
        {
            EnsureDiskFree(SnapshotDiskRequirementBytes, "save a snapshot");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var output = save
                ? await _adb.EmulatorConsoleAsync(record.AdbSerial, new[] { "avd", "snapshot", "save", tag }, cancellationToken)
                    .ConfigureAwait(false)
                : await _adb.EmulatorConsoleAsync(record.AdbSerial, new[] { "avd", "snapshot", "load", tag }, cancellationToken)
                    .ConfigureAwait(false);
            stopwatch.Stop();
            if (!output.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new SupervisorOperationException(ErrorCodes.SnapshotFailed, $"Snapshot '{tag}' returned: {output}");
            }

            var recovery = "none";
            if (!save)
            {
                // A snapshot load can leave the guest without an interactive
                // Android runtime. The restore is only complete once the guest
                // answers again; otherwise the supervisor performs a clean boot
                // so the caller always gets a usable guest back.
                if (!await WaitForInteractiveAsync(record, TimeSpan.FromSeconds(90), cancellationToken)
                        .ConfigureAwait(false))
                {
                    await StopAsync(name, force: true, cancellationToken).ConfigureAwait(false);
                    await StartAsync(
                        name,
                        gpu: null,
                        headless: record.Headless,
                        coldBoot: true,
                        ramdisk: null,
                        cancellationToken).ConfigureAwait(false);
                    await WaitAsync(name, TimeSpan.FromSeconds(300), cancellationToken).ConfigureAwait(false);
                    recovery = "cold_boot";
                }
                else
                {
                    recovery = "restored";
                }
            }

            return new JsonObject
            {
                ["name"] = name,
                ["tag"] = tag,
                ["action"] = save ? "save" : "load",
                ["seconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
                ["recovery"] = recovery,
            };
        }
        catch (InvalidOperationException ex)
        {
            throw new SupervisorOperationException(ErrorCodes.SnapshotFailed, ex.Message);
        }
    }

    /// <summary>
    /// Reports whether the guest answers again after a snapshot load: the
    /// transport must exist and Android must report boot completion.
    /// </summary>
    private async Task<bool> WaitForInteractiveAsync(
        InstanceRecord record, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var probe = await _adb.RunAsync(
                        record.AdbSerial,
                        new[] { "shell", "getprop", "sys.boot_completed" },
                        TimeSpan.FromSeconds(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (probe.ExitCode == 0 && probe.StandardOutput.Trim() == "1")
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // The guest is still coming back; keep waiting.
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<byte[]> CapturePngAsync(
        string name, int width, string? via, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
        {
            throw new SupervisorOperationException(ErrorCodes.NotRunning, $"Instance '{name}' is not running.");
        }

        if (string.Equals(via, "adb", StringComparison.OrdinalIgnoreCase))
        {
            return await _adb.ScreencapPngAsync(record.AdbSerial, cancellationToken).ConfigureAwait(false);
        }

        return await WithControllerAsync(
            record,
            channel => channel.GetScreenshotPngAsync(width, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports the exact identity of the managed target: instance name, ADB
    /// serial, the transport id the pinned ADB server currently resolves, the
    /// engine process generation and the owned ports. A serial that is missing
    /// or not in the "device" state fails closed instead of touching another
    /// transport.
    /// </summary>
    public async Task<JsonObject> AdbIdentityAsync(string name, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var listing = await _adb.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        var transport = AdbIdentity.Find(listing, record.AdbSerial);
        var transportId = transport?.TransportId;
        var state = transport?.State;
        var model = transport?.Model;
        var device = transport?.Device;
        var product = transport?.Product;
        if (state is null)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InstanceUnavailable,
                $"The managed serial '{record.AdbSerial}' is not visible to the pinned ADB server.");
        }

        if (!state.Equals("device", StringComparison.Ordinal))
        {
            throw new SupervisorOperationException(
                ErrorCodes.InstanceUnavailable,
                $"The managed serial '{record.AdbSerial}' is '{state}'; the target is not usable.");
        }

        var probe = await _adb.RunAsync(
                record.AdbSerial,
                new[] { "shell", "echo", "ws-identity-probe" },
                TimeSpan.FromSeconds(20),
                cancellationToken)
            .ConfigureAwait(false);
        var transportUsable = probe.ExitCode == 0
                              && probe.StandardOutput.Contains("ws-identity-probe", StringComparison.Ordinal);
        if (!transportUsable)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InstanceUnavailable,
                $"The managed serial '{record.AdbSerial}' is listed but did not answer a round-trip probe.");
        }

        var identity = string.Join(
            "|",
            record.Name,
            record.AdbSerial,
            transportId ?? "",
            record.EnginePid?.ToString() ?? "",
            record.EngineStartTimeUtc ?? "",
            record.ConsolePort.ToString(),
            record.GrpcPort.ToString());

        return new JsonObject
        {
            ["name"] = record.Name,
            ["serial"] = record.AdbSerial,
            ["transportId"] = transportId,
            ["state"] = state,
            ["model"] = model,
            ["device"] = device,
            ["product"] = product,
            ["enginePid"] = record.EnginePid,
            ["engineStartUtc"] = record.EngineStartTimeUtc,
            ["consolePort"] = record.ConsolePort,
            ["grpcPort"] = record.GrpcPort,
            ["transportUsable"] = transportUsable,
            ["identity"] = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant(),
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    public async Task<JsonObject> VerifyTargetAsync(string name, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var transport = await _adb.GetTransportAsync(record.AdbSerial, cancellationToken).ConfigureAwait(false);
        var devices = await ProcessRunner.RunAsync(
            _sdk.AdbExe,
            new[] { "devices", "-l" },
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var lines = devices.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();

        var matching = lines.Where(line => line.StartsWith(record.AdbSerial, StringComparison.Ordinal)).ToList();
        var foreign = lines
            .Where(line => line.StartsWith("emulator-", StringComparison.Ordinal)
                           && !line.StartsWith(record.AdbSerial, StringComparison.Ordinal))
            .ToList();

        var verified = transport == record.AdbSerial && matching.Count == 1;
        if (!verified)
        {
            throw new SupervisorOperationException(
                ErrorCodes.AdbFailure,
                $"ADB target verification failed: expected {record.AdbSerial}, got '{transport}'.");
        }

        return new JsonObject
        {
            ["name"] = record.Name,
            ["expectedSerial"] = record.AdbSerial,
            ["consolePort"] = record.ConsolePort,
            ["transport"] = transport,
            ["engineAlive"] = NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc),
            ["pid"] = record.EnginePid,
            ["deviceLine"] = matching[0],
            ["otherEmulators"] = new JsonArray(foreign.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
        };
    }

    public async Task<JsonObject> ControllerProbeAsync(string name, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var discovery = await EmulatorDiscovery.ReadAsync(_adb, record.AdbSerial, cancellationToken)
            .ConfigureAwait(false);

        var identityMatches = discovery.AdbPort == record.ConsolePort + 1
                              && discovery.GrpcPort == record.GrpcPort
                              && discovery.AvdName == record.AvdName;

        var endpoints = PortBindings(discovery.GrpcPort);
        var advertised = ReadAdvertisedEndpoint(name);
        var loopbackOnly = advertised is not null
                           && (advertised.StartsWith("127.", StringComparison.Ordinal)
                               || advertised.StartsWith("[::1]", StringComparison.Ordinal));
        var lanReachable = await ProbeLanReachabilityAsync(discovery.GrpcPort).ConfigureAwait(false);

        string unauthenticated;
        try
        {
            using var anonymous = EmulatorControlChannel.Connect(discovery.GrpcPort, null);
            await anonymous.GetStatusAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            unauthenticated = "ACCEPTED (unexpected)";
        }
        catch (Grpc.Core.RpcException ex)
        {
            unauthenticated = ex.StatusCode.ToString();
        }

        string forged;
        try
        {
            using var forgedChannel = EmulatorControlChannel.Connect(
                discovery.GrpcPort, new string('0', 88), "bearer");
            await forgedChannel.GetStatusAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            forged = "ACCEPTED (unexpected)";
        }
        catch (Grpc.Core.RpcException ex)
        {
            forged = ex.StatusCode.ToString();
        }

        using var channel = EmulatorControlChannel.Connect(discovery.GrpcPort, discovery.GrpcToken, "bearer");
        var status = await channel.GetStatusAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var frame = await channel.GetScreenshotPngAsync(480, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!identityMatches || !loopbackOnly || lanReachable
            || unauthenticated == "ACCEPTED (unexpected)" || forged == "ACCEPTED (unexpected)")
        {
            throw new SupervisorOperationException(
                ErrorCodes.ControllerFailure,
                $"Controller probe failed: identity={identityMatches} loopback={loopbackOnly} " +
                $"lan={lanReachable} noToken={unauthenticated} forged={forged}.");
        }

        return new JsonObject
        {
            ["name"] = name,
            ["port"] = discovery.GrpcPort,
            ["discoveryPath"] = discovery.DiscoveryPath,
            ["tokenLength"] = discovery.GrpcToken.Length,
            ["avdName"] = discovery.AvdName,
            ["adbPort"] = discovery.AdbPort,
            ["build"] = discovery.EmulatorBuild,
            ["advertisedEndpoint"] = advertised,
            ["socketEndpoints"] = new JsonArray(endpoints.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            ["unauthenticated"] = unauthenticated,
            ["forged"] = forged,
            ["statusVersion"] = status.Version,
            ["booted"] = status.Booted,
            ["uptimeMs"] = status.Uptime,
            ["frameBytes"] = frame.Length,
            ["identityMatches"] = identityMatches,
            ["loopbackOnly"] = loopbackOnly,
            ["lanReachable"] = lanReachable,
        };
    }

    private string? ReadAdvertisedEndpoint(string name)
    {
        var log = _paths.InstanceLogPath(name);
        if (!File.Exists(log))
        {
            return null;
        }

        const string marker = "Started GRPC server at ";
        string? endpoint = null;
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                endpoint = line[(index + marker.Length)..].Split(',')[0].Trim();
            }
        }

        return endpoint;
    }

    private static List<string> PortBindings(int port)
    {
        var result = ProcessRunner.RunAsync("netstat.exe", new[] { "-ano", "-p", "TCP" })
            .GetAwaiter().GetResult();
        var addresses = new List<string>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 3 || columns[0] != "TCP")
            {
                continue;
            }

            var local = columns[1];
            var separator = local.LastIndexOf(':');
            if (separator < 0 || local[(separator + 1)..] != port.ToString())
            {
                continue;
            }

            addresses.Add(local[..separator]);
        }

        return addresses.Distinct().ToList();
    }

    private static async Task<bool> ProbeLanReachabilityAsync(int port)
    {
        var address = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                          && nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                  && !System.Net.IPAddress.IsLoopback(ip));

        if (address is null)
        {
            return false;
        }

        using var client = new System.Net.Sockets.TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
    public async Task<JsonObject> TapAsync(string name, int x, int y, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var dispatched = DateTimeOffset.UtcNow;
        await WithControllerAsync(record, channel => channel.TapAsync(x, y, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        return new JsonObject
        {
            ["name"] = name,
            ["x"] = x,
            ["y"] = y,
            ["dispatchedUtc"] = dispatched.ToString("O"),
            ["acknowledgedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    public async Task<JsonObject> SwipeAsync(
        string name, int x1, int y1, int x2, int y2, int durationMs, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var dispatched = DateTimeOffset.UtcNow;
        await WithControllerAsync(
            record,
            channel => channel.SwipeAsync(x1, y1, x2, y2, durationMs, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["name"] = name,
            ["dispatchedUtc"] = dispatched.ToString("O"),
            ["acknowledgedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    public async Task<JsonObject> KeyAsync(string name, string key, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var code = key.ToUpperInvariant() switch
        {
            "BACK" => "4",
            "HOME" => "3",
            "RECENTS" or "APP_SWITCH" => "187",
            "WAKEUP" => "224",
            "ENTER" => "66",
            _ => key,
        };

        await _adb.RunAsync(record.AdbSerial, new[] { "shell", "input", "keyevent", code }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new JsonObject { ["name"] = name, ["key"] = code };
    }

    /// <summary>
    /// Sets the simulated GPS position of a running guest through the emulator
    /// console and returns the value the guest reports back.
    /// </summary>
    public async Task<JsonObject> GeoFixAsync(
        string name, double longitude, double latitude, double? altitude, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
        {
            throw new SupervisorOperationException(ErrorCodes.NotRunning, $"Instance '{name}' is not running.");
        }

        var command = new List<string> { "geo", "fix" };
        command.Add(longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        command.Add(latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        if (altitude.HasValue)
        {
            command.Add(altitude.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        var output = await _adb.EmulatorConsoleAsync(record.AdbSerial, command, cancellationToken)
            .ConfigureAwait(false);
        var reported = await _adb.ShellAsync(
                record.AdbSerial,
                new[] { "dumpsys", "location" },
                cancellationToken)
            .ConfigureAwait(false);
        var applied = reported.Contains(
            longitude.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        return new JsonObject
        {
            ["name"] = name,
            ["longitude"] = longitude,
            ["latitude"] = latitude,
            ["altitude"] = altitude,
            ["console"] = output,
            ["applied"] = applied,
        };
    }

    /// <summary>Sets the simulated battery level and charger state of a running guest.</summary>
    public async Task<JsonObject> BatteryAsync(
        string name, int level, bool charging, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
        {
            throw new SupervisorOperationException(ErrorCodes.NotRunning, $"Instance '{name}' is not running.");
        }

        if (level is < 0 or > 100)
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "The battery level must be 0-100.");
        }

        await _adb.EmulatorConsoleAsync(
                record.AdbSerial,
                new[] { "power", "capacity", level.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                cancellationToken)
            .ConfigureAwait(false);
        await _adb.EmulatorConsoleAsync(
                record.AdbSerial,
                new[] { "power", "ac", charging ? "on" : "off" },
                cancellationToken)
            .ConfigureAwait(false);

        var state = await _adb.ShellAsync(record.AdbSerial, new[] { "dumpsys", "battery" }, cancellationToken)
            .ConfigureAwait(false);
        return new JsonObject
        {
            ["name"] = name,
            ["level"] = level,
            ["charging"] = charging,
            ["reportedLevel"] = ExtractBatteryValue(state, "level"),
            ["reportedStatus"] = ExtractBatteryValue(state, "status"),
            ["reportedAc"] = ExtractBatteryValue(state, "AC powered"),
        };
    }

    private static string ExtractBatteryValue(string dumpsys, string key)
    {
        foreach (var line in dumpsys.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains(':'))
            {
                return trimmed[(trimmed.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
        }

        return "";
    }

    public async Task<JsonObject> RotateAsync(string name, int rotation, CancellationToken cancellationToken)
    {
        if (rotation is < 0 or > 3)
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Rotation must be 0-3.");
        }

        var record = RuntimeFor(name).Record;
        await _adb.RunAsync(record.AdbSerial,
            new[] { "shell", "settings", "put", "system", "accelerometer_rotation", "0" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _adb.RunAsync(record.AdbSerial,
            new[] { "shell", "settings", "put", "system", "user_rotation", rotation.ToString(CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["name"] = name, ["rotation"] = rotation };
    }

    public async Task<JsonObject> InstallAsync(string name, string apkPath, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        if (!File.Exists(apkPath))
        {
            throw new SupervisorOperationException(ErrorCodes.NotFound, $"APK not found: {apkPath}");
        }

        if (!apkPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new SupervisorOperationException(ErrorCodes.InstallFailed, "Only .apk files can be installed.");
        }

        var size = new FileInfo(apkPath).Length;
        await _hub.PublishAsync(name, "state", EventNames.Progress, new JsonObject
        {
            ["phase"] = "install",
            ["percent"] = 10,
            ["detail"] = $"Installing {Path.GetFileName(apkPath)} ({size / 1024 / 1024} MiB)",
        }).ConfigureAwait(false);

        var result = await _adb.RunAsync(
            record.AdbSerial,
            new[] { "install", "-r", "-t", apkPath },
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);

        var combined = (result.StandardOutput + "\n" + result.StandardError).Trim();
        if (result.ExitCode != 0 || !combined.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            throw new SupervisorOperationException(ErrorCodes.InstallFailed, combined);
        }

        await _hub.PublishAsync(name, "state", EventNames.Progress, new JsonObject
        {
            ["phase"] = "install",
            ["percent"] = 100,
            ["detail"] = "Install complete",
        }).ConfigureAwait(false);

        return new JsonObject
        {
            ["name"] = name,
            ["apk"] = Path.GetFullPath(apkPath),
            ["bytes"] = size,
            ["output"] = combined,
        };
    }

/// <summary>
    /// Dismisses the keyguard of a headless guest. Failures are ignored: a
    /// guest without a keyguard simply has nothing to dismiss.
    /// </summary>
    private async Task DismissKeyguardAsync(InstanceRecord record, CancellationToken cancellationToken)
    {
        foreach (var command in new[]
                 {
                     new[] { "wm", "dismiss-keyguard" },
                     new[] { "input", "keyevent", "82" },
                 })
        {
            try
            {
                await _adb.ShellAsync(record.AdbSerial, command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Closes a system dialog that took the focus away from the launched
    /// application. A headless guest regularly shows an "Application Not
    /// Responding" dialog for SystemUI, which would otherwise keep the
    /// application in the background forever.
    /// </summary>
    private async Task DismissSystemDialogsAsync(InstanceRecord record, CancellationToken cancellationToken)
    {
        foreach (var command in new[]
                 {
                     new[] { "am", "broadcast", "-a", "android.intent.action.CLOSE_SYSTEM_DIALOGS" },
                     new[] { "input", "keyevent", "4" },
                 })
        {
            try
            {
                await _adb.ShellAsync(record.AdbSerial, command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

/// <summary>
    /// Switches the ADB daemon of a lab guest between the unprivileged and the
    /// root user and reports the effective UID before and after. The transport
    /// is re-verified because "adb root" restarts adbd.
    /// </summary>
    public async Task<JsonObject> SetAdbRootAsync(
        string name, bool root, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var buildType = (await _adb.GetPropAsync(record.AdbSerial, "ro.build.type", cancellationToken)
            .ConfigureAwait(false)).Trim();
        var state = await AdbRootStateAsync(name, cancellationToken).ConfigureAwait(false);
        if (!state["rootCapable"]!.GetValue<bool>())
        {
            throw new SupervisorOperationException(
                ErrorCodes.CapabilityUnavailable,
                $"The image '{record.ImagePackage}' (ro.build.type={buildType}) is not a userdebug build, "
                + "so the ADB daemon cannot run as root.");
        }

        var before = state["uid"]!.GetValue<string>();
        var output = await _adb.RunAsync(
                record.AdbSerial,
                new[] { root ? "root" : "unroot" },
                TimeSpan.FromSeconds(60),
                cancellationToken)
            .ConfigureAwait(false);

        // adbd restarts, so the transport is re-acquired and the effective UID
        // is read again before the result is reported.
        string? after = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            after = await ReadAdbUidAsync(record, cancellationToken).ConfigureAwait(false);
            if (after is not null
                && (!after.Equals(before, StringComparison.Ordinal)
                    || after.Equals(root ? "0" : "2000", StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        after ??= before;

        var refreshed = await AdbRootStateAsync(name, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["name"] = name,
            ["serial"] = record.AdbSerial,
            ["requested"] = root ? "root" : "unroot",
            ["buildType"] = buildType,
            ["uidBefore"] = before,
            ["uidAfter"] = refreshed["uid"]!.DeepClone(),
            ["effectiveRoot"] = refreshed["uid"]!.GetValue<string>() == "0",
            ["transportId"] = refreshed["transportId"]?.DeepClone(),
            ["output"] = (output.StandardOutput + output.StandardError).Trim(),
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>Reports the ADB daemon identity of a managed guest.</summary>
    public async Task<JsonObject> AdbRootStateAsync(string name, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var buildType = (await _adb.GetPropAsync(record.AdbSerial, "ro.build.type", cancellationToken)
            .ConfigureAwait(false)).Trim();
        var uid = await ReadAdbUidAsync(record, cancellationToken).ConfigureAwait(false);
        if (uid is null)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InstanceUnavailable,
                $"The ADB transport of '{name}' did not report an effective user.");
        }

        var listing = await _adb.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        string? transportId = null;
        foreach (var line in listing.Split('\n'))
        {
            var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals(record.AdbSerial, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var token in parts.Skip(2))
            {
                if (token.StartsWith("transport_id:", StringComparison.OrdinalIgnoreCase))
                {
                    transportId = token["transport_id:".Length..];
                }
            }

            break;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["serial"] = record.AdbSerial,
            ["uid"] = uid,
            ["effectiveRoot"] = uid == "0",
            ["rootCapable"] = buildType.Contains("userdebug", StringComparison.OrdinalIgnoreCase)
                               || buildType.Contains("eng", StringComparison.OrdinalIgnoreCase),
            ["buildType"] = buildType,
            ["transportId"] = transportId,
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>
    /// Reads the effective ADB daemon user. A response that cannot be parsed
    /// (the daemon is restarting) returns null instead of a wrong value.
    /// </summary>
    private async Task<string?> ReadAdbUidAsync(InstanceRecord record, CancellationToken cancellationToken)
    {
        string output;
        try
        {
            output = await _adb.ShellAsync(record.AdbSerial, new[] { "id" }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var token in output.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("uid=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = token[4..].Split('(')[0];
            return int.TryParse(value, out _) ? value : null;
        }

        return null;
    }

    public async Task<JsonObject> LaunchAsync(
        string name, string package, CancellationToken cancellationToken, int focusWaitSeconds = 20)
    {
        var record = RuntimeFor(name).Record;

        // A headless guest boots with the keyguard up, which hides the
        // launched activity from every observer. The keyguard is dismissed
        // before the launch so the application really reaches the foreground.
        await DismissKeyguardAsync(record, cancellationToken).ConfigureAwait(false);

        var resolved = await _adb.ShellAsync(
            record.AdbSerial,
            new[] { "cmd", "package", "resolve-activity", "--brief", package },
            cancellationToken).ConfigureAwait(false);

        var component = resolved
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.Contains('/', StringComparison.Ordinal));

        string output;
        if (component is not null)
        {
            output = await _adb.ShellAsync(
                record.AdbSerial,
                new[] { "am", "start", "-W", "-n", component },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            output = await _adb.ShellAsync(
                record.AdbSerial,
                new[] { "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1" },
                cancellationToken).ConfigureAwait(false);
        }

        // The launch is only useful once the activity is the focused window.
        // The guest is asked for its focus and its process so a caller can tell
        // "started but not foreground" from "started and foreground".
        var focused = "";
        var processId = "";
        for (var attempt = 0; attempt < Math.Max(1, focusWaitSeconds); attempt++)
        {
            try
            {
                focused = await ForegroundAsync(record, cancellationToken).ConfigureAwait(false) ?? "";
            }
            catch (Exception)
            {
                focused = "";
            }

            if (focused.Contains(package, StringComparison.Ordinal))
            {
                break;
            }

            try
            {
                processId = await _adb.ShellAsync(
                        record.AdbSerial,
                        new[] { "pidof", package },
                        cancellationToken)
                    .ConfigureAwait(false);
                processId = processId.Trim();
            }
            catch (Exception)
            {
                processId = "";
            }

            // A system dialog (for example an ANR report of SystemUI) holds
            // the focus; it is closed so the application can come forward.
            if (focused.Contains("Application Not Responding", StringComparison.OrdinalIgnoreCase)
                || focused.Contains("isn't responding", StringComparison.OrdinalIgnoreCase))
            {
                await DismissSystemDialogsAsync(record, cancellationToken).ConfigureAwait(false);
            }

            // The launcher takes the focus back on a guest that is still
            // settling, so the start is issued again until the application
            // really owns the foreground.
            if (attempt % 4 == 3 && component is not null)
            {
                try
                {
                    await _adb.ShellAsync(
                            record.AdbSerial,
                            new[] { "am", "start", "-n", component },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        var focusedMatches = focused.Contains(package, StringComparison.Ordinal);
        if (!focusedMatches)
        {
            throw new SupervisorOperationException(
                ErrorCodes.AppLaunchFailed,
                $"The application '{package}' started but did not become the focused window "
                + $"(focused: '{focused}', pid: '{processId}').");
        }

        return new JsonObject
        {
            ["name"] = name,
            ["package"] = package,
            ["component"] = component,
            ["output"] = output,
            ["started"] = true,
            ["focused"] = focused,
            ["focusedMatches"] = focusedMatches,
            ["pid"] = processId,
        };
    }

    /// <summary>
    /// Reports the installed fixture package through the guest package
    /// manager, so a recipe can verify a baseline that already contains the
    /// application before it tries to launch it.
    /// </summary>
    public async Task<JsonObject> PackageInfoAsync(
        string name, string package, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var list = await _adb.ShellAsync(
                record.AdbSerial,
                new[] { "pm", "list", "packages", "-f", package },
                cancellationToken)
            .ConfigureAwait(false);
        var installed = list.Contains(package, StringComparison.Ordinal);

        var versionName = "";
        var apkPath = "";
        if (installed)
        {
            var dump = await _adb.ShellAsync(
                    record.AdbSerial,
                    new[] { "dumpsys", "package", package },
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var line in dump.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("versionName=", StringComparison.Ordinal))
                {
                    versionName = trimmed["versionName=".Length..].Trim();
                }
                else if (trimmed.StartsWith("codePath=", StringComparison.Ordinal))
                {
                    apkPath = trimmed["codePath=".Length..].Trim();
                }
            }
        }

        return new JsonObject
        {
            ["name"] = name,
            ["package"] = package,
            ["installed"] = installed,
            ["versionName"] = versionName,
            ["codePath"] = apkPath,
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

// ------------------------------------------------ developer tooling

    /// <summary>
    /// Reports the permissions a package requests and holds. The information
    /// comes from the guest package manager, so it always describes the image
    /// that is actually running.
    /// </summary>
    public async Task<JsonObject> PackagePermissionsAsync(
        string name, string package, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var dump = await _adb.ShellAsync(
                record.AdbSerial,
                new[] { "dumpsys", "package", package },
                cancellationToken)
            .ConfigureAwait(false);

        var requested = new JsonArray();
        var granted = new JsonArray();
        var inBlock = string.Empty;
        foreach (var raw in dump.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("requested permissions:", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = "requested";
                continue;
            }

            if (line.StartsWith("install permissions:", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = "install";
                continue;
            }

            if (line.StartsWith("runtime permissions:", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = "runtime";
                continue;
            }

            if (line.Length == 0)
            {
                inBlock = string.Empty;
                continue;
            }

            var permission = line.TrimEnd(':', ' ').Trim();
            if (permission.Length == 0 || !permission.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            var isGranted = line.Contains(": granted=true", StringComparison.OrdinalIgnoreCase);
            if (isGranted)
            {
                granted.Add(permission);
            }

            if (inBlock is "requested" or "runtime")
            {
                requested.Add(permission);
            }
        }

        return new JsonObject
        {
            ["name"] = name,
            ["package"] = package,
            ["requested"] = requested,
            ["granted"] = granted,
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>
    /// Starts a deep link on the guest. The URI is validated first so a
    /// malformed value never reaches the guest's intent parser.
    /// </summary>
    public async Task<JsonObject> DeepLinkAsync(
        string name, string uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "uri is required.");
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, $"'{uri}' is not an absolute URI.");
        }

        var record = RuntimeFor(name).Record;
        var output = await _adb.ShellAsync(
                record.AdbSerial,
                new[] { "am", "start", "-W", "-a", "android.intent.action.VIEW", "-d", parsed.AbsoluteUri },
                cancellationToken)
            .ConfigureAwait(false);

        var started = output.Contains("Status: ok", StringComparison.OrdinalIgnoreCase)
                      || output.Contains("Starting:", StringComparison.OrdinalIgnoreCase);
        return new JsonObject
        {
            ["name"] = name,
            ["uri"] = parsed.AbsoluteUri,
            ["scheme"] = parsed.Scheme,
            ["host"] = parsed.Host,
            ["started"] = started,
            ["output"] = output.Trim(),
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>
    /// Runs an instrumentation test on the guest and reports its result. The
    /// runner is optional; the guest default runner is used when it is omitted.
    /// </summary>
    public async Task<JsonObject> InstrumentationAsync(
        string name, string package, string? runner, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var arguments = new List<string> { "am", "instrument", "-w" };
        if (!string.IsNullOrWhiteSpace(runner))
        {
            arguments.Add(runner);
        }
        else
        {
            arguments.Add(package);
        }

        var output = await _adb.ShellAsync(record.AdbSerial, arguments.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        var failures = 0;
        var tests = 0;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("INSTRUMENTATION_RESULT:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["INSTRUMENTATION_RESULT:".Length..].Trim();
                foreach (var token in value.Split(','))
                {
                    var pair = token.Split('=', 2);
                    if (pair.Length != 2)
                    {
                        continue;
                    }

                    if (pair[0].Trim().Equals("numtests", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(pair[1].Trim(), out tests);
                    }
                    else if (pair[0].Trim().Equals("numfailures", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(pair[1].Trim(), out failures);
                    }
                }
            }
        }

        return new JsonObject
        {
            ["name"] = name,
            ["package"] = package,
            ["runner"] = runner ?? string.Empty,
            ["tests"] = tests,
            ["failures"] = failures,
            ["passed"] = failures == 0 && tests > 0,
            ["output"] = output.Trim(),
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>
    /// Exports the guest logcat to a workspace evidence file. The file is
    /// written under the workspace evidence directory so it never lands in an
    /// unrelated location.
    /// </summary>
    public async Task<JsonObject> LogcatExportAsync(
        string name, string? tag, int lines, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var arguments = new List<string> { "logcat", "-d" };
        if (lines > 0)
        {
            arguments.Add("-t");
            arguments.Add(lines.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            arguments.Add(tag);
        }

        var output = await _adb.ShellAsync(record.AdbSerial, arguments.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var file = Path.Combine(
            _paths.EvidenceDirectory,
            $"logcat-{name}-{stamp}.txt");
        Directory.CreateDirectory(_paths.EvidenceDirectory);
        await File.WriteAllTextAsync(file, output, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["name"] = name,
            ["artifactPath"] = file,
            ["bytes"] = new FileInfo(file).Length,
            ["lines"] = lines,
            ["tag"] = tag ?? string.Empty,
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>
    /// Collects the crash and ANR evidence of the guest: the tombstone
    /// directory, the dropbox crash entries and the ANR traces.
    /// </summary>
    public async Task<JsonObject> CrashReportAsync(
        string name, string? package, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var crashes = new JsonArray();
        var anrs = new JsonArray();

        var dropbox = await _adb.ShellAsync(
                record.AdbSerial,
                new[] { "dumpsys", "dropbox", "--print" },
                cancellationToken)
            .ConfigureAwait(false);

        var current = string.Empty;
        var buffer = new System.Text.StringBuilder();
        foreach (var raw in dropbox.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Drop box entry '", StringComparison.Ordinal))
            {
                FlushDropbox(buffer, current, package, crashes, anrs);
                current = line["Drop box entry '".Length..].Split('\'')[0];
                buffer.Clear();
                continue;
            }

            buffer.AppendLine(line);
        }

        FlushDropbox(buffer, current, package, crashes, anrs);

        var traces = await _adb.ShellAsync(
                record.AdbSerial,
                new[] { "ls", "/data/anr" },
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var line in traces.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                anrs.Add(trimmed);
            }
        }

        return new JsonObject
        {
            ["name"] = name,
            ["package"] = package ?? string.Empty,
            ["crashes"] = crashes,
            ["anrs"] = anrs,
            ["crashCount"] = crashes.Count,
            ["anrCount"] = anrs.Count,
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    private static void FlushDropbox(
        System.Text.StringBuilder buffer,
        string entry,
        string? package,
        JsonArray crashes,
        JsonArray anrs)
    {
        if (entry.Length == 0)
        {
            return;
        }

        var kind = entry.Split('_')[0].ToLowerInvariant();
        if (kind is "crash" or "anr")
        {
            var body = buffer.ToString();
            if (package is null || body.Contains(package, StringComparison.Ordinal)
                              || entry.Contains(package, StringComparison.OrdinalIgnoreCase))
            {
                var item = new JsonObject
                {
                    ["entry"] = entry,
                    ["kind"] = kind,
                    ["bytes"] = body.Length,
                    ["excerpt"] = body.Length > 2000 ? body[..2000] : body,
                };
                if (kind == "crash")
                {
                    crashes.Add(item);
                }
                else
                {
                    anrs.Add(item);
                }
            }
        }

        buffer.Clear();
    }

    /// <summary>
    /// Writes a full bug report of the guest into the workspace evidence
    /// directory and returns its path and digest.
    /// </summary>
    public async Task<JsonObject> BugReportAsync(
        string name, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        Directory.CreateDirectory(_paths.EvidenceDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var file = Path.Combine(_paths.EvidenceDirectory, $"bugreport-{name}-{stamp}.txt");

        var result = await _adb.RunAsync(
                record.AdbSerial,
                new[] { "bugreport", file },
                TimeSpan.FromMinutes(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (!File.Exists(file))
        {
            throw new SupervisorOperationException(
                ErrorCodes.Internal,
                "The bug report was not produced: " + (result.StandardError + result.StandardOutput).Trim());
        }

        var info = new FileInfo(file);
        return new JsonObject
        {
            ["name"] = name,
            ["artifactPath"] = file,
            ["bytes"] = info.Length,
            ["sha256"] = HashFile(file),
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public async Task<JsonObject> ForegroundStatusAsync(string name, CancellationToken cancellationToken)
    {
        var record = RuntimeFor(name).Record;
        var focus = await ForegroundAsync(record, cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["name"] = name, ["foreground"] = focus };
    }

    public async Task<string> SubscribeFramesAsync(
        string name, string format, int width, int height, Func<EventEnvelope, Task> send, CancellationToken cancellationToken)
    {
        var runtime = RuntimeFor(name);
        var record = runtime.Record;
        if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
        {
            throw new SupervisorOperationException(ErrorCodes.NotRunning, $"Instance '{name}' is not running.");
        }

        if (runtime.Frames is null || !runtime.Frames.IsRunning)
        {
            var options = new FramePumpOptions(format, width, height);
            var pump = new FramePump(
                record,
                _frameDirectory,
                options,
                async frame =>
                {
                    await _hub.PublishAsync(frame.Instance, "frame", EventNames.Frame, new JsonObject
                    {
                        ["seq"] = frame.Sequence,
                        ["width"] = frame.Width,
                        ["height"] = frame.Height,
                        ["stride"] = frame.Stride,
                        ["format"] = frame.Format,
                        ["guestWidth"] = frame.GuestWidth,
                        ["guestHeight"] = frame.GuestHeight,
                        ["bytes"] = frame.Bytes,
                        ["emulatorTimestampUs"] = frame.EmulatorTimestampUs,
                        ["receivedUtc"] = frame.ReceivedUtc.ToString("O"),
                        ["writtenUtc"] = frame.WrittenUtc.ToString("O"),
                        ["path"] = frame.Path,
                        ["dropped"] = frame.DroppedSincePrevious,
                    }).ConfigureAwait(false);
                },
                async (instance, reason) =>
                {
                    await _hub.PublishAsync(instance, "frame", EventNames.SubscriptionClosed, new JsonObject
                    {
                        ["reason"] = reason,
                    }).ConfigureAwait(false);
                });

            await pump.StartAsync(cancellationToken).ConfigureAwait(false);
            runtime.Frames = pump;
        }

        var id = _hub.Add(name, "frame", send);
        _subscriptions[id] = (name, "frame");
        return id;
    }

    public async Task UnsubscribeAsync(string id, CancellationToken cancellationToken)
    {
        if (!_subscriptions.TryRemove(id, out var info))
        {
            return;
        }

        _hub.Remove(id);
        if (info.Kind == "frame" && _hub.Count(info.Instance, "frame") == 0)
        {
            if (_runtimes.TryGetValue(info.Instance, out var runtime) && runtime.Frames is not null)
            {
                await runtime.Frames.StopAsync().ConfigureAwait(false);
                runtime.Frames = null;
            }
        }

        if (info.Kind == "logcat" && _hub.Count(info.Instance, "logcat") == 0)
        {
            if (_runtimes.TryGetValue(info.Instance, out var runtime) && runtime.Logcat is not null)
            {
                await runtime.Logcat.StopAsync().ConfigureAwait(false);
                runtime.Logcat = null;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<string> SubscribeLogcatAsync(string name, Func<EventEnvelope, Task> send, CancellationToken cancellationToken)
    {
        var runtime = RuntimeFor(name);
        var record = runtime.Record;
        if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
        {
            throw new SupervisorOperationException(ErrorCodes.NotRunning, $"Instance '{name}' is not running.");
        }

        if (runtime.Logcat is null)
        {
            var pump = new LogcatPump(
                record.AdbSerial,
                _sdk.AdbExe,
                async entry =>
                {
                    await _hub.PublishAsync(name, "logcat", EventNames.Logcat, new JsonObject
                    {
                        ["level"] = entry.Level,
                        ["tag"] = entry.Tag,
                        ["message"] = entry.Message,
                        ["raw"] = entry.Raw,
                        ["receivedUtc"] = entry.ReceivedUtc.ToString("O"),
                    }).ConfigureAwait(false);
                },
                async reason =>
                {
                    await _hub.PublishAsync(name, "logcat", EventNames.SubscriptionClosed, new JsonObject
                    {
                        ["reason"] = reason,
                    }).ConfigureAwait(false);
                });
            await pump.StartAsync(cancellationToken).ConfigureAwait(false);
            runtime.Logcat = pump;
        }

        var id = _hub.Add(name, "logcat", send);
        _subscriptions[id] = (name, "logcat");
        return id;
    }

    public JsonArray Trace(string name)
    {
        var runtime = RuntimeFor(name);
        var trace = runtime.Frames?.Trace ?? Array.Empty<FrameSample>();
        var array = new JsonArray();
        foreach (var sample in trace)
        {
            array.Add(new JsonObject
            {
                ["seq"] = sample.Sequence,
                ["emulatorTimestampUs"] = sample.EmulatorTimestampUs,
                ["receivedUtc"] = sample.ReceivedUtc.ToString("O"),
                ["width"] = sample.Width,
                ["height"] = sample.Height,
                ["bytes"] = sample.Bytes,
                ["intervalMs"] = Math.Round(sample.IntervalMs, 3),
                ["dropped"] = sample.Dropped,
            });
        }

        return array;
    }

    public string FramePath(string name, string format) => Path.Combine(
        _frameDirectory,
        name + (format.Equals("rgb", StringComparison.OrdinalIgnoreCase) ? ".bgr" : ".png"));

    public async Task ShutdownAsync()
    {
        foreach (var runtime in _runtimes.Values)
        {
            await StopPumpsAsync(runtime).ConfigureAwait(false);
        }
    }

    private async Task StopPumpsAsync(Runtime runtime)
    {
        if (runtime.Frames is not null)
        {
            await runtime.Frames.StopAsync().ConfigureAwait(false);
            runtime.Frames = null;
        }

        if (runtime.Logcat is not null)
        {
            await runtime.Logcat.StopAsync().ConfigureAwait(false);
            runtime.Logcat = null;
        }
    }

    private async Task<JsonObject> ReadinessJson(InstanceRecord record, ReadyStages stages, TimeSpan timeout)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return new JsonObject
        {
            ["name"] = record.Name,
            ["serial"] = record.AdbSerial,
            ["launchedUtc"] = stages.LaunchedUtc.ToString("O"),
            ["adbOnlineUtc"] = stages.AdbOnlineUtc?.ToString("O"),
            ["bootCompletedUtc"] = stages.BootCompletedUtc?.ToString("O"),
            ["interactiveUtc"] = stages.InteractiveUtc?.ToString("O"),
            ["launchedToBootSeconds"] = stages.LaunchedToBootSeconds is null
                ? null
                : Math.Round(stages.LaunchedToBootSeconds.Value, 2),
            ["launchedToInteractiveSeconds"] = stages.LaunchedToInteractiveSeconds is null
                ? null
                : Math.Round(stages.LaunchedToInteractiveSeconds.Value, 2),
            ["timeoutSeconds"] = timeout.TotalSeconds,
        };
    }

    private async Task PublishProgress(string name, string stage, ReadyStages stages)
    {
        await _hub.PublishAsync(name, "state", EventNames.Progress, new JsonObject
        {
            ["phase"] = "readiness",
            ["stage"] = stage,
            ["elapsedSeconds"] = Math.Round((DateTimeOffset.UtcNow - stages.LaunchedUtc).TotalSeconds, 2),
        }).ConfigureAwait(false);
    }

    private async Task<string?> ForegroundAsync(InstanceRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var dump = await _adb.RunAsync(
                record.AdbSerial,
                new[] { "shell", "dumpsys", "window" },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            var line = dump.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("mCurrentFocus=", StringComparison.Ordinal));

            return line is null ? null : line["mCurrentFocus=".Length..].Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    private async Task<T> WithControllerAsync<T>(
        InstanceRecord record,
        Func<EmulatorControlChannel, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var discovery = await EmulatorDiscovery.ReadAsync(_adb, record.AdbSerial, cancellationToken)
            .ConfigureAwait(false);

        if (discovery.AdbPort != record.ConsolePort + 1
            || discovery.GrpcPort != record.GrpcPort
            || discovery.AvdName != record.AvdName)
        {
            throw new SupervisorOperationException(
                ErrorCodes.ControllerFailure,
                "The engine's advertised identity does not match the managed instance; input was not sent.");
        }

        using var channel = EmulatorControlChannel.Connect(discovery.GrpcPort, discovery.GrpcToken, "bearer");
        return await action(channel).ConfigureAwait(false);
    }

    private async Task WithControllerAsync(
        InstanceRecord record,
        Func<EmulatorControlChannel, Task> action,
        CancellationToken cancellationToken)
    {
        await WithControllerAsync(
            record,
            async channel =>
            {
                await action(channel).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private Runtime RuntimeFor(string name)
    {
        var record = Get(name);
        var runtime = _runtimes.GetOrAdd(name, _ => new Runtime(record));
        runtime.Record = record;
        return runtime;
    }

    private void EnsureDiskFree(long requiredBytes, string action) =>
        DiskSpaceGuard.Ensure(_paths.Root, requiredBytes, action);

    private async Task<string?> SafeVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _sdk.GetEmulatorVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 32
            || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$"))
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest,
                "Instance names must be 1-32 characters of lowercase letters, digits, and hyphens.");
        }
    }

    private static void ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)
            || tag.Length > 64
            || !System.Text.RegularExpressions.Regex.IsMatch(tag, "^[A-Za-z0-9._-]+$"))
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, "Snapshot tags must be 1-64 characters of letters, digits, dot, dash, or underscore.");
        }
    }
}
