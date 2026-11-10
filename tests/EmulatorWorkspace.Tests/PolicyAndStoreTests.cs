using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
using Xunit;

namespace EmulatorWorkspace.Tests;

public sealed class AuthorizationPolicyTests
{
    private readonly AuthorizationPolicy _policy = new();

    [Fact]
    public void Human_operator_is_allowed_for_mutating_operations()
    {
        var decision = _policy.Authorize(CallerIdentity.LocalOperator(), OperationCatalog.InstanceStart);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Agent_access_is_disabled_by_default()
    {
        var decision = _policy.Authorize(CallerIdentity.Agent("mcp"), OperationCatalog.InstanceStart);
        Assert.False(decision.Allowed);
        Assert.Equal("agent_access_disabled", decision.Code);
    }

    [Fact]
    public void Agent_with_scope_still_cannot_perform_approval_required_operation()
    {
        var policy = new AuthorizationPolicy
        {
            AgentAccessEnabled = true,
            GrantedScopes = new HashSet<string> { OperationCatalog.ScopeControl },
        };

        var decision = policy.Authorize(CallerIdentity.Agent("mcp"), OperationCatalog.InstanceStart);
        Assert.False(decision.Allowed);
        Assert.Equal("approval_required", decision.Code);
    }

    [Fact]
    public void Agent_with_scope_can_read_when_access_is_enabled()
    {
        var policy = new AuthorizationPolicy
        {
            AgentAccessEnabled = true,
            GrantedScopes = new HashSet<string> { OperationCatalog.ScopeRead },
        };

        var decision = policy.Authorize(CallerIdentity.Agent("mcp"), OperationCatalog.FrameCapture);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Agent_without_matching_scope_is_denied()
    {
        var policy = new AuthorizationPolicy
        {
            AgentAccessEnabled = true,
            GrantedScopes = new HashSet<string> { OperationCatalog.ScopeRead },
        };

        var decision = policy.Authorize(CallerIdentity.Agent("mcp"), OperationCatalog.InputTap);
        Assert.False(decision.Allowed);
        Assert.Equal("scope_not_granted", decision.Code);
    }
}

public sealed class InstanceStoreTests
{
    [Fact]
    public void Save_and_reload_round_trips_every_targeting_field()
    {
        using var temp = new TempWorkspace();
        var store = new InstanceStore(temp.Paths);
        var record = TempWorkspace.NewRecord("alpha", 5554, 8554);
        store.Save(record);

        var loaded = store.Get("alpha");

        Assert.Equal("emulator-5554", loaded.AdbSerial);
        Assert.Equal(8554, loaded.GrpcPort);
        Assert.Equal("ws_alpha", loaded.AvdName);
        Assert.Equal(InstanceRecord.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Update_is_serialized_and_persisted()
    {
        using var temp = new TempWorkspace();
        var store = new InstanceStore(temp.Paths);
        store.Save(TempWorkspace.NewRecord("alpha", 5554, 8554));

        store.Update("alpha", r =>
        {
            r.State = InstanceState.Running;
            r.EnginePid = 4242;
        });

        var loaded = store.Get("alpha");
        Assert.Equal(InstanceState.Running, loaded.State);
        Assert.Equal(4242, loaded.EnginePid);
    }

    [Fact]
    public void List_ignores_corrupt_records_instead_of_failing()
    {
        using var temp = new TempWorkspace();
        var store = new InstanceStore(temp.Paths);
        store.Save(TempWorkspace.NewRecord("alpha", 5554, 8554));
        File.WriteAllText(Path.Combine(temp.Paths.InstancesDirectory, "broken.json"), "{ not json");

        var records = store.List();

        Assert.Single(records);
        Assert.Equal("alpha", records[0].Name);
    }
}

public sealed class PortAllocatorTests
{
    [Fact]
    public void Allocates_even_console_port_and_skips_used_entries()
    {
        var used = new[] { TempWorkspace.NewRecord("alpha", 5554, 8554) };

        var (console, grpc) = PortAllocator.Allocate(used, firstGrpcPort: 8654);

        Assert.Equal(0, console % 2);
        Assert.NotEqual(5554, console);
        Assert.NotEqual(8554, grpc);
    }
}

internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "ws-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Paths = new WorkspacePaths(Root);
        Paths.EnsureLayout();
    }

    public string Root { get; }

    public WorkspacePaths Paths { get; }

    public static InstanceRecord NewRecord(string name, int consolePort, int grpcPort) => new()
    {
        Name = name,
        AvdName = "ws_" + name,
        ImagePackage = "system-images;android-35;default;x86_64",
        DeviceProfile = "pixel_6",
        AvdHome = @"C:\avd",
        ConsolePort = consolePort,
        GrpcPort = grpcPort,
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
