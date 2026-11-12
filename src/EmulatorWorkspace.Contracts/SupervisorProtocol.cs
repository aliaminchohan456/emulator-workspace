using System.Text.Json.Nodes;

namespace EmulatorWorkspace.Contracts;

/// <summary>
/// Versioned request/response/event envelope for the local supervisor pipe.
/// The contract is deliberately schema-first: every payload is a JSON object
/// and every failure carries a stable code so clients never parse messages.
/// </summary>
public static class SupervisorProtocol
{
    public const int Version = 1;

    public const int MaxRequestBytes = 1024 * 1024;
    public const int MaxResponseBytes = 4 * 1024 * 1024;
}

public sealed class RequestEnvelope
{
    public string Type { get; set; } = "request";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int Protocol { get; set; } = SupervisorProtocol.Version;

    public string Op { get; set; } = "";

    public JsonObject? Args { get; set; }

    public int? DeadlineMs { get; set; }

    /// <summary>Opaque session credential. Never logged or echoed in responses.</summary>
    public string? Auth { get; set; }

    /// <summary>Bounded idempotency key scoped to the caller and operation.</summary>
    public string? IdempotencyKey { get; set; }
}

public sealed class ResponseEnvelope
{
    public string Type { get; set; } = "response";

    public string Id { get; set; } = "";

    public bool Ok { get; set; }

    public string Code { get; set; } = "ok";

    public string Message { get; set; } = "";

    public JsonNode? Data { get; set; }

    public long DurationMs { get; set; }
}

public sealed class EventEnvelope
{
    public string Type { get; set; } = "event";

    public string Event { get; set; } = "";

    public string Subscription { get; set; } = "";

    public JsonNode? Data { get; set; }
}

public sealed class CancelEnvelope
{
    public string Type { get; set; } = "cancel";

    public string Id { get; set; } = "";
}

public static class ErrorCodes
{
    public const string Ok = "ok";
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedProtocol = "unsupported_protocol";
    public const string UnknownOperation = "unknown_operation";
    public const string Denied = "denied";
    public const string NotFound = "instance_not_found";
    public const string AlreadyRunning = "instance_already_running";
    public const string NotRunning = "instance_not_running";
    public const string PortConflict = "port_conflict";
    public const string LowDisk = "low_disk";
    public const string ImageMissing = "image_missing";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
    public const string SnapshotFailed = "snapshot_failed";
    public const string InstallFailed = "install_failed";
    public const string AppLaunchFailed = "app_launch_failed";
    public const string AdbFailure = "adb_failure";
    public const string ControllerFailure = "controller_failure";
    public const string SupervisorUnavailable = "supervisor_unavailable";
    public const string Internal = "internal_error";
    public const string Unauthorized = "unauthorized";
    public const string ForbiddenScope = "forbidden_scope";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
    public const string WrongInstance = "wrong_instance";
    public const string StaleObservation = "stale_observation";
    public const string AmbiguousSelector = "ambiguous_selector";
    public const string SelectorNotFound = "selector_not_found";
    public const string SecureWindow = "secure_window";
    public const string ApprovalRequired = "approval_required";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string UnknownOutcome = "unknown_outcome";
    public const string CapabilityUnavailable = "capability_unavailable";
    public const string InstanceUnavailable = "instance_unavailable";
    public const string RecipeInvalid = "recipe_invalid";
    public const string RecipeUnsupportedSchema = "recipe_unsupported_schema";
    public const string RecipeUnsupportedOperation = "recipe_unsupported_operation";
    public const string RecipeIncompatibleBaseline = "recipe_incompatible_baseline";
    public const string RecipeMissingRuntimeValue = "recipe_missing_runtime_value";
    public const string RecipeBaselineNotVerified = "recipe_baseline_not_verified";
}

public static class OperationNames
{
    public const string SupervisorStatus = "supervisor.status";
    public const string SupervisorShutdown = "supervisor.shutdown";
    public const string Doctor = "doctor";

