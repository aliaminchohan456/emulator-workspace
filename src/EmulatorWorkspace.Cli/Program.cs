using System.Globalization;
using EmulatorWorkspace.Controller;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            HelpText.Print();
            return args.Length == 0 ? 2 : 0;
        }

        var parsed = Arguments.Parse(args);
        if (!parsed.Has("direct") && parsed.Command != "engine-host")
        {
            try
            {
                return await SupervisorCommands.RunAsync(parsed).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SupervisorUnavailableException or IOException
                                           )
            {
                Console.Error.WriteLine("ERROR " + ex.Message);
                return 3;
            }
        }

        try
        {
            return await CliRunner.RunAsync(args).ConfigureAwait(false);
        }
        catch (OperationDeniedException ex)
        {
            Console.Error.WriteLine($"DENIED [{ex.Code}] {ex.Message}");
            return 4;
        }
        catch (OperationFailedException ex)
        {
            Console.Error.WriteLine($"FAILED [{ex.Code}] {ex.Message}");
            return 5;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or FileNotFoundException or TimeoutException
                                       or InstanceNotFoundException)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 3;
        }
    }
}

internal sealed class CliRunner
{
    private readonly WorkspacePaths _paths;
    private readonly SdkLocator _sdk;
    private readonly InstanceStore _store;
    private readonly EmulatorEngine _engine;
    private readonly AvdProvisioner _provisioner;
    private readonly OperationRegistry _registry;
    private readonly Arguments _args;
    private readonly CallerIdentity _caller;

    private CliRunner(
        WorkspacePaths paths,
        SdkLocator sdk,
        InstanceStore store,
        EmulatorEngine engine,
        AvdProvisioner provisioner,
        OperationRegistry registry,
        Arguments args,
        CallerIdentity caller)
    {
        _paths = paths;
        _sdk = sdk;
        _store = store;
        _engine = engine;
        _provisioner = provisioner;
        _registry = registry;
        _args = args;
        _caller = caller;
    }

    public static async Task<int> RunAsync(string[] rawArgs)
    {
        var arguments = Arguments.Parse(rawArgs);
        var paths = WorkspacePaths.Resolve(arguments.Optional("root"));
        paths.EnsureLayout();
        var sdk = SdkLocator.Resolve(arguments.Optional("sdk"));
        var store = new InstanceStore(paths);
        var engine = new EmulatorEngine(sdk, paths, store);
        var provisioner = new AvdProvisioner(sdk);

        using var journal = new Journal(paths.JournalPath);
        var policy = new AuthorizationPolicy { AgentAccessEnabled = false };
        var registry = new OperationRegistry(policy, journal);
        var caller = arguments.Has("agent")
            ? CallerIdentity.Agent(arguments.Optional("agent-name") ?? "cli-agent")
            : CallerIdentity.LocalOperator();

        var runner = new CliRunner(paths, sdk, store, engine, provisioner, registry, arguments, caller);
        runner.RegisterOperations();
        return await runner.DispatchAsync().ConfigureAwait(false);
    }

    private void RegisterOperations()
    {
        _registry.Register(OperationCatalog.Doctor, (request, ct) => Wrap(DoctorAsync, ct));

        _registry.Register(OperationCatalog.InstanceList, (request, ct) =>
        {
            foreach (var record in _store.List())
            {
                Console.WriteLine($"{record.Name}\t{record.AdbSerial}\tgrpc:{record.GrpcPort}\t{record.State}\t{record.ImagePackage}");
            }

            return Task.FromResult(OperationResult.Ok("listed", TimeSpan.Zero));
        });

        _registry.Register(OperationCatalog.InstanceStatus, (request, ct) => Wrap(StatusAsync, ct));
        _registry.Register(OperationCatalog.InstanceCreate, (request, ct) => Wrap(CreateAsync, ct));
        _registry.Register(OperationCatalog.InstanceStart, (request, ct) => Wrap(StartAsync, ct));
        _registry.Register(OperationCatalog.InstanceStop, (request, ct) => Wrap(StopAsync, ct));
        _registry.Register(OperationCatalog.FrameCapture, (request, ct) => Wrap(FrameAsync, ct));
        _registry.Register(OperationCatalog.InputTap, (request, ct) => Wrap(TapAsync, ct));
        _registry.Register(OperationCatalog.SnapshotSave, (request, ct) => Wrap(token => SnapshotAsync(true, token), ct));
        _registry.Register(OperationCatalog.SnapshotLoad, (request, ct) => Wrap(token => SnapshotAsync(false, token), ct));
    }

