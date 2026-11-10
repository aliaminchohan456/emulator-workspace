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
    public const string AdbFailure = "adb_failure";
    public const string ControllerFailure = "controller_failure";
    public const string SupervisorUnavailable = "supervisor_unavailable";
    public const string Internal = "internal_error";
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

    public const string FrameSubscribe = "frame.subscribe";
    public const string FrameUnsubscribe = "frame.unsubscribe";
    public const string FrameSnapshot = "frame.snapshot";
    public const string FrameTrace = "frame.trace";

    public const string InputTap = "input.tap";
    public const string InputSwipe = "input.swipe";
    public const string InputKey = "input.key";
    public const string DisplayRotate = "display.rotate";

    public const string AppInstall = "app.install";
    public const string AppLaunch = "app.launch";
    public const string AppForeground = "app.foreground";

    public const string LogcatSubscribe = "logcat.subscribe";
    public const string LogcatUnsubscribe = "logcat.unsubscribe";

    public const string SnapshotSave = "snapshot.save";
    public const string SnapshotLoad = "snapshot.load";

    public const string DiagnosticsSummary = "diagnostics.summary";
}

public static class EventNames
{
    public const string Frame = "frame";
    public const string Logcat = "logcat";
    public const string State = "state";
    public const string Progress = "progress";
    public const string SubscriptionClosed = "subscription.closed";
}
