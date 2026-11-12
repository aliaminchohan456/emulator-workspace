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
    private readonly RecipeService _recipes;
    private readonly Action _requestShutdown;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private bool _replaying;

    public OperationDispatcher(
        WorkspacePaths paths,
        SdkLocator sdk,
        SupervisorStore store,
        InstanceManager manager,
        Journal journal,
        AgentService agents,
        DeviceLibraryService library,
        RecipeService recipes,
        Action requestShutdown)
    {
        _paths = paths;
        _sdk = sdk;
        _store = store;
        _manager = manager;
        _journal = journal;
        _agents = agents;
        _library = library;
        _recipes = recipes;
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
            if (!_replaying)
            {
                var capturedInstance = session?.Instance ?? JsonArgs.String(request.Args, "name") ?? "";
                if (capturedInstance.Length > 0)
                {
                    _recipes.Capture(capturedInstance, request.Op, request.Args, data);
                }
            }

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
        OperationNames.RecipeReplay => AgentService.ScopeInput,
        OperationNames.InstanceStatus or OperationNames.ObservationScreenshot
            or OperationNames.ObservationTree or OperationNames.LogsRead
            or OperationNames.EventPoll or OperationNames.ArtifactRead or OperationNames.UiAssert
            or OperationNames.InstanceAdbIdentity or OperationNames.RecipeValidate
            or OperationNames.RecipeExport or OperationNames.LogcatExport
            or OperationNames.DiagnosticsCrash => AgentService.ScopeObserve,
        OperationNames.InputTap or OperationNames.InputText or OperationNames.InputKey
            or OperationNames.InputSwipe => AgentService.ScopeInput,
        OperationNames.AppInstall or OperationNames.AppLaunch or OperationNames.AppForeground
        or OperationNames.AppPackageInfo or OperationNames.AppPackagePermissions
        or OperationNames.AppDeepLink or OperationNames.AppInstrumentation =>
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
        or OperationNames.DeviceCreateDisposable or OperationNames.InstanceAdbIdentity
        or OperationNames.RecipeReplay or OperationNames.InstanceAdbRoot
        or OperationNames.InstanceAdbRootState or OperationNames.AppPackagePermissions
        or OperationNames.AppDeepLink or OperationNames.AppInstrumentation
        or OperationNames.LogcatExport or OperationNames.DiagnosticsCrash
        or OperationNames.DiagnosticsBugreport;

    private string? TryApprovedApkHash()
    {
        var approved = Path.Combine(_paths.Root, "apps", "ws-sample.apk");
        if (!File.Exists(approved))
        {
            return null;
        }

        using var stream = File.OpenRead(approved);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseRuntime(JsonObject? node)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null)
        {
            return values;
        }

        foreach (var property in node)
        {
            if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
            {
                values[property.Key] = text;
            }
        }

        return values;
    }

    /// <summary>
    /// Replays a recorded recipe against the caller's own guest. Every step
    /// goes through the same scope, approval and lease checks as a live
    /// operation, and the report records per-step timings, artifact ids and the
    /// baseline restore outcome.
    /// </summary>
    private async Task<JsonObject> ReplayAsync(
        JsonObject? args,
        AgentSession? session,
        IEventSink sink,
        CancellationToken cancellationToken)
    {
        var recipe = _recipes.LoadRecipe(
            JsonArgs.String(args, "recipe") ?? throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, "recipe is required."));
        var runtimeValues = ParseRuntime(args?["runtime"] as JsonObject);

        if (session is null)
        {
            throw new SupervisorOperationException(
                ErrorCodes.ForbiddenScope, "A recipe replay needs a paired agent session.");
        }

        session = _agents.EnsureActive(session);
        var record = _manager.Get(session.Instance);
        var guest = _library.GuestInfo(record);
        var validation = _recipes.Validate(recipe, guest);
        if (!validation.Valid)
        {
            throw new SupervisorOperationException(
                ErrorCodes.RecipeInvalid, string.Join("; ", validation.Errors));
        }

        var report = new JsonObject
        {
            ["recipeId"] = recipe["recipeId"]?.GetValue<string>(),
            ["schemaVersion"] = recipe["schemaVersion"]?.GetValue<int>(),
            ["instance"] = session.Instance,
            ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        // Baseline resolution: an existing compatible snapshot of this guest is
        // reused; a fresh clone gets its own baseline with the same tag.
        var baselineTag = recipe["baseline"]?["snapshotTag"]?.GetValue<string>();
        var baselineOutcome = "not_required";
        if (!string.IsNullOrEmpty(baselineTag))
        {
            var snapshots = _library.ListSnapshots(record);
            var match = snapshots["snapshots"]!.AsArray()
                .Select(node => node!.AsObject())
                .FirstOrDefault(node => node["tag"]!.GetValue<string>() == baselineTag);
            if (match is null)
            {
                await _manager.SnapshotAsync(record.Name, baselineTag, save: true, cancellationToken)
                    .ConfigureAwait(false);
                _library.WriteSnapshotMetadata(record, baselineTag);
                baselineOutcome = "created";
            }
            else if (!match["compatible"]!.GetValue<bool>())
            {
                throw new SupervisorOperationException(
                    ErrorCodes.RecipeIncompatibleBaseline,
                    $"The baseline snapshot '{baselineTag}' is not compatible with this guest: "
                    + match["incompatibleReason"]?.GetValue<string>());
            }
            else
            {
                baselineOutcome = "resolved";
            }
        }

        report["baselineOutcome"] = baselineOutcome;
        var baselineVerified = false;
        var recovery = "none";

        // A previous run that was cancelled may have left guest network
        // shaping behind. The recorded original profile is applied first so a
        // replay after an interruption starts from a known network state.
        var recordedOriginal = recipe["network"]?["original"] as JsonObject;
        if (recordedOriginal is not null)
        {
            try
            {
                await DispatchAsync(
                        OperationNames.NetworkProfile,
                        new JsonObject
                        {
                            ["name"] = session.Instance,
                            ["delay"] = recordedOriginal["delay"]?.GetValue<string>() ?? "none",
                            ["speed"] = recordedOriginal["speed"]?.GetValue<string>() ?? "full",
                            ["latencyMs"] = recordedOriginal["latencyMs"]?.GetValue<int>() ?? 0,
                        },
                        sink,
                        cancellationToken,
                        session)
                    .ConfigureAwait(false);
                report["networkRestored"] = "restored";
            }
            catch (Exception ex)
            {
                // A guest whose console is not reachable yet must not abort
                // the whole replay; the shaping is restored at the end.
                report["networkRestored"] = "failed: " + ex.Message;
            }
        }

        var steps = recipe["steps"] as JsonArray ?? new JsonArray();
        var stepReports = new JsonArray();
        var failed = false;
        var networkRestored = "not_required";
        var originalNetwork = recipe["network"] as JsonObject;
        _replaying = true;
        try
        {
            var stepIndex = -1;
            foreach (var node in steps)
            {
                stepIndex++;
                if (node is not JsonObject step)
                {
                    continue;
                }

                var stepOp = step["op"]?.GetValue<string>() ?? "";
                var stepArgs = step["args"] as JsonObject ?? new JsonObject();
                var started = DateTimeOffset.UtcNow;
                var stepReport = new JsonObject
                {
                    ["index"] = step["index"]?.DeepClone(),
                    ["op"] = stepOp,
                    ["startedUtc"] = started.ToString("O"),
                };

                if (failed)
                {
                    stepReport["result"] = "skipped";
                    stepReports.Add(stepReport);
                    continue;
                }

                try
                {
                    if (stepOp == "snapshot.save")
                    {
                        // A recorded snapshot save defines the baseline. The
                        // replay resolves or creates that snapshot on this
                        // guest, restores it and reports the recovery mode
                        // instead of re-saving over the baseline.
                        var tag = stepArgs["tag"]?.GetValue<string>() ?? baselineTag;
                        if (string.IsNullOrEmpty(tag))
                        {
                            throw new SupervisorOperationException(
                                ErrorCodes.RecipeInvalid, "The baseline snapshot step has no tag.");
                        }

                        var snapshots = _library.ListSnapshots(record);
                        var existing = snapshots["snapshots"]!.AsArray()
                            .Select(node => node!.AsObject())
                            .FirstOrDefault(node => node["tag"]!.GetValue<string>() == tag);
                        if (existing is null)
                        {
                            await _manager.SnapshotAsync(record.Name, tag, save: true, cancellationToken)
                                .ConfigureAwait(false);
                            _library.WriteSnapshotMetadata(record, tag);
                            baselineOutcome = "created";
                        }
                        else if (!existing["compatible"]!.GetValue<bool>())
                        {
                            throw new SupervisorOperationException(
                                ErrorCodes.RecipeIncompatibleBaseline,
                                $"The baseline snapshot '{tag}' is not compatible with this guest.");
                        }

                        var restoreArgs = new JsonObject { ["tag"] = tag };
                        var approval = JsonArgs.String(args, "approval");
                        if (!string.IsNullOrEmpty(approval))
                        {
                            restoreArgs["approval"] = approval;
                        }

                        var restored = await DispatchAsync(
                                OperationNames.SnapshotLoad, restoreArgs, sink, cancellationToken, session)
                            .ConfigureAwait(false) as JsonObject;
                        recovery = restored?["recovery"]?.GetValue<string>() ?? "unknown";

                        // A restored guest may come back without the fixture in
                        // the foreground, so the recorded application is
                        // relaunched and the hierarchy is awaited again.
                        // A restored guest reboots: the fixture is relaunched
                        // and the hierarchy is awaited, both retried until the
                        // guest is back or the bound is exhausted.
                        var package = recipe["app"]?["package"]?.GetValue<string>();

                        // A baseline that is followed by install/launch steps
                        // carries those steps in the recipe, so the guest only has
                        // to be interactive again. Otherwise the fixture belonged
                        // to the recorded state and must be brought back here.
                        // A baseline that is followed by install/launch steps
                        // carries those steps in the recipe, so the guest only has
                        // to be interactive again. Otherwise the fixture belonged
                        // to the recorded state and must be brought back here.
                        var relaunchNeeded = RecipeService.NeedsFixtureRelaunch(steps, stepIndex);
                        var uiReady = !relaunchNeeded;
                        if (relaunchNeeded)
                        {
                            // A restore can revert the guest's userdata, so the
                            // fixture recorded before the baseline may no longer
                            // be installed. The recipe's own install step is
                            // re-executed once, honouring the recorded order.
                            var installStep = steps
                                .Take(stepIndex)
                                .OfType<JsonObject>()
                                .LastOrDefault(step => step["op"]?.GetValue<string>() == "app.install");
                            if (installStep is not null && !string.IsNullOrEmpty(package))
                            {
                                // The package manager of a guest that just
                                // rebooted is not ready; the install waits for
                                // it instead of blocking on the device.
                                var packageManagerReady = false;
                                var alreadyInstalled = false;
                                for (var probe = 0; probe < 20 && !packageManagerReady; probe++)
                                {
                                    try
                                    {
                                        var info = await DispatchAsync(
                                                OperationNames.AppPackageInfo,
                                                new JsonObject { ["name"] = session.Instance, ["package"] = package },
                                                sink,
                                                cancellationToken,
                                                session)
                                            .ConfigureAwait(false) as JsonObject;
                                        packageManagerReady = true;
                                        alreadyInstalled = info?["installed"] is { } flag && flag.GetValue<bool>();
                                    }
                                    catch (Exception)
                                    {
                                        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                }

                                stepReport["packageManagerReady"] = packageManagerReady;
                                stepReport["baselineAppAlreadyInstalled"] = alreadyInstalled;

                                // The install is only repeated when the restore
                                // actually removed the package. Reinstalling a
                                // package that is present blocks the ADB client
                                // on a guest that is still settling.
                                if (packageManagerReady && !alreadyInstalled)
                                {
                                    var installArgs = RecipeService.ResolveRuntime(
                                        installStep["args"] as JsonObject ?? new JsonObject(), runtimeValues);
                                    installArgs["name"] = session.Instance;
                                    try
                                    {
                                        await DispatchAsync(
                                                OperationNames.AppInstall,
                                                installArgs,
                                                sink,
                                                cancellationToken,
                                                session)
                                            .ConfigureAwait(false);
                                        stepReport["baselineAppReinstalled"] = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        stepReport["baselineAppReinstallError"] = ex.Message;
                                    }
                                }
                            }

                            // The restored guest is relaunched until the fixture
                            // is the focused window and a fresh hierarchy
                            // contains its controls. The whole wait has one
                            // budget so a replay never exceeds the deadline.
                            uiReady = await WaitForGuestUiAsync(
                                    session,
                                    sink,
                                    cancellationToken,
                                    package,
                                    attempts: 20,
                                    relaunch: () => DispatchAsync(
                                        OperationNames.AppLaunch,
                                        new JsonObject
                                        {
                                            ["name"] = session.Instance,
                                            ["package"] = package,
                                            ["focusWaitSeconds"] = 20,
                                        },
                                        sink,
                                        cancellationToken,
                                        session))
                                .ConfigureAwait(false);
                        }

                        stepReport["baselineAppRelaunched"] = package;
                        stepReport["guestUiReady"] = uiReady;

                        // Baseline verification: a recovered guest proceeds only
                        // when the recipe's required state is independently
                        // confirmed (the fixture package is focused and a fresh
                        // hierarchy contains its stable marker). Otherwise the
                        // recipe stops with a typed result.
                        if (!uiReady)
                        {
                            stepReport["result"] = "baseline_not_verified";
                            stepReport["code"] = ErrorCodes.RecipeBaselineNotVerified;
                            stepReport["message"] =
                                "The restored guest did not expose the recipe's fixture within the bounded wait.";
                            stepReport["durationMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                            stepReports.Add(stepReport);
                            failed = true;
                            report["steps"] = stepReports;
                            report["failed"] = failed;
                            report["baselineVerified"] = false;
                            report["recovery"] = recovery;
                            report["networkRestored"] = "not_required";
                            report["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                            return report;
                        }

                        baselineVerified = await VerifyBaselineStateAsync(
                                session,
                                sink,
                                recipe,
                                relaunchNeeded ? package : null,
                                cancellationToken)
                            .ConfigureAwait(false);
                        stepReport["baselineVerified"] = baselineVerified;

                        // The generic marker is not enough: the fixture's first
                        // targeted control must resolve before any app-targeted
                        // step runs, otherwise the step races the activity.
                        var readinessSelector = RecipeService.FirstAppSelector(steps, stepIndex);
                        if (baselineVerified && readinessSelector is not null)
                        {
                            var ready = false;
                            for (var attempt = 0; attempt < 30 && !ready; attempt++)
                            {
                                var probe = await DispatchAsync(
                                        OperationNames.UiAssert,
                                        new JsonObject { ["name"] = session.Instance, ["selector"] = readinessSelector },
                                        sink,
                                        cancellationToken,
                                        session)
                                    .ConfigureAwait(false) as JsonObject;
                                ready = probe?["pass"]?.GetValue<bool>() ?? false;
                                if (!ready)
                                {
                                    if (attempt % 8 == 7 && !string.IsNullOrEmpty(package))
                                    {
                                        try
                                        {
                                            await DispatchAsync(
                                                    OperationNames.AppLaunch,
                                                    new JsonObject { ["name"] = session.Instance, ["package"] = package },
                                                    sink,
                                                    cancellationToken,
                                                    session)
                                                .ConfigureAwait(false);
                                        }
                                        catch (SupervisorOperationException)
                                        {
                                        }
                                    }

                                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                                }
                            }

                            stepReport["fixtureReady"] = ready;
                            if (!ready)
                            {
                                stepReport["result"] = "baseline_not_verified";
                                stepReport["code"] = ErrorCodes.RecipeBaselineNotVerified;
                                stepReport["message"] =
                                    "The restored guest did not expose the recipe's first targeted control in time.";
                                stepReport["durationMs"] =
                                    (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                                stepReports.Add(stepReport);
                                failed = true;
                                report["steps"] = stepReports;
                                report["failed"] = failed;
                                report["baselineVerified"] = false;
                                report["recovery"] = recovery;
                                report["networkRestored"] = "not_required";
                                report["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                                return report;
                            }
                        }
                        if (!baselineVerified)
                        {
                            stepReport["result"] = "baseline_not_verified";
                            stepReport["code"] = ErrorCodes.RecipeBaselineNotVerified;
                            stepReport["message"] =
                                "The restored guest did not reach the recipe's baseline state "
                                + "(fixture focused with a stable hierarchy marker).";
                            stepReport["durationMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                            stepReports.Add(stepReport);
                            failed = true;
                            report["steps"] = stepReports;
                            report["failed"] = failed;
                            report["baselineVerified"] = false;
                            report["recovery"] = recovery;
                            report["networkRestored"] = "not_required";
                            report["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                            return report;
                        }
                        stepReport["result"] = "baseline_restored";
                        stepReport["baselineOutcome"] = baselineOutcome;
                        stepReport["recovery"] = recovery;
                        stepReport["durationMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                        stepReports.Add(stepReport);
                        continue;
                    }

                    var resolved = RecipeService.ResolveRuntime(stepArgs, runtimeValues);
                    if (IsInstanceBound(stepOp))
                    {
                        // Every step runs against the guest the session is bound
                        // to; a recipe can never address another instance.
                        resolved["name"] = session.Instance;
                    }

                    if (stepArgs["needsObservation"] is JsonValue needs && needs.GetValue<bool>())
                    {
                        var observation = await DispatchAsync(
                                OperationNames.ObservationScreenshot,
                                new JsonObject { ["name"] = session.Instance },
                                sink,
                                cancellationToken,
                                session)
                            .ConfigureAwait(false) as JsonObject;
                        var frameId = observation?["frameId"]?.GetValue<string>()
                                      ?? throw new SupervisorOperationException(
                                          ErrorCodes.StaleObservation, "A fresh observation was not available.");
                        resolved["frameId"] = frameId;
                    }

                    session = _agents.EnsureActive(session);
                    AuthorizeAgent(session, stepOp, resolved);
                    var data = await DispatchAsync(stepOp, resolved, sink, cancellationToken, session)
                        .ConfigureAwait(false);
                    var expectation = step["expect"] as JsonObject;
                    var expectationOk = await EvaluateExpectationAsync(expectation, data, session, sink, cancellationToken)
                        .ConfigureAwait(false);
                    stepReport["result"] = expectationOk ? "ok" : "expectation_failed";
                    if (expectationOk && stepOp == "ui.assert" && !baselineVerified)
                    {
                        baselineVerified = true;
                    }
                    stepReport["artifactId"] = data?["artifactId"]?.DeepClone();
                    stepReport["durationMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                    if (!expectationOk)
                    {
                        failed = true;
                    }
                }
                catch (SupervisorOperationException ex)
                {
                    stepReport["result"] = "failed";
                    stepReport["code"] = ex.Code;
                    stepReport["message"] = ex.Message;
                    stepReport["durationMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                    failed = true;
                }
                catch (Exception ex)
                {
                    // A transport hiccup fails the step with a typed outcome
                    // instead of aborting the replay without a report.
                    stepReport["result"] = "failed";
                    stepReport["code"] = ErrorCodes.Internal;
                    stepReport["message"] = ex.Message;
                    stepReport["durationMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                    failed = true;
                }

                stepReports.Add(stepReport);
            }
        }
        finally
        {
            _replaying = false;
        }

        // The recorded network profile is restored so a replay never leaves
        // shaping behind on the guest.
        if (originalNetwork?["original"]?["latencyMs"]?.GetValue<int>() is > 0)
        {
            try
            {
                await DispatchAsync(
                        OperationNames.NetworkProfile,
                        new JsonObject { ["delay"] = "none", ["speed"] = "full", ["latencyMs"] = 0 },
                        sink,
                        cancellationToken,
                        session)
                    .ConfigureAwait(false);
                networkRestored = "restored";
            }
            catch (Exception ex)
            {
                // The restore of the recorded profile is best effort: a guest
                // whose console is gone must not lose the replay report.
                networkRestored = "failed: " + ex.Message;
            }
        }

        report["steps"] = stepReports;
        report["failed"] = failed;
        report["baselineVerified"] = baselineVerified;
        report["recovery"] = recovery;
        report["networkRestored"] = networkRestored;
        report["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O");
        return report;
    }


    /// <summary>
    /// Waits until the guest exposes an interactive hierarchy again after a
    /// restore. A restored guest can be back at the launcher for a moment, and
    /// a step that targets an application control must not race that window.
    /// </summary>
    private async Task<bool> WaitForGuestUiAsync(
        AgentSession session,
        IEventSink sink,
        CancellationToken cancellationToken,
        string? package = null,
        int attempts = 30,
        Func<Task>? relaunch = null)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (!string.IsNullOrEmpty(package))
            {
                var foreground = await DispatchAsync(
                        OperationNames.AppForeground,
                        new JsonObject { ["name"] = session.Instance },
                        sink,
                        cancellationToken,
                        session)
                    .ConfigureAwait(false) as JsonObject;
                var window = foreground?["foreground"]?.GetValue<string>() ?? "";
                if (!window.Contains(package, StringComparison.Ordinal))
                {
                    // A guest that came back from a restore can lose the
                    // foreground again, so the application is relaunched
                    // periodically while the wait continues.
                    if (relaunch is not null && attempt % 10 == 9)
                    {
                        try
                        {
                            await relaunch().ConfigureAwait(false);
                        }
                        catch (SupervisorOperationException)
                        {
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            var tree = await DispatchAsync(
                    OperationNames.ObservationTree,
                    new JsonObject { ["name"] = session.Instance },
                    sink,
                    cancellationToken,
                    session)
                .ConfigureAwait(false) as JsonObject;
            var nodes = tree?["nodes"] as JsonArray;
            if (nodes is not null && nodes.Any(node =>
                    node?["contentDescription"]?.GetValue<string>() is { Length: > 0 }))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }


    /// <summary>
    /// Confirms that the fixture package is focused and that a fresh hierarchy
    /// contains one of the recipe's stable markers. The hierarchy must come from
    /// a fresh observation taken after the restore.
    /// </summary>
    private async Task<bool> VerifyBaselineStateAsync(
        AgentSession session,
        IEventSink sink,
        JsonObject recipe,
        string? package,
        CancellationToken cancellationToken)
    {
        // When the recipe installs or launches the fixture after the baseline,
        // the required state at this point is an interactive guest; the fixture
        // is verified by its own recorded steps.
        // Bounded by the caller's deadline: the guest may still be settling
        // after the restore, so the required state is polled rather than
        // asserted once.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!string.IsNullOrEmpty(package))
            {
                var foreground = await DispatchAsync(
                        OperationNames.AppForeground,
                        new JsonObject { ["name"] = session.Instance },
                        sink,
                        cancellationToken,
                        session)
                    .ConfigureAwait(false) as JsonObject;
                var window = foreground?["foreground"]?.GetValue<string>() ?? "";
                if (!window.Contains(package, StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            var tree = await DispatchAsync(
                    OperationNames.ObservationTree,
                    new JsonObject { ["name"] = session.Instance },
                    sink,
                    cancellationToken,
                    session)
                .ConfigureAwait(false) as JsonObject;
            var nodes = tree?["nodes"] as JsonArray;
            if (nodes is not null && nodes.Count > 0)
            {
                foreach (var node in nodes)
                {
                    var description = node?["contentDescription"]?.GetValue<string>() ?? "";
                    var text = node?["text"]?.GetValue<string>() ?? "";
                    if (description.Length > 0 || text.Length > 0)
                    {
                        return true;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> EvaluateExpectationAsync(
        JsonObject? expectation,
        JsonNode? data,
        AgentSession session,
        IEventSink sink,
        CancellationToken cancellationToken)
    {
        if (expectation is null)
        {
            return true;
        }

        var type = expectation["type"]?.GetValue<string>() ?? "";
        if (type == "selectorVisible")
        {
            return data?["pass"]?.GetValue<bool>() ?? false;
        }

        if (type == "packageForeground")
        {
            var package = expectation["package"]?.GetValue<string>();
            if (string.IsNullOrEmpty(package))
            {
                return true;
            }

            // The launch operation already waited for the resumed state.
            if (data?["focusedMatches"] is { } matched && matched.GetValue<bool>())
            {
                return true;
            }

            // The launch is asynchronous, so the foreground window is polled for
            // a bounded time before the expectation fails, relaunching once when
            // the guest is still settling after a restore.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var foreground = await DispatchAsync(
                        OperationNames.AppForeground,
                        new JsonObject { ["name"] = session.Instance },
                        sink,
                        cancellationToken,
                        session)
                    .ConfigureAwait(false) as JsonObject;
                var window = foreground?["foreground"]?.GetValue<string>() ?? "";
                var reported = foreground?["package"]?.GetValue<string>();
                if (reported == package
                    || window.Contains(package, StringComparison.Ordinal))
                {
                    return true;
                }

                if (attempt == 10)
                {
                    // One explicit relaunch after the guest had time to settle.
                    try
                    {
                        await DispatchAsync(
                                OperationNames.AppLaunch,
                                new JsonObject { ["name"] = session.Instance, ["package"] = package },
                                sink,
                                cancellationToken,
                                session)
                            .ConfigureAwait(false);
                    }
                    catch (SupervisorOperationException)
                    {
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        return true;
    }

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

            case OperationNames.AppBuildApkSet:
                return await BundleApkSetBuilder.BuildAsync(
                    JsonArgs.String(args, "path") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "An app bundle path is required."),
                    JsonArgs.String(args, "bundletool"), _paths.EvidenceDirectory,
                    JsonArgs.Bool(args, "testSigning"), cancellationToken).ConfigureAwait(false);

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
                    cancellationToken,
                    inspectManifest: session is null && JsonArgs.Bool(args, "inspectManifest")).ConfigureAwait(false);
            }

            case OperationNames.AppPackageInfo:
            {
                var package = JsonArgs.String(args, "package") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "package is required.");
                return await _manager.PackageInfoAsync(name, package, cancellationToken)
                    .ConfigureAwait(false);
            }

            case OperationNames.AppLaunch:
            {
                var focusWait = Math.Clamp(JsonArgs.Int(args, "focusWaitSeconds") ?? 20, 1, 180);
                return await _manager.LaunchAsync(
                    name,
                    JsonArgs.String(args, "package") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "package is required."),
                    cancellationToken,
                    focusWait).ConfigureAwait(false);
            }

            case OperationNames.AppForeground:
                return await _manager.ForegroundStatusAsync(name, cancellationToken).ConfigureAwait(false);

            case OperationNames.AppPackagePermissions:
            {
                var package = JsonArgs.String(args, "package") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "package is required.");
                return await _manager.PackagePermissionsAsync(name, package, cancellationToken)
                    .ConfigureAwait(false);
            }

            case OperationNames.AppDeepLink:
            {
                var uri = JsonArgs.String(args, "uri") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "uri is required.");
                return await _manager.DeepLinkAsync(name, uri, cancellationToken).ConfigureAwait(false);
            }

            case OperationNames.AppInstrumentation:
            {
                var package = JsonArgs.String(args, "package") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "package is required.");
                return await _manager.InstrumentationAsync(
                    name,
                    package,
                    JsonArgs.String(args, "runner"),
                    cancellationToken).ConfigureAwait(false);
            }

            case OperationNames.SimulationProbe:
                return await _manager.SimulationProbeAsync(name, cancellationToken).ConfigureAwait(false);

            case OperationNames.LogcatExport:
            {
                var lines = JsonArgs.Int(args, "lines") ?? 500;
                return await _manager.LogcatExportAsync(
                    name,
                    JsonArgs.String(args, "tag"),
                    Math.Clamp(lines, 1, 20000),
                    cancellationToken).ConfigureAwait(false);
            }

            case OperationNames.DiagnosticsCrash:
                return await _manager.CrashReportAsync(
                    name,
                    JsonArgs.String(args, "package"),
                    cancellationToken).ConfigureAwait(false);

            case OperationNames.DiagnosticsBugreport:
                return await _manager.BugReportAsync(name, cancellationToken).ConfigureAwait(false);

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

            case OperationNames.InstanceImportAvd:
            {
                var source = JsonArgs.String(args, "source") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "source is required.");
                var targetName = JsonArgs.String(args, "target") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "target is required.");
                if (JsonArgs.Bool(args, "preview", true))
                {
                    return _library.ValidateAvdImport(source, targetName);
                }

                return await _library.ImportAvdAsync(source, targetName, cancellationToken)
                    .ConfigureAwait(false);
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

            case OperationNames.RecipeRecordStart:
            {
                var target = JsonArgs.String(args, "name") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "name is required.");
                var record = _manager.Get(target);
                var recording = _recipes.StartRecording(
                    JsonArgs.String(args, "recipe") ?? "recipe",
                    target,
                    JsonArgs.String(args, "template") ?? target,
                    _library.GuestInfo(record));
                return new JsonObject
                {
                    ["recipeId"] = recording.Id,
                    ["instance"] = recording.Instance,
                    ["guest"] = recording.Guest.DeepClone(),
                    ["startedUtc"] = recording.StartedUtc.ToString("O"),
                };
            }

            case OperationNames.RecipeRecordRuntime:
            {
                var recordingId = JsonArgs.String(args, "recipeId") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "recipeId is required.");
                _recipes.RegisterRuntimeValue(
                    recordingId,
                    JsonArgs.String(args, "runtime") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "runtime is required."),
                    JsonArgs.String(args, "value") ?? "");
                return new JsonObject { ["recipeId"] = recordingId, ["registered"] = true };
            }

            case OperationNames.RecipeRecordStop:
            {
                var recordingId = JsonArgs.String(args, "recipeId") ?? throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, "recipeId is required.");
                var apkHash = TryApprovedApkHash();
                return _recipes.StopRecording(recordingId, apkHash);
            }

            case OperationNames.RecipeExport:
                return _recipes.ExportRecipe(
                    JsonArgs.String(args, "recipeId") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "recipeId is required."));

            case OperationNames.RecipeValidate:
            {
                var recipe = _recipes.LoadRecipe(
                    JsonArgs.String(args, "recipe") ?? throw new SupervisorOperationException(
                        ErrorCodes.InvalidRequest, "recipe is required."));
                var target = JsonArgs.String(args, "name");
                JsonObject? guest = null;
                if (!string.IsNullOrEmpty(target))
                {
                    guest = _library.GuestInfo(_manager.Get(target));
                }

                var validation = _recipes.Validate(recipe, guest);
                if (!validation.Valid)
                {
                    throw new SupervisorOperationException(
                        ErrorCodes.RecipeInvalid, string.Join("; ", validation.Errors));
                }

                return validation.Report;
            }

            case OperationNames.RecipeReplay:
                return await ReplayAsync(args, session, sink, cancellationToken)
                    .ConfigureAwait(false);

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

            case OperationNames.InstanceAdbRoot:
            {
                var root = JsonArgs.Bool(args, "root", true);
                return await _manager.SetAdbRootAsync(name, root, cancellationToken).ConfigureAwait(false);
            }

            case OperationNames.InstanceAdbRootState:
                return await _manager.AdbRootStateAsync(name, cancellationToken).ConfigureAwait(false);

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
