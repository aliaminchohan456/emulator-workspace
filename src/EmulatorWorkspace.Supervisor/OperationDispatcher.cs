using System.Diagnostics;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Host;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Storage;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Supervisor;

public interface IEventSink
{
    Task SendEventAsync(EventEnvelope envelope);

    void TrackSubscription(string id);
}

public sealed class OperationDispatcher
{
    private readonly WorkspacePaths _paths;
    private readonly SdkLocator _sdk;
    private readonly SupervisorStore _store;
    private readonly InstanceManager _manager;
    private readonly Journal _journal;
    private readonly Action _requestShutdown;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    public OperationDispatcher(
        WorkspacePaths paths,
        SdkLocator sdk,
        SupervisorStore store,
        InstanceManager manager,
        Journal journal,
        Action requestShutdown)
    {
        _paths = paths;
        _sdk = sdk;
        _store = store;
        _manager = manager;
        _journal = journal;
        _requestShutdown = requestShutdown;
    }

    public async Task<ResponseEnvelope> ExecuteAsync(
        RequestEnvelope request,
        PeerInfo peer,
        IEventSink sink,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var startedUtc = DateTimeOffset.UtcNow;
        var argsJson = request.Args?.ToJsonString(PipeFraming.JsonOptions) ?? "{}";
        var callerName = $"{peer.ProcessName}#{peer.ProcessId} session={peer.SessionId}";

        _store.RecordOperation(
            request.Id, request.Op, "local_user", callerName, argsJson,
            startedUtc, null, "started", null, null, null);

        try
        {
            var agentClaim = request.Args?["_agent"] is JsonValue value && value.TryGetValue<bool>(out var isAgent) && isAgent;
            if (agentClaim)
            {
                _journal.Append(
                    "denied",
                    new OperationRequest(request.Op, CallerIdentity.Agent("supervisor-agent-claim"), null),
                    null,
                    null);
                throw new SupervisorOperationException(
                    ErrorCodes.Denied,
                    "Agent access is disabled. Pair a client from the desktop to create a scoped grant.");
            }

            var data = await DispatchAsync(request.Op, request.Args, sink, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var response = new ResponseEnvelope
            {
                Id = request.Id,
                Ok = true,
                Code = ErrorCodes.Ok,
                Message = "ok",
                Data = data,
                DurationMs = (long)stopwatch.ElapsedMilliseconds,
            };
            _store.RecordOperation(
                request.Id, request.Op, "local_user", callerName, argsJson,
                startedUtc, DateTimeOffset.UtcNow, "ok", ErrorCodes.Ok, "ok", (long)stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (SupervisorOperationException ex)
        {
            stopwatch.Stop();
            _store.RecordOperation(
                request.Id, request.Op, "local_user", callerName, argsJson,
                startedUtc, DateTimeOffset.UtcNow, "failed", ex.Code, ex.Message, (long)stopwatch.ElapsedMilliseconds);
            return new ResponseEnvelope
            {
                Id = request.Id,
                Ok = false,
                Code = ex.Code,
                Message = ex.Message,
                Data = new JsonObject { ["retryable"] = ex.Retryable },
                DurationMs = (long)stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _store.RecordOperation(
                request.Id, request.Op, "local_user", callerName, argsJson,
                startedUtc, DateTimeOffset.UtcNow, "cancelled", ErrorCodes.Cancelled, "cancelled",
                (long)stopwatch.ElapsedMilliseconds);
            return new ResponseEnvelope
            {
                Id = request.Id,
                Ok = false,
                Code = ErrorCodes.Cancelled,
                Message = "Operation cancelled.",
                DurationMs = (long)stopwatch.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _store.RecordOperation(
                request.Id, request.Op, "local_user", callerName, argsJson,
                startedUtc, DateTimeOffset.UtcNow, "error", ErrorCodes.Internal, ex.Message,
                (long)stopwatch.ElapsedMilliseconds);
            return new ResponseEnvelope
            {
                Id = request.Id,
                Ok = false,
                Code = ErrorCodes.Internal,
                Message = ex.Message,
                DurationMs = (long)stopwatch.ElapsedMilliseconds,
            };
        }
    }

    private async Task<JsonNode?> DispatchAsync(
        string op,
        JsonObject? args,
        IEventSink sink,
        CancellationToken cancellationToken)
    {
        var name = JsonArgs.String(args, "name") ?? "";

        switch (op)
        {
            case OperationNames.SupervisorStatus:
                return new JsonObject
                {
                    ["version"] = SupervisorProtocol.Version,
                    ["root"] = _paths.Root,
                    ["pid"] = Environment.ProcessId,
                    ["session"] = Process.GetCurrentProcess().SessionId,
                    ["user"] = Environment.UserDomainName + "\\" + Environment.UserName,
                    ["startedUtc"] = _startedUtc.ToString("O"),
                    ["database"] = _store.DatabasePath,
                    ["migrationError"] = _store.LastMigrationError,
                    ["instances"] = _manager.List().Count,
                };

            case OperationNames.SupervisorShutdown:
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    _requestShutdown();
                });
                return new JsonObject { ["shuttingDown"] = true, ["guestsStopped"] = false };

            case OperationNames.Doctor:
                return await DoctorAsync(cancellationToken).ConfigureAwait(false);

            case OperationNames.DiagnosticsSummary:
                return new JsonObject { ["summary"] = await DiagnosticsSummaryAsync(cancellationToken).ConfigureAwait(false) };

            case OperationNames.InstanceList:
            {
                var array = new JsonArray();
                foreach (var record in _manager.List())
                {
                    array.Add(InstanceJson(record));
                }

                return new JsonObject { ["instances"] = array };
            }

            case OperationNames.InstanceCreate:
                return InstanceJson(await _manager.CreateAsync(
                    name,
                    JsonArgs.String(args, "image"),
                    JsonArgs.String(args, "device"),
                    JsonArgs.Int(args, "console"),
                    JsonArgs.Int(args, "grpc"),
                    JsonArgs.String(args, "gpu"),
                    JsonArgs.Bool(args, "headless", true),
                    cancellationToken).ConfigureAwait(false));

            case OperationNames.InstanceStart:
                return await _manager.StartAsync(
                    name,
                    JsonArgs.String(args, "gpu"),
                    JsonArgs.Bool(args, "headless", true),
                    JsonArgs.Bool(args, "coldBoot", true),
                    JsonArgs.String(args, "ramdisk"),
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.InstanceStop:
                return await _manager.StopAsync(name, JsonArgs.Bool(args, "force"), cancellationToken)
                    .ConfigureAwait(false);

            case OperationNames.InstanceRestart:
            {
                var record = _manager.Get(name);
                await _manager.StopAsync(name, false, cancellationToken).ConfigureAwait(false);
                return await _manager.StartAsync(
                    name, record.GpuMode, record.Headless, true, record.RamdiskPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            case OperationNames.InstanceColdBoot:
                return await _manager.StartAsync(
                    name, null, null, true, null, cancellationToken).ConfigureAwait(false);

            case OperationNames.InstanceStatus:
                return await _manager.StatusAsync(name, cancellationToken).ConfigureAwait(false);

            case OperationNames.InstanceWait:
            {
                var timeout = TimeSpan.FromSeconds(JsonArgs.Int(args, "timeoutSeconds") ?? 600);
                return await _manager.WaitAsync(name, timeout, cancellationToken).ConfigureAwait(false);
            }

            case "instance.verify":
                return await _manager.VerifyTargetAsync(name, cancellationToken).ConfigureAwait(false);

            case "controller.probe":
                return await _manager.ControllerProbeAsync(name, cancellationToken).ConfigureAwait(false);

            case OperationNames.InstanceDelete:
                return await _manager.DeleteAsync(name, cancellationToken).ConfigureAwait(false);

            case OperationNames.FrameSubscribe:
            {
                var width = JsonArgs.Int(args, "width") ?? 540;
                var height = JsonArgs.Int(args, "height") ?? 1200;
                var format = JsonArgs.String(args, "format") ?? "rgb";
                var id = await _manager.SubscribeFramesAsync(
                        name, format, width, height, sink.SendEventAsync, cancellationToken)
                    .ConfigureAwait(false);
                sink.TrackSubscription(id);
                return new JsonObject
                {
                    ["subscription"] = id,
                    ["name"] = name,
                    ["width"] = width,
                    ["path"] = _manager.FramePath(name, format),
                };
            }

            case OperationNames.FrameUnsubscribe:
            {
                var subscription = JsonArgs.String(args, "subscription")
                                   ?? throw new SupervisorOperationException(
                                       ErrorCodes.InvalidRequest, "subscription is required.");
                await _manager.UnsubscribeAsync(subscription, cancellationToken).ConfigureAwait(false);
                return new JsonObject { ["subscription"] = subscription, ["removed"] = true };
            }

            case OperationNames.FrameSnapshot:
            {
                var width = JsonArgs.Int(args, "width") ?? 1080;
                var png = await _manager.CapturePngAsync(name, width, JsonArgs.String(args, "via"), cancellationToken).ConfigureAwait(false);
                return new JsonObject
                {
                    ["name"] = name,
                    ["bytes"] = png.Length,
                    ["pngBase64"] = Convert.ToBase64String(png),
                };
            }

            case OperationNames.FrameTrace:
                return new JsonObject { ["name"] = name, ["samples"] = _manager.Trace(name) };

            case OperationNames.InputTap:
                return await _manager.TapAsync(
                    name,
                    JsonArgs.Int(args, "x") ?? throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "x is required."),
                    JsonArgs.Int(args, "y") ?? throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "y is required."),
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.InputSwipe:
                return await _manager.SwipeAsync(
                    name,
                    JsonArgs.Int(args, "x1") ?? 0,
                    JsonArgs.Int(args, "y1") ?? 0,
                    JsonArgs.Int(args, "x2") ?? 0,
                    JsonArgs.Int(args, "y2") ?? 0,
                    JsonArgs.Int(args, "durationMs") ?? 300,
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.InputKey:
                return await _manager.KeyAsync(
                    name,
                    JsonArgs.String(args, "key") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "key is required."),
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.DisplayRotate:
                return await _manager.RotateAsync(name, JsonArgs.Int(args, "rotation") ?? 0, cancellationToken)
                    .ConfigureAwait(false);

            case OperationNames.AppInstall:
                return await _manager.InstallAsync(
                    name,
                    JsonArgs.String(args, "path") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "path is required."),
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.AppLaunch:
                return await _manager.LaunchAsync(
                    name,
                    JsonArgs.String(args, "package") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "package is required."),
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.AppForeground:
                return await _manager.ForegroundStatusAsync(name, cancellationToken).ConfigureAwait(false);

            case OperationNames.LogcatSubscribe:
            {
                var id = await _manager.SubscribeLogcatAsync(name, sink.SendEventAsync, cancellationToken)
                    .ConfigureAwait(false);
                sink.TrackSubscription(id);
                return new JsonObject { ["subscription"] = id, ["name"] = name };
            }

            case OperationNames.LogcatUnsubscribe:
            {
                var subscription = JsonArgs.String(args, "subscription")
                                   ?? throw new SupervisorOperationException(
                                       ErrorCodes.InvalidRequest, "subscription is required.");
                await _manager.UnsubscribeAsync(subscription, cancellationToken).ConfigureAwait(false);
                return new JsonObject { ["subscription"] = subscription, ["removed"] = true };
            }

            case OperationNames.SnapshotSave:
                return await _manager.SnapshotAsync(
                    name,
                    JsonArgs.String(args, "tag") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "tag is required."),
                    save: true,
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.SnapshotLoad:
                return await _manager.SnapshotAsync(
                    name,
                    JsonArgs.String(args, "tag") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "tag is required."),
                    save: false,
                    cancellationToken).ConfigureAwait(false);

            default:
                throw new SupervisorOperationException(
                    ErrorCodes.UnknownOperation, $"Unknown operation '{op}'.");
        }
    }

    private async Task<JsonObject> DoctorAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new HostDiagnostics(_sdk);
        var report = await diagnostics.CollectAsync(_paths.Root, cancellationToken).ConfigureAwait(false);
        var images = new JsonArray();
        foreach (var image in report.AndroidSdk.Images)
        {
            images.Add(new JsonObject
            {
                ["packageId"] = image.PackageId,
                ["revision"] = image.Revision,
                ["systemImageBytes"] = image.SystemImageBytes,
            });
        }

        return new JsonObject
        {
            ["collectedUtc"] = report.CollectedUtc.ToString("O"),
            ["machineName"] = report.MachineName,
            ["operatingSystem"] = new JsonObject
            {
                ["productName"] = report.OperatingSystem.ProductName,
                ["displayVersion"] = report.OperatingSystem.DisplayVersion,
                ["build"] = report.OperatingSystem.Build,
                ["ubr"] = report.OperatingSystem.Ubr,
                ["edition"] = report.OperatingSystem.Edition,
                ["full"] = report.OperatingSystem.Full,
            },
            ["architecture"] = report.Architecture,
            ["processArchitecture"] = report.ProcessArchitecture,
            ["processorName"] = report.ProcessorName,
            ["processorCount"] = report.ProcessorCount,
            ["logicalProcessors"] = report.LogicalProcessors,
            ["totalMemoryBytes"] = report.TotalMemoryBytes,
            ["availableMemoryBytes"] = report.AvailableMemoryBytes,
            ["totalMemoryGiB"] = Math.Round(report.TotalMemoryGiB, 2),
            ["availableMemoryGiB"] = Math.Round(report.AvailableMemoryGiB, 2),
            ["workspaceRoot"] = report.WorkspaceRoot,
            ["workspaceDriveFreeBytes"] = report.WorkspaceDriveFreeBytes,
            ["workspaceDriveTotalBytes"] = report.WorkspaceDriveTotalBytes,
            ["workspaceDriveFreeGiB"] = Math.Round(report.WorkspaceDriveFreeGiB, 2),
            ["gpu"] = new JsonObject
            {
                ["name"] = report.Gpu.Name,
                ["driverVersion"] = report.Gpu.DriverVersion,
                ["driverDate"] = report.Gpu.DriverDate,
            },
            ["accelerationVerdict"] = report.AccelerationVerdict,
            ["hyperV"] = report.HyperV,
            ["androidSdk"] = new JsonObject
            {
                ["root"] = report.AndroidSdk.Root,
                ["emulatorPath"] = report.AndroidSdk.EmulatorPath,
                ["emulatorVersion"] = report.AndroidSdk.EmulatorVersion,
                ["adbPath"] = report.AndroidSdk.AdbPath,
                ["adbVersion"] = report.AndroidSdk.AdbVersion,
                ["images"] = images,
            },
            ["pinned"] = new JsonObject
            {
                ["image"] = "system-images;android-35;default;x86_64",
                ["imageRevision"] = _sdk.ImageRevision("system-images;android-35;default;x86_64"),
                ["emulator"] = "37.2.12.0",
                ["buildId"] = "16428233",
            },
        };
    }
    private async Task<string> DiagnosticsSummaryAsync(CancellationToken cancellationToken)
    {
        var doctor = await DoctorAsync(cancellationToken).ConfigureAwait(false);
        var sdk = doctor["androidSdk"]!.AsObject();
        var images = sdk["images"]!.AsArray()
            .Select(i => $"  {i!["packageId"]} rev {i["revision"]}")
            .ToList();

        return string.Join(Environment.NewLine, new[]
        {
            "Emulator Workspace diagnostics (redacted)",
            $"Generated: {doctor["collectedUtc"]}",
            $"Host: {doctor["machineName"]} / {doctor["os"]}",
            $"CPU: {doctor["processorName"]} ({doctor["processorCount"]} logical)",
            $"Memory: {BytesToGiB(doctor["totalMemoryBytes"])} GiB total, {BytesToGiB(doctor["availableMemoryBytes"])} GiB free",
            $"Workspace root: {doctor["workspaceRoot"]}",
            $"Workspace drive free: {BytesToGiB(doctor["workspaceDriveFreeBytes"])} GiB",
            $"GPU: {doctor["gpu"]!["name"]} driver {doctor["gpu"]!["driverVersion"]}",
            $"Acceleration: {doctor["accelerationVerdict"]}",
            $"Emulator: {sdk["emulatorVersion"]}",
            $"ADB: {sdk["adbVersion"]}",
            "Installed images:",
            string.Join(Environment.NewLine, images),
            "Controller tokens and discovery files are never included.",
        });
    }

    private JsonObject InstanceJson(InstanceRecord record) => new()
    {
        ["name"] = record.Name,
        ["avdName"] = record.AvdName,
        ["imagePackage"] = record.ImagePackage,
        ["deviceProfile"] = record.DeviceProfile,
        ["consolePort"] = record.ConsolePort,
        ["grpcPort"] = record.GrpcPort,
        ["serial"] = record.AdbSerial,
        ["state"] = record.State.ToString(),
        ["enginePid"] = record.EnginePid,
        ["gpu"] = record.GpuMode,
        ["headless"] = record.Headless,
    };

    private static double BytesToGiB(JsonNode? node) =>
        Math.Round((node?.GetValue<long>() ?? 0) / 1024d / 1024d / 1024d, 1);
}
