using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly AgentService _agents;
    private readonly DeviceLibraryService _library;
    private readonly Action _requestShutdown;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    public OperationDispatcher(
        WorkspacePaths paths,
        SdkLocator sdk,
        SupervisorStore store,
        InstanceManager manager,
        Journal journal,
        AgentService agents,
        DeviceLibraryService library,
        Action requestShutdown)
    {
        _paths = paths;
        _sdk = sdk;
        _store = store;
        _manager = manager;
        _journal = journal;
        _agents = agents;
        _library = library;
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
        var callerKind = "local_user";
        AgentSession? session = null;
        var actor = "operator";
        var idemKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey;
        var digest = Digest(request.Op, argsJson);

        try
        {
            if (!string.IsNullOrWhiteSpace(request.Auth))
            {
                session = _agents.ResolveSession(request.Auth!);
                callerKind = "agent";
                callerName = $"{session.ClientName}/{session.Id}";
                actor = session.Id;
            }
        }
        catch (SupervisorOperationException ex)
        {
            _store.RecordOperation(
                request.Id, request.Op, "agent", "unresolved", argsJson,
                startedUtc, DateTimeOffset.UtcNow, "denied", ex.Code, ex.Message, 0);
            _journal.Append("denied", new OperationRequest(request.Op, CallerIdentity.Agent("unresolved"), null), null, null);
            return Failure(request.Id, ex.Code, ex.Message, ex.Retryable, stopwatch);
        }

        _store.RecordOperation(
            request.Id, request.Op, callerKind, callerName, argsJson,
            startedUtc, null, "started", null, null, null);

        ResponseEnvelope? cached = null;
        if (idemKey is not null)
        {
            var existing = _agents.Store.GetIdempotency(actor, idemKey);
            if (existing is { } value)
            {
                if (!value.Operation.Equals(request.Op, StringComparison.Ordinal)
                    || !value.Digest.Equals(digest, StringComparison.Ordinal))
                {
                    return Failure(
                        request.Id, ErrorCodes.IdempotencyConflict,
                        "This idempotency key was used with different arguments.", false, stopwatch);
                }

                if (value.State is "completed" or "failed" && value.Response is not null)
                {
                    cached = JsonSerializer.Deserialize<ResponseEnvelope>(value.Response, PipeFraming.JsonOptions);
                    if (cached is not null)
                    {
                        cached.Id = request.Id;
                        return cached;
                    }
                }

                if (value.State is "in_progress" or "unknown_outcome")
                {
                    return Failure(
                        request.Id, ErrorCodes.UnknownOutcome,
                        "The mutation outcome is unknown after a restart; reconcile before retrying.", false, stopwatch);
                }
            }

            _agents.Store.BeginIdempotency(actor, idemKey, request.Op, digest);
        }

        try
        {
            var agentClaim = request.Args?["_agent"] is JsonValue value && value.TryGetValue<bool>(out var isAgent) && isAgent;
            if (agentClaim)
            {
                _journal.Append(
                    "denied",
                    new OperationRequest(request.Op, CallerIdentity.Agent(session?.Id ?? "supervisor-agent-claim"), null),
                    null,
                    null);
                throw new SupervisorOperationException(
                    ErrorCodes.Denied,
                    "A caller-declared agent role cannot elevate or impersonate; pair a real session instead.");
            }

            if (session is not null)
            {
                // Re-read durable session state immediately before dispatch so
                // a takeover/revocation that landed while this request was
                // queued cannot authorize input or a mutation.
                session = _agents.EnsureActive(session);
                AuthorizeAgent(session, request.Op, request.Args);
            }

            var data = await DispatchAsync(request.Op, request.Args, sink, cancellationToken, session)
                .ConfigureAwait(false);
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
            if (idemKey is not null)
            {
                _agents.Store.CompleteIdempotency(actor, idemKey, "completed", JsonSerializer.Serialize(response, PipeFraming.JsonOptions));
            }

            _store.RecordOperation(
                request.Id, request.Op, callerKind, callerName, argsJson,
                startedUtc, DateTimeOffset.UtcNow, "ok", ErrorCodes.Ok, "ok", (long)stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (SupervisorOperationException ex)
        {
            stopwatch.Stop();
            var response = Failure(request.Id, ex.Code, ex.Message, ex.Retryable, stopwatch);
            if (idemKey is not null)
            {
                _agents.Store.CompleteIdempotency(actor, idemKey, "failed", JsonSerializer.Serialize(response, PipeFraming.JsonOptions));
            }

            _store.RecordOperation(
                request.Id, request.Op, callerKind, callerName, argsJson,
                startedUtc, DateTimeOffset.UtcNow, "failed", ex.Code, ex.Message, (long)stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            if (idemKey is not null)
            {
                _agents.Store.CompleteIdempotency(actor, idemKey, "unknown_outcome", null);
            }

            _store.RecordOperation(
                request.Id, request.Op, callerKind, callerName, argsJson,
                startedUtc, DateTimeOffset.UtcNow, "cancelled", ErrorCodes.Cancelled, "cancelled",
                (long)stopwatch.ElapsedMilliseconds);
            return Failure(request.Id, ErrorCodes.Cancelled, "Operation cancelled.", false, stopwatch);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (idemKey is not null)
            {
                _agents.Store.CompleteIdempotency(actor, idemKey, "unknown_outcome", null);
            }

            _store.RecordOperation(
                request.Id, request.Op, callerKind, callerName, argsJson,
                startedUtc, DateTimeOffset.UtcNow, "error", ErrorCodes.Internal, ex.Message,
                (long)stopwatch.ElapsedMilliseconds);
            return Failure(request.Id, ErrorCodes.Internal, ex.Message, false, stopwatch);
        }
    }

    private static ResponseEnvelope Failure(string id, string code, string message, bool retryable, Stopwatch stopwatch)
    {
        if (stopwatch.IsRunning)
        {
            stopwatch.Stop();
        }

        return new ResponseEnvelope
        {
            Id = id,
            Ok = false,
            Code = code,
            Message = message,
            Data = new JsonObject { ["retryable"] = retryable },
            DurationMs = (long)stopwatch.ElapsedMilliseconds,
        };
    }

    private static string Digest(string op, string argsJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(op + "\n" + argsJson))).ToLowerInvariant();

    private void AuthorizeAgent(AgentSession session, string op, JsonObject? args)
    {
        if (op is OperationNames.AgentPairApprove or OperationNames.AgentSessionList
            or OperationNames.AgentSessionRevoke or OperationNames.AgentTakeover
            or OperationNames.AgentApproveAction or OperationNames.InstanceInputHold
            or OperationNames.InstanceInputGate)
        {
            throw new SupervisorOperationException(
                ErrorCodes.ForbiddenScope, "This operation is reserved for the local operator.");
        }

        var scope = RequiredScope(op);
        if (scope is null)
        {
            throw new SupervisorOperationException(
                ErrorCodes.ForbiddenScope, $"Operation '{op}' is not available to agent sessions.");
        }

        _agents.RequireScope(session, scope);
        if (IsInstanceBound(op))
        {
            _agents.BindInstance(session, args);
        }
    }

    private static string? RequiredScope(string op) => op switch
    {
        OperationNames.InstanceStatus or OperationNames.ObservationScreenshot
            or OperationNames.ObservationTree or OperationNames.LogsRead
            or OperationNames.EventPoll or OperationNames.ArtifactRead or OperationNames.UiAssert
            or OperationNames.InstanceAdbIdentity => AgentService.ScopeObserve,
        OperationNames.InputTap or OperationNames.InputText or OperationNames.InputKey
            or OperationNames.InputSwipe => AgentService.ScopeInput,
        OperationNames.AppInstall or OperationNames.AppLaunch or OperationNames.AppForeground =>
            AgentService.ScopeApp,
        OperationNames.InstanceStart or OperationNames.InstanceWait or OperationNames.InstanceStop
            or OperationNames.InstanceRestart or OperationNames.DeviceCreateDisposable =>
            AgentService.ScopeLifecycle,
        OperationNames.NetworkProfile => AgentService.ScopeNetwork,
        OperationNames.SnapshotSave or OperationNames.SnapshotLoad => AgentService.ScopeRestore,
        OperationNames.DeviceCleanup => AgentService.ScopeCleanup,
        _ => null,
    };

    private static bool IsInstanceBound(string op) => op is
        OperationNames.InstanceStatus or OperationNames.InstanceStart or OperationNames.InstanceWait
        or OperationNames.InstanceStop or OperationNames.InstanceRestart or OperationNames.ObservationScreenshot
        or OperationNames.ObservationTree or OperationNames.InputTap or OperationNames.InputText
        or OperationNames.InputKey or OperationNames.InputSwipe or OperationNames.AppInstall
        or OperationNames.AppLaunch or OperationNames.AppForeground or OperationNames.LogsRead
        or OperationNames.EventPoll or OperationNames.NetworkProfile or OperationNames.SnapshotSave
        or OperationNames.SnapshotLoad or OperationNames.DeviceCleanup or OperationNames.ArtifactRead
        or OperationNames.DeviceCreateDisposable;

    private async Task<JsonNode?> DispatchAsync(
        string op,
        JsonObject? args,
        IEventSink sink,
        CancellationToken cancellationToken,
        AgentSession? session)
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
                if (session is not null)
                {
                    return await _agents.TapAsync(session, args ?? new JsonObject(), cancellationToken)
                        .ConfigureAwait(false);
                }

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
            {
                var path = JsonArgs.String(args, "path");
                if (session is not null)
                {
                    if (string.IsNullOrWhiteSpace(path) || path.Equals("sample", StringComparison.OrdinalIgnoreCase))
                    {
                        path = Path.Combine(_paths.Root, "apps", "ws-sample.apk");
                    }

                    var approvedRoot = Path.GetFullPath(Path.Combine(_paths.Root, "apps")) + Path.DirectorySeparatorChar;
                    var full = Path.GetFullPath(path);
                    if (!full.StartsWith(approvedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SupervisorOperationException(
                            ErrorCodes.ForbiddenScope,
                            "Agent sessions may only install the approved sample APK.");
                    }

                    path = full;
                }

                return await _manager.InstallAsync(
                    name,
                    path ?? throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "path is required."),
                    cancellationToken).ConfigureAwait(false);
            }

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
            {
                var tag = JsonArgs.String(args, "tag") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "tag is required.");
                var saved = await _manager.SnapshotAsync(name, tag, save: true, cancellationToken)
                    .ConfigureAwait(false);
                _library.WriteSnapshotMetadata(_manager.Get(name), tag);
                return saved;
            }

            case OperationNames.SnapshotLoad:
                if (session is not null)
                {
                    return await _agents.RestoreAsync(session, args ?? new JsonObject(), cancellationToken)
                        .ConfigureAwait(false);
                }

                return await _manager.SnapshotAsync(
                    name,
                    JsonArgs.String(args, "tag") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "tag is required."),
                    save: false,
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.InstanceDuplicate:
            {
                var source = name;
                var target = JsonArgs.String(args, "target") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "target is required.");
                var result = await _library.DuplicateAsync(_manager.Get(source), target, cancellationToken)
                    .ConfigureAwait(false);
                _agents.InvalidateObservations(source);
                return result;
            }

            case OperationNames.InstanceReset:
            {
                var record = _manager.Get(name);
                var result = _library.Reset(record);
                _agents.InvalidateObservations(name);
                return result;
            }

            case OperationNames.InstanceProfileExport:
                return _library.ExportProfile(_manager.Get(name));

            case OperationNames.InstanceProfileImport:
            {
                var profileNode = args?["profile"] as JsonObject ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "profile is required.");
                var profile = DeviceLibraryService.ParseProfile(profileNode);
                var targetName = JsonArgs.String(args, "name") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "name is required.");
                var created = await _manager.CreateAsync(
                    targetName,
                    profile.ImagePackage,
                    profile.DeviceId,
                    null,
                    null,
                    profile.GpuMode,
                    profile.Headless,
                    cancellationToken).ConfigureAwait(false);
                var exported = _library.ExportProfile(created);
                return new JsonObject
                {
                    ["created"] = created.Name,
                    ["profile"] = exported["profile"]!.DeepClone(),
                    ["importedFrom"] = profile.LineageId,
                    ["schemaVersion"] = profile.SchemaVersion,
                };
            }

            case OperationNames.InstancePresets:
                return new JsonObject
                {
                    ["presets"] = DeviceLibraryService.Presets(),
                    ["estimate"] = _library.DiskEstimate("2048M"),
                };

            case OperationNames.InstanceDiskEstimate:
                return _library.DiskEstimate(
                    JsonArgs.String(args, "dataPartition") ?? "2048M");

            case OperationNames.SnapshotList:
                return _library.ListSnapshots(_manager.Get(name));

            case OperationNames.SnapshotDelete:
            {
                var tag = JsonArgs.String(args, "tag") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "tag is required.");
                var force = JsonArgs.Bool(args, "force", false);
                var result = _library.DeleteSnapshot(_manager.Get(name), tag, force);
                _agents.InvalidateObservations(name);
                return result;
            }

            case OperationNames.SimulationGeoFix:
            {
                var latitude = JsonArgs.Double(args, "latitude") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "latitude is required.");
                var longitude = JsonArgs.Double(args, "longitude") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "longitude is required.");
                return await _manager.GeoFixAsync(
                    name, longitude, latitude, JsonArgs.Double(args, "altitude"), cancellationToken)
                    .ConfigureAwait(false);
            }

            case OperationNames.SimulationBattery:
            {
                var level = JsonArgs.Int(args, "level") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "level is required.");
                var charging = JsonArgs.Bool(args, "charging", false);
                return await _manager.BatteryAsync(name, level, charging, cancellationToken)
                    .ConfigureAwait(false);
            }

            case OperationNames.InstanceAdbIdentity:
                return await _manager.AdbIdentityAsync(name, cancellationToken).ConfigureAwait(false);

            case OperationNames.InstanceInputHold:
            {
                var target = JsonArgs.String(args, "name") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "name is required.");
                var held = JsonArgs.Bool(args, "held", true);
                return _agents.SetInputHold(target, held);
            }

            case OperationNames.InstanceInputGate:
            {
                var target = JsonArgs.String(args, "name") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "name is required.");
                return _agents.DescribeInputHold(target);
            }

            case OperationNames.AgentPairRequest:
                return _agents.RequestPairing(
                    JsonArgs.String(args, "clientName") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "clientName is required."));

            case OperationNames.AgentPairApprove:
                return _agents.ApprovePairing(
                    JsonArgs.String(args, "pairingId") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "pairingId is required."),
                    JsonArgs.String(args, "scopes") ?? "observe,input,app,lifecycle,network,restore,cleanup,artifact",
                    JsonArgs.String(args, "template") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "template is required."));

            case OperationNames.AgentPairComplete:
                return _agents.CompletePairing(
                    JsonArgs.String(args, "pairingId") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "pairingId is required."),
                    JsonArgs.String(args, "code") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "code is required."));

            case OperationNames.AgentSessionList:
            {
                var sessions = new JsonArray();
                foreach (var item in _agents.Store.ListSessions())
                {
                    sessions.Add(new JsonObject
                    {
                        ["session"] = item.Id,
                        ["clientName"] = item.ClientName,
                        ["instance"] = item.Instance,
                        ["scopes"] = item.Scopes,
                        ["createdUtc"] = item.CreatedUtc.ToString("O"),
                        ["expiresUtc"] = item.ExpiresUtc.ToString("O"),
                        ["revoked"] = item.Revoked,
                        ["expired"] = item.ExpiresUtc < DateTimeOffset.UtcNow,
                    });
                }

                var pairings = new JsonArray();
                foreach (var item in _agents.Store.ListPairings())
                {
                    pairings.Add(new JsonObject
                    {
                        ["pairingId"] = item.Id,
                        ["clientName"] = item.ClientName,
                        ["status"] = item.Status,
                        ["template"] = item.Template,
                        ["instance"] = item.Instance,
                        ["scopes"] = item.Scopes,
                        ["expiresUtc"] = item.ExpiresUtc.ToString("O"),
                    });
                }

                var operations = new JsonArray();
                foreach (var item in _store.ListRecentAgentOperations(25))
                {
                    operations.Add(new JsonObject
                    {
                        ["id"] = item.Id,
                        ["operation"] = item.Name,
                        ["caller"] = item.CallerName,
                        ["outcome"] = item.Outcome,
                        ["code"] = item.Code,
                        ["startedUtc"] = item.StartedUtc.ToString("O"),
                        ["durationMs"] = item.DurationMs,
                    });
                }

                return new JsonObject
                {
                    ["sessions"] = sessions,
                    ["pairings"] = pairings,
                    ["recentOperations"] = operations,
                };
            }

            case OperationNames.AgentSessionRevoke:
                _agents.Store.RevokeSession(
                    JsonArgs.String(args, "session") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "session is required."));
                return new JsonObject { ["revoked"] = true };

            case OperationNames.AgentTakeover:
            {
                var target = JsonArgs.String(args, "name") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "name is required.");
                var count = _agents.Store.RevokeSessionsForInstance(target);
                _agents.BumpGeneration(target);
                _agents.RecordEvent(target, "takeover", new JsonObject { ["revokedSessions"] = count });
                return new JsonObject { ["instance"] = target, ["revokedSessions"] = count };
            }

            case OperationNames.AgentApproveAction:
                return new JsonObject
                {
                    ["code"] = _agents.ApproveAction(
                        JsonArgs.String(args, "session") ?? throw new SupervisorOperationException(
                            ErrorCodes.InvalidRequest, "session is required."),
                        JsonArgs.String(args, "operation") ?? throw new SupervisorOperationException(
                            ErrorCodes.InvalidRequest, "operation is required.")),
                    ["expiresSeconds"] = 300,
                };

            case OperationNames.DeviceCreateDisposable:
            {
                var target = session?.Instance ?? throw new SupervisorOperationException(
                    ErrorCodes.Unauthorized, "Pairing is required.");
                var record = _manager.Get(target);
                return new JsonObject
                {
                    ["instance"] = record.Name,
                    ["serial"] = record.AdbSerial,
                    ["imagePackage"] = record.ImagePackage,
                    ["state"] = record.State.ToString(),
                };
            }

            case OperationNames.ObservationScreenshot:
                return await _agents.ScreenshotAsync(
                    session ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Pairing is required."),
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.ObservationTree:
                return await _agents.UiTreeAsync(
                    session ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Pairing is required."),
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.InputText:
                return await _agents.TypeAsync(
                    session ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Pairing is required."),
                    args ?? new JsonObject(), cancellationToken).ConfigureAwait(false);

            case OperationNames.UiAssert:
                return await _agents.AssertAsync(
                    session ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Pairing is required."),
                    args ?? new JsonObject(), cancellationToken).ConfigureAwait(false);

            case OperationNames.LogsRead:
                return await _agents.LogsAsync(
                    session ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Pairing is required."),
                    args ?? new JsonObject(), cancellationToken).ConfigureAwait(false);

            case OperationNames.NetworkProfile:
                return await _agents.NetworkAsync(
                    session ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Pairing is required."),
                    args ?? new JsonObject(), cancellationToken).ConfigureAwait(false);

            case OperationNames.EventPoll:
            {
                var target = session?.Instance ?? name;
                return _agents.PollEvents(target, args?["cursor"]?.GetValue<long>() ?? 0);
            }

            case OperationNames.ArtifactRead:
                return _agents.ReadArtifact(
                    session ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Pairing is required."),
                    JsonArgs.String(args, "artifactId") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "artifactId is required."));

            case OperationNames.DeviceCleanup:
                return await _agents.CleanupAsync(
                    session ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Pairing is required."),
                    args ?? new JsonObject(), cancellationToken).ConfigureAwait(false);

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