    public const string InstanceList = "instance.list";
    public const string InstanceCreate = "instance.create";
    public const string InstanceStart = "instance.start";
    public const string InstanceStop = "instance.stop";
    public const string InstanceRestart = "instance.restart";
    public const string InstanceColdBoot = "instance.coldboot";
    public const string InstanceStatus = "instance.status";
    public const string InstanceWait = "instance.wait";
    public const string InstanceDelete = "instance.delete";
    public const string InstanceAdbIdentity = "instance.adbIdentity";
    public const string InstanceAdbRoot = "instance.adbRoot";
    public const string InstanceAdbRootState = "instance.adbRootState";
    public const string InstanceDuplicate = "instance.duplicate";
    public const string InstanceReset = "instance.reset";
    public const string InstanceProfileExport = "instance.profileExport";
    public const string InstanceImportAvd = "instance.importAvd";
    public const string InstanceProfileImport = "instance.profileImport";
    public const string InstancePresets = "instance.presets";
    public const string InstanceDiskEstimate = "instance.diskEstimate";
    public const string RecipeRecordStart = "recipe.recordStart";
    public const string RecipeRecordRuntime = "recipe.recordRuntime";
    public const string RecipeRecordStop = "recipe.recordStop";
    public const string RecipeExport = "recipe.export";
    public const string RecipeValidate = "recipe.validate";
    public const string RecipeReplay = "recipe.replay";
    public const string InstanceInputHold = "instance.inputHold";
    public const string InstanceInputGate = "instance.inputGate";

    public const string FrameSubscribe = "frame.subscribe";
    public const string FrameUnsubscribe = "frame.unsubscribe";
    public const string FrameSnapshot = "frame.snapshot";
    public const string FrameTrace = "frame.trace";

    public const string InputTap = "input.tap";
    public const string InputSwipe = "input.swipe";
    public const string InputKey = "input.key";
    public const string DisplayRotate = "display.rotate";
    public const string SimulationGeoFix = "sim.geoFix";
    public const string SimulationBattery = "sim.battery";

    public const string AppInstall = "app.install";
    public const string AppBuildApkSet = "app.buildApkSet";
    public const string AppInstallApkSet = "app.installApkSet";
    public const string AppLaunch = "app.launch";
    public const string AppPackageInfo = "app.packageInfo";
    public const string AppForeground = "app.foreground";
    public const string AppPackagePermissions = "app.packagePermissions";
    public const string AppDeepLink = "app.deepLink";
    public const string AppInstrumentation = "app.instrumentation";
    public const string LogcatExport = "logcat.export";
    public const string SimulationProbe = "simulation.probe";
    public const string DiagnosticsCrash = "diagnostics.crash";
    public const string DiagnosticsBugreport = "diagnostics.bugreport";

    public const string LogcatSubscribe = "logcat.subscribe";
    public const string LogcatUnsubscribe = "logcat.unsubscribe";

    public const string SnapshotSave = "snapshot.save";
    public const string SnapshotLoad = "snapshot.load";
    public const string SnapshotList = "snapshot.list";
    public const string SnapshotDelete = "snapshot.delete";

    public const string DiagnosticsSummary = "diagnostics.summary";

    public const string AgentPairRequest = "agent.pair.request";
    public const string AgentPairApprove = "agent.pair.approve";
    public const string AgentPairComplete = "agent.pair.complete";
    public const string AgentSessionList = "agent.session.list";
    public const string AgentSessionRevoke = "agent.session.revoke";
    public const string AgentTakeover = "agent.takeover";
    public const string AgentApproveAction = "agent.approveAction";

    public const string DeviceCreateDisposable = "device.createDisposable";
    public const string DeviceCleanup = "device.cleanup";
    public const string ObservationScreenshot = "observation.screenshot";
    public const string ObservationTree = "observation.tree";
    public const string InputText = "input.text";
    public const string UiAssert = "ui.assert";
    public const string LogsRead = "logs.read";
    public const string EventPoll = "event.poll";
    public const string NetworkProfile = "network.profile";
    public const string ArtifactRead = "artifact.read";
}

public static class EventNames
{
    public const string Frame = "frame";
    public const string Logcat = "logcat";
    public const string State = "state";
    public const string Progress = "progress";
    public const string SubscriptionClosed = "subscription.closed";
}
