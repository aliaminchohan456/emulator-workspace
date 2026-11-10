namespace EmulatorWorkspace.Core.Supervisor;

public enum CallerKind
{
    Human,
    Agent,
}

public sealed record CallerIdentity(CallerKind Kind, string Name)
{
    public static CallerIdentity LocalOperator() =>
        new(CallerKind.Human, Environment.UserDomainName + "\\" + Environment.UserName);

    public static CallerIdentity Agent(string name) => new(CallerKind.Agent, name);
}

public sealed record OperationDescriptor(
    string Name,
    string Scope,
    bool Mutating,
    bool RequiresApproval = false);

/// <summary>
/// Single authorization choke point shared by the desktop, CLI, SDK, and (later)
/// MCP surfaces. Agent access is off by default; a human operator is the only
/// caller allowed to perform mutating operations until pairing exists.
/// </summary>
public sealed class AuthorizationPolicy
{
    public bool AgentAccessEnabled { get; init; }

    public IReadOnlySet<string> GrantedScopes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public PolicyDecision Authorize(CallerIdentity caller, OperationDescriptor operation)
    {
        if (caller.Kind == CallerKind.Human)
        {
            return PolicyDecision.Permit();
        }

        if (!AgentAccessEnabled)
        {
            return PolicyDecision.Deny(
                "agent_access_disabled",
                "Agent access is disabled. Pair a client from the desktop to create a scoped grant.");
        }

        if (!GrantedScopes.Contains(operation.Scope))
        {
            return PolicyDecision.Deny(
                "scope_not_granted",
                $"The active grant does not include scope '{operation.Scope}'.");
        }

        if (operation.Mutating && operation.RequiresApproval)
        {
            return PolicyDecision.Deny(
                "approval_required",
                $"Operation '{operation.Name}' requires single-use human approval, which no active grant carries.");
        }

        return PolicyDecision.Permit();
    }
}

public sealed record PolicyDecision(bool Allowed, string? Code, string? Reason)
{
    public static PolicyDecision Permit() => new(true, null, null);

    public static PolicyDecision Deny(string code, string reason) => new(false, code, reason);
}
