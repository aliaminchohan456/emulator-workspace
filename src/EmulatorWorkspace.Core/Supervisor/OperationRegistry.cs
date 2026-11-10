using System.Diagnostics;

namespace EmulatorWorkspace.Core.Supervisor;

public sealed record OperationRequest(
    string Name,
    CallerIdentity Caller,
    IReadOnlyDictionary<string, string?>? Arguments = null);

public sealed record OperationResult(bool Success, string Code, string Message, TimeSpan Duration)
{
    public static OperationResult Ok(string message, TimeSpan duration) =>
        new(true, "ok", message, duration);

    public static OperationResult Fail(string code, string message, TimeSpan duration) =>
        new(false, code, message, duration);
}

public sealed class OperationDeniedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The only entry point through which desktop, CLI, SDK, and future MCP calls
/// reach engine or guest state. Registrations declare their scope so that a
/// single policy decision covers every surface.
/// </summary>
public sealed class OperationRegistry
{
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly AuthorizationPolicy _policy;
    private readonly Journal _journal;

    public OperationRegistry(AuthorizationPolicy policy, Journal journal)
    {
        _policy = policy;
        _journal = journal;
    }

    public IReadOnlyCollection<OperationDescriptor> Descriptors =>
        _registrations.Values.Select(r => r.Descriptor).ToList();

    public void Register(
        OperationDescriptor descriptor,
        Func<OperationRequest, CancellationToken, Task<OperationResult>> handler)
    {
        if (!_registrations.TryAdd(descriptor.Name, new Registration(descriptor, handler)))
        {
            throw new InvalidOperationException($"Operation '{descriptor.Name}' is already registered.");
        }
    }

    public async Task<OperationResult> InvokeAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_registrations.TryGetValue(request.Name, out var registration))
        {
            throw new InvalidOperationException($"Unknown operation '{request.Name}'.");
        }

        var decision = _policy.Authorize(request.Caller, registration.Descriptor);
        if (!decision.Allowed)
        {
            _journal.Append("denied", request, null, null);
            throw new OperationDeniedException(decision.Code ?? "denied", decision.Reason ?? "Denied.");
        }

        _journal.Append("begin", request, null, null);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await registration.Handler(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _journal.Append("end", request, result, stopwatch.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _journal.Append(
                "cancelled",
                request,
                OperationResult.Fail("cancelled", "Operation cancelled.", stopwatch.Elapsed),
                stopwatch.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failure = OperationResult.Fail(ex.GetType().Name, ex.Message, stopwatch.Elapsed);
            _journal.Append("error", request, failure, stopwatch.Elapsed);
            return failure;
        }
    }

    private sealed record Registration(
        OperationDescriptor Descriptor,
        Func<OperationRequest, CancellationToken, Task<OperationResult>> Handler);
}

/// <summary>Operation names and scopes used across every surface.</summary>
public static class OperationCatalog
{
    public const string ScopeRead = "device.read";
    public const string ScopeControl = "device.control";
    public const string ScopeSnapshot = "device.snapshot";
    public const string ScopeAdmin = "workspace.admin";

    public static readonly OperationDescriptor Doctor =
        new("doctor", ScopeRead, Mutating: false);

    public static readonly OperationDescriptor InstanceList =
        new("instance.list", ScopeRead, Mutating: false);

    public static readonly OperationDescriptor InstanceStatus =
        new("instance.status", ScopeRead, Mutating: false);

    public static readonly OperationDescriptor InstanceCreate =
        new("instance.create", ScopeAdmin, Mutating: true);

    public static readonly OperationDescriptor InstanceStart =
        new("instance.start", ScopeControl, Mutating: true, RequiresApproval: true);

    public static readonly OperationDescriptor InstanceStop =
        new("instance.stop", ScopeControl, Mutating: true, RequiresApproval: true);

    public static readonly OperationDescriptor FrameCapture =
        new("frame.capture", ScopeRead, Mutating: false);

    public static readonly OperationDescriptor InputTap =
        new("input.tap", ScopeControl, Mutating: true);

    public static readonly OperationDescriptor SnapshotSave =
        new("snapshot.save", ScopeSnapshot, Mutating: false);

    public static readonly OperationDescriptor SnapshotLoad =
        new("snapshot.load", ScopeSnapshot, Mutating: true, RequiresApproval: true);
}