    private static async Task<OperationResult> Wrap(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await action(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return OperationResult.Ok("ok", stopwatch.Elapsed);
    }

    private async Task<int> DispatchAsync()
    {
        var command = _args.Command;

        if (command == "engine-host")
        {
            // Internal launcher child: owns engine stdio and log pumping.
            try
            {
                return await _engine.RunHostAsync(RequireName()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var hostLog = _paths.InstanceHostLogPath(_args.Optional("name") ?? "unknown");
                await File.AppendAllTextAsync(
                    hostLog,
                    $"{DateTimeOffset.UtcNow:O} engine-host failed: {ex}{Environment.NewLine}")
                    .ConfigureAwait(false);
                return 1;
            }
        }

        Func<Task> invoke = command switch
        {
            "doctor" => () => InvokeAsync(OperationCatalog.Doctor),
            "list" => () => InvokeAsync(OperationCatalog.InstanceList),
            "create" => () => InvokeAsync(OperationCatalog.InstanceCreate),
            "start" => () => InvokeAsync(OperationCatalog.InstanceStart),
            "stop" => () => InvokeAsync(OperationCatalog.InstanceStop),
            "status" => () => InvokeAsync(OperationCatalog.InstanceStatus),
            "frame" => () => InvokeAsync(OperationCatalog.FrameCapture),
            "tap" => () => InvokeAsync(OperationCatalog.InputTap),
            "snapshot-save" => () => InvokeAsync(OperationCatalog.SnapshotSave),
            "snapshot-load" => () => InvokeAsync(OperationCatalog.SnapshotLoad),
            "wait" => WaitAsync,
            "verify-target" => VerifyTargetAsync,
            "controller-probe" => ControllerProbeAsync,
            "destroy" => DestroyAsync,
            _ => throw new ArgumentException($"Unknown command '{command}'. Run 'wsctl --help'."),
        };

        await invoke().ConfigureAwait(false);
        return 0;
    }

    private Task InvokeAsync(OperationDescriptor descriptor)
    {
        var request = new OperationRequest(
            descriptor.Name,
            _caller,
            new Dictionary<string, string?>
            {
                ["instance"] = _args.Optional("name"),
                ["tag"] = _args.Optional("tag"),
            });

        return InvokeCoreAsync(request);
    }

    private async Task InvokeCoreAsync(OperationRequest request)
    {
        var result = await _registry.InvokeAsync(request).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new OperationFailedException(result.Code, result.Message);
        }
    }

    private async Task DoctorAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new Core.Host.HostDiagnostics(_sdk);
        var report = await diagnostics.CollectAsync(_paths.Root, cancellationToken).ConfigureAwait(false);
        var json = System.Text.Json.JsonSerializer.Serialize(
            report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var outputPath = _args.Optional("json");
        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
        }

        if (_args.Has("quiet"))
        {
            Console.WriteLine(outputPath ?? json);
            return;
        }

        Console.WriteLine($"Host        : {report.MachineName} / {report.OperatingSystem.Full}");
        Console.WriteLine($"Architecture: {report.Architecture} (process {report.ProcessArchitecture})");
        Console.WriteLine($"CPU         : {report.ProcessorName} / {report.LogicalProcessors} logical processors");
        Console.WriteLine($"Memory      : {report.TotalMemoryGiB:N1} GiB total, {report.AvailableMemoryGiB:N1} GiB available");
        Console.WriteLine($"Disk        : {report.WorkspaceDriveFreeGiB:N1} GiB free on {Path.GetPathRoot(report.WorkspaceRoot)}");
        Console.WriteLine($"GPU         : {report.Gpu.Name} driver {report.Gpu.DriverVersion}");
        Console.WriteLine($"Acceleration: {report.AccelerationVerdict}");
        Console.WriteLine($"Hyper-V     : {report.HyperV}");
        Console.WriteLine($"SDK         : {report.AndroidSdk.Root}");
        Console.WriteLine($"Emulator    : {report.AndroidSdk.EmulatorVersion}");
        Console.WriteLine($"ADB         : {report.AndroidSdk.AdbVersion}");
        Console.WriteLine("Images      :");
        foreach (var image in report.AndroidSdk.Images)
        {
            Console.WriteLine($"  {image.PackageId}  rev {image.Revision}  {image.SystemImageBytes / 1024d / 1024d / 1024d:N2} GiB");
        }

        if (outputPath is not null)
        {
            Console.WriteLine($"JSON        : {Path.GetFullPath(outputPath)}");
        }
    }

    private async Task CreateAsync(CancellationToken cancellationToken)
    {
        var name = RequireName();
        if (_store.Exists(name))
        {
            throw new InvalidOperationException($"Instance '{name}' already exists.");
        }

        var image = SdkLocator.NormalizeImageId(_args.Optional("image") ?? "system-images;android-35;default;x86_64");
        if (!_sdk.IsImageInstalled(image))
        {
            throw new InvalidOperationException(
                $"System image '{image}' is not installed. Install it with:" + Environment.NewLine +
                $"  sdkmanager \"{image}\"");
        }

        var device = _args.Optional("device") ?? "pixel_6";
        var gpu = _args.Optional("gpu") ?? EngineArguments.DefaultGpuMode;
        var existing = _store.List();

        var consolePort = _args.OptionalInt("console");
        var grpcPort = _args.OptionalInt("grpc");
        if (consolePort is null || grpcPort is null)
        {
            var allocated = PortAllocator.Allocate(existing);
            consolePort ??= allocated.ConsolePort;
            grpcPort ??= allocated.GrpcPort;
        }

        if (consolePort % 2 != 0)
        {
            throw new ArgumentException("The emulator console port must be even.");
        }

        if (existing.Any(r => r.ConsolePort == consolePort || r.GrpcPort == grpcPort))
        {
            throw new InvalidOperationException("Requested port is already owned by another managed instance.");
        }

        var avdName = "ws_" + name;
        await _provisioner.CreateAsync(avdName, image, device, _paths.AvdHome, gpu, cancellationToken)
            .ConfigureAwait(false);

        var record = new InstanceRecord
        {
            Name = name,
            AvdName = avdName,
            ImagePackage = image,
            DeviceProfile = device,
            AvdHome = _paths.AvdHome,
            ConsolePort = consolePort.Value,
            GrpcPort = grpcPort.Value,
            State = InstanceState.Defined,
            Headless = !_args.Has("windowed"),
            GpuMode = gpu,
            EngineVersion = await SafeVersionAsync(cancellationToken).ConfigureAwait(false),
        };

        _store.Save(record);
        Console.WriteLine($"created {name}");
        Console.WriteLine($"  avd       : {avdName} ({device})");
        Console.WriteLine($"  image     : {image} (rev {_sdk.ImageRevision(image)})");
        Console.WriteLine($"  adb serial: {record.AdbSerial}");
        Console.WriteLine($"  controller: 127.0.0.1:{record.GrpcPort} (token auth)");
        Console.WriteLine($"  avd home  : {_paths.AvdHome}");
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var name = RequireName();
        var record = _store.Get(name);

        if (_engine.IsEngineAlive(record))
        {
            Console.WriteLine($"{name} is already running (pid {record.EnginePid}).");
            return;
        }

        // A previous host may have outlived a crashed engine; reap it before
        // publishing a new launch for the same instance.
        _engine.ReconcileHost(record);

        var gpu = _args.Optional("gpu") ?? record.GpuMode ?? EngineArguments.DefaultGpuMode;
        var headless = _args.Has("windowed") ? false : record.Headless;
        var ramdisk = _args.Optional("ramdisk");
        if (ramdisk is not null)
        {
            if (ramdisk.Equals("stock", StringComparison.OrdinalIgnoreCase)
                || ramdisk.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                ramdisk = null;
            }
            else
            {
                ramdisk = Path.GetFullPath(ramdisk);
                if (!File.Exists(ramdisk))
                {
                    throw new FileNotFoundException("The replacement ramdisk does not exist.", ramdisk);
                }
            }
        }

        _store.Update(name, r =>
        {
            r.GpuMode = gpu;
            r.Headless = headless;
            r.RamdiskPath = ramdisk;
            r.State = InstanceState.Starting;
            r.LastError = null;
        });

        LaunchEngineHost(name);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        InstanceRecord? updated = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            updated = _store.TryGet(name);
            if (updated?.EnginePid is not null)
            {
                break;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        if (updated?.EnginePid is null)
        {
            throw new TimeoutException(
                $"The engine host did not publish a process id for '{name}'. See {_paths.InstanceHostLogPath(name)}.");
        }

        Console.WriteLine($"{name} starting: pid {updated.EnginePid}, adb {updated.AdbSerial}, controller {updated.GrpcPort}");
        Console.WriteLine($"  log: {_paths.InstanceLogPath(name)}");
    }

    private void LaunchEngineHost(string name)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Cannot determine the running executable path.");
        var startInfo = new System.Diagnostics.ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            WorkingDirectory = _paths.Root,
        };

        if (Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(
                typeof(Program).Assembly.Location);
        }

        startInfo.ArgumentList.Add("engine-host");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(_paths.Root);
        startInfo.ArgumentList.Add("--sdk");
        startInfo.ArgumentList.Add(_sdk.SdkRoot);

        var process = System.Diagnostics.Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start the engine host.");

        _store.Update(name, r =>
        {
            r.HostPid = process.Id;
            r.HostStartTimeUtc = ProcessIdentity.CaptureStartTime(process.Id);
        });

        File.AppendAllText(
            _paths.InstanceHostLogPath(name),
            $"{DateTimeOffset.UtcNow:O} engine-host pid {process.Id}{Environment.NewLine}");
    }

    private async Task WaitAsync()
    {
        var name = RequireName();
        var record = _store.Get(name);
        var timeout = TimeSpan.FromSeconds(_args.OptionalInt("timeout") ?? 480);

        if (!_engine.IsEngineAlive(record))
        {
            throw new InvalidOperationException(
                $"{name} has no live engine process. Run 'wsctl start --name {name}' first.");
        }

        var ready = _engine.Adb.WaitForBootCompletedAsync(record.AdbSerial, timeout);
        var elapsed = await ready.ConfigureAwait(false);
        if (elapsed is null)
        {
            throw new TimeoutException($"{name} did not reach boot completion within {timeout.TotalSeconds:N0}s.");
        }

        _store.Update(name, r => r.State = InstanceState.Running);
        var fingerprint = await _engine.Adb
            .GetPropAsync(record.AdbSerial, "ro.build.fingerprint").ConfigureAwait(false);
        Console.WriteLine($"{name} boot_completed=1 in {elapsed.Value.TotalSeconds:N1}s");
        Console.WriteLine($"  serial     : {record.AdbSerial}");
        Console.WriteLine($"  fingerprint: {fingerprint}");
    }

    private async Task StatusAsync(CancellationToken cancellationToken)
    {
        var status = await _engine.GetStatusAsync(_store.Get(RequireName()), cancellationToken).ConfigureAwait(false);
        if (_args.Has("json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                status,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                }));
            return;
        }

        Console.WriteLine($"{status.Name}: state={status.State} engineAlive={status.EngineAlive} pid={status.EnginePid}");
        Console.WriteLine($"  serial={status.AdbSerial} console={status.ConsolePort} controller={status.GrpcPort}");
        Console.WriteLine($"  adb={status.AdbState} boot_completed={status.BootCompleted}");
        if (status.Fingerprint is not null)
        {
            Console.WriteLine($"  fingerprint={status.Fingerprint}");
            Console.WriteLine($"  abi={status.Abi} sdk={status.SdkLevel}");
        }

        if (status.LastError is not null)
        {
            Console.WriteLine("  lastError=" + status.LastError);
        }
    }

    private async Task FrameAsync(CancellationToken cancellationToken)
    {
        var name = RequireName();
        var record = _store.Get(name);
        var output = _args.Optional("out")
                     ?? Path.Combine(_paths.EvidenceDirectory, $"{name}-frame.png");
        var via = (_args.Optional("via") ?? "auto").ToLowerInvariant();
        var width = _args.OptionalInt("width");

        byte[] bytes;
        string source;
        if (via is "controller" or "auto")
        {
            try
            {
                bytes = await WithControllerAsync(
                    record,
                    channel => channel.GetScreenshotPngAsync(width, cancellationToken: cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                source = "controller";
            }
            catch (Exception) when (via == "auto")
            {
                bytes = await _engine.Adb.ScreencapPngAsync(record.AdbSerial, cancellationToken).ConfigureAwait(false);
                source = "adb";
            }
        }
        else
        {
            bytes = await _engine.Adb.ScreencapPngAsync(record.AdbSerial, cancellationToken).ConfigureAwait(false);
            source = "adb";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllBytesAsync(output, bytes, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"frame {name} via {source}: {bytes.Length} bytes -> {Path.GetFullPath(output)}");
    }

    private async Task TapAsync(CancellationToken cancellationToken)
    {
        var name = RequireName();
        var record = _store.Get(name);
        var x = _args.RequiredInt("x");
        var y = _args.RequiredInt("y");
        var via = (_args.Optional("via") ?? "controller").ToLowerInvariant();

        if (via == "adb")
        {
            await _engine.Adb.TapAsync(record.AdbSerial, x, y, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WithControllerAsync(
                record,
                channel => channel.TapAsync(x, y, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"tap {name} at {x},{y} via {via}");
    }

    private async Task SnapshotAsync(bool save, CancellationToken cancellationToken)
    {
        var name = RequireName();
        var record = _store.Get(name);
        var tag = _args.Optional("tag") ?? throw new ArgumentException("--tag is required.");
        var via = (_args.Optional("via") ?? "adb").ToLowerInvariant();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (via == "controller")
        {
            var result = await WithControllerAsync(
                record,
                channel => save
                    ? channel.SaveSnapshotAsync(tag, cancellationToken)
                    : channel.LoadSnapshotAsync(tag, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (!result.Success)
            {
                throw new InvalidOperationException($"Snapshot {tag} failed: {result.Error}");
            }
        }
        else
        {
            var output = save
                ? await _engine.SaveSnapshotAsync(record, tag, cancellationToken).ConfigureAwait(false)
                : await _engine.LoadSnapshotAsync(record, tag, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (!output.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Snapshot {tag} returned: {output}");
            }
        }

        Console.WriteLine($"snapshot {(save ? "save" : "load")} {name} tag={tag} via={via} in {stopwatch.Elapsed.TotalSeconds:N1}s");
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var record = _store.Get(RequireName());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _engine.StopAsync(record, _args.Has("force"), cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var after = _store.Get(record.Name);
        Console.WriteLine($"{record.Name} stopped in {stopwatch.Elapsed.TotalSeconds:N1}s state={after.State}");
    }

    private async Task DestroyAsync()
    {
        var name = RequireName();
        var record = _store.Get(name);
        await _engine.StopAsync(record, force: true).ConfigureAwait(false);
        _provisioner.Delete(record.AvdName, _paths.AvdHome);
        _store.Delete(name);
        Console.WriteLine($"destroyed {name} (avd {record.AvdName}, ports {record.ConsolePort}/{record.GrpcPort} released)");
    }

    private async Task VerifyTargetAsync()
    {
        var name = RequireName();
        var record = _store.Get(name);
        var alive = _engine.IsEngineAlive(record);

        var transport = await _engine.Adb.GetTransportAsync(record.AdbSerial).ConfigureAwait(false);
        var devices = await ProcessRunner.RunAsync(_sdk.AdbExe, new[] { "devices", "-l" })
            .ConfigureAwait(false);

        var matching = devices.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(record.AdbSerial, StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        var foreign = devices.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("emulator-", StringComparison.Ordinal)
                           && !line.StartsWith(record.AdbSerial, StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        Console.WriteLine($"instance       : {name}");
        Console.WriteLine($"expected serial: {record.AdbSerial} (console {record.ConsolePort}, adb {record.ConsolePort + 1})");
        Console.WriteLine($"engine alive   : {alive} (pid {record.EnginePid})");
        Console.WriteLine($"get-serialno   : {transport}");
        Console.WriteLine($"adb devices    : {(matching.Count == 1 ? matching[0] : "MISMATCH")}");
        if (foreign.Count > 0)
        {
            Console.WriteLine($"other emulators: {string.Join(" | ", foreign)} (excluded from this operation)");
        }

        if (transport != record.AdbSerial || matching.Count != 1)
        {
            throw new InvalidOperationException("ADB target verification failed: the transport does not match the managed instance.");
        }
    }

    private async Task ControllerProbeAsync()
    {
        var name = RequireName();
        var record = _store.Get(name);
        var discovery = await EmulatorDiscovery.ReadAsync(_engine.Adb, record.AdbSerial).ConfigureAwait(false);
        var bindings = PortBindings(discovery.GrpcPort);
        var advertised = ReadAdvertisedEndpoint(name);

        Console.WriteLine($"controller port : {discovery.GrpcPort}");
        Console.WriteLine($"discovery file  : {discovery.DiscoveryPath}");
        Console.WriteLine($"advertised id   : avd={discovery.AvdName} adb={discovery.AdbPort} build={discovery.EmulatorBuild}");
        Console.WriteLine($"engine bind line: {(advertised ?? "not found")}");
        Console.WriteLine($"socket endpoints: {(bindings.Count == 0 ? "no listener row observed" : string.Join(", ", bindings))}");

        var loopbackOnly = advertised is not null
                           && (advertised.StartsWith("127.", StringComparison.Ordinal)
                               || advertised.StartsWith("[::1]", StringComparison.Ordinal));
        var lanReachable = await ProbeLanReachabilityAsync(discovery.GrpcPort).ConfigureAwait(false);

        var identityMatches = discovery.GrpcPort == record.GrpcPort
                              && discovery.AdbPort == record.ConsolePort + 1
                              && string.Equals(discovery.AvdName, record.AvdName, StringComparison.Ordinal);

        string unauthenticated;
        try
        {
            using var anonymous = EmulatorControlChannel.Connect(discovery.GrpcPort, null);
            await anonymous.GetStatusAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            unauthenticated = "ACCEPTED (unexpected)";
        }
        catch (Grpc.Core.RpcException ex)
        {
            unauthenticated = ex.StatusCode.ToString();
        }

        using var channel = EmulatorControlChannel.Connect(discovery.GrpcPort, discovery.GrpcToken, "bearer");
        var status = await channel.GetStatusAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        string wrongToken;
        try
        {
            using var forged = EmulatorControlChannel.Connect(discovery.GrpcPort, new string('0', 88), "bearer");
            await forged.GetStatusAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            wrongToken = "ACCEPTED (unexpected)";
        }
        catch (Grpc.Core.RpcException ex)
        {
            wrongToken = ex.StatusCode.ToString();
        }

        var frame = await channel.GetScreenshotPngAsync(480).ConfigureAwait(false);

        Console.WriteLine($"token           : {discovery.GrpcToken.Length} chars from discovery (value not logged)");
        Console.WriteLine($"no-token call   : {unauthenticated}");
        Console.WriteLine($"forged-token    : {wrongToken}");
        Console.WriteLine($"authed status   : version={status.Version} booted={status.Booted} uptimeMs={status.Uptime}");
        Console.WriteLine($"authed frame    : {frame.Length} bytes (png)");
        Console.WriteLine($"identity match  : {identityMatches}");
        Console.WriteLine($"binding         : {(loopbackOnly ? "loopback-only" : "CHECK REQUIRED")}");
        Console.WriteLine($"lan probe       : {(lanReachable ? "REACHABLE (unexpected)" : "refused/unreachable")}");

        if (!loopbackOnly)
        {
            throw new InvalidOperationException("The controller is not bound exclusively to loopback addresses.");
        }

        if (lanReachable)
        {
            throw new InvalidOperationException("The controller port accepted a connection from a non-loopback address.");
        }

        if (!identityMatches)
        {
            throw new InvalidOperationException("The controller advertisement does not match this managed instance.");
        }

        if (unauthenticated == "ACCEPTED (unexpected)" || wrongToken == "ACCEPTED (unexpected)")
        {
            throw new InvalidOperationException("The controller accepted a call without a valid token.");
        }
    }

    /// <summary>
    /// Reads the engine's own startup line, which is the authoritative record of
    /// the interface and port the controller actually bound.
    /// </summary>
    private string? ReadAdvertisedEndpoint(string instanceName)
    {
        var log = _paths.InstanceLogPath(instanceName);
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

    /// <summary>
    /// Reads the local addresses actually bound to a port from netstat so the
    /// check reflects the OS socket table rather than a .NET-side view.
    /// </summary>
    private static List<string> PortBindings(int port)
    {
        var result = ProcessRunner.RunAsync("netstat.exe", new[] { "-ano", "-p", "TCP" }).GetAwaiter().GetResult();
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

    private async Task<T> WithControllerAsync<T>(
        InstanceRecord record,
        Func<EmulatorControlChannel, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        var discovery = await EmulatorDiscovery.ReadAsync(_engine.Adb, record.AdbSerial, cancellationToken)
            .ConfigureAwait(false);

        if (discovery.GrpcPort != record.GrpcPort
            || discovery.AdbPort != record.ConsolePort + 1
            || !string.Equals(discovery.AvdName, record.AvdName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Discovery identity mismatch: engine advertised avd={discovery.AvdName} " +
                $"adb={discovery.AdbPort} grpc={discovery.GrpcPort}.");
        }

        using var channel = EmulatorControlChannel.Connect(discovery.GrpcPort, discovery.GrpcToken, "bearer");
        return await action(channel).ConfigureAwait(false);
    }

    private async Task WithControllerAsync(
        InstanceRecord record,
        Func<EmulatorControlChannel, Task> action,
        CancellationToken cancellationToken = default)
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

    private string RequireName() =>
        _args.Optional("name") ?? throw new ArgumentException("--name is required.");

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
}

internal sealed class OperationFailedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();

    public string Command => _positional.Count > 0 ? _positional[0] : "";

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = token.IndexOf('=');
                if (equals > 0)
                {
                    result._options[token[2..equals]] = token[(equals + 1)..];
                    continue;
                }

                var key = token[2..];
                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result._options[key] = args[++index];
                }
                else
                {
                    result._options[key] = null;
                }
            }
            else
            {
                result._positional.Add(token);
            }
        }

        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Optional(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public int? OptionalInt(string name)
    {
        var value = Optional(name);
        return value is null ? null : int.Parse(value, CultureInfo.InvariantCulture);
    }

    public int RequiredInt(string name) =>
        OptionalInt(name) ?? throw new ArgumentException($"--{name} must be an integer.");
}

internal static class HelpText
{
    public static void Print()
    {
        Console.WriteLine("""
            wsctl - Android emulator workspace control (Phase 0)

            Usage:
              wsctl doctor [--json <path>] [--sdk <path>] [--root <path>]
              wsctl create --name <n> [--image system-images;android-35;default;x86_64]
                           [--device pixel_6] [--console 5554] [--grpc 8554]
                           [--gpu swiftshader_indirect] [--windowed]
              wsctl start --name <n> [--gpu <mode>] [--windowed] [--ramdisk <img>]
              wsctl wait --name <n> [--timeout 480]
              wsctl status --name <n> [--json]
              wsctl frame --name <n> [--out <png>] [--via auto|controller|adb] [--width 480]
              wsctl tap --name <n> --x <px> --y <px> [--via controller|adb]
              wsctl install --name <n> --apk <path> [--json]
              wsctl build-apks --aab <path> --test-signing [--bundletool <jar>]
              wsctl snapshot-save --name <n> --tag <t> [--via adb|controller]
              wsctl snapshot-load --name <n> --tag <t> [--via adb|controller]
              wsctl verify-target --name <n>
              wsctl controller-probe --name <n>
              wsctl stop --name <n> [--force]
              wsctl destroy --name <n>
              wsctl list

            Global:
              --root <dir>   Runtime state root (default %LOCALAPPDATA%\EmulatorWorkspace)
              --sdk <dir>    Android SDK root (default ANDROID_SDK_ROOT / ANDROID_HOME)
              --agent        Run as an agent caller to exercise the authorization policy

            Notes:
              Agent access is disabled by default; agent calls are denied and journaled.
              Every instance owns one console port, one ADB serial, and one authenticated
              controller port. Commands never address a device that is not managed here.
            """);
    }
}

