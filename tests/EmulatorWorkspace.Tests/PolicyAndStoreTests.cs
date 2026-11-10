using System.Text.Json.Nodes;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Storage;
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

public sealed class SecureWindowDetectionTests
{
    [Fact]
    public void Symbolic_secure_flag_is_detected()
    {
        const string dump = """
            Window #7 Window{651747f u0 dev.emulatorworkspace.sample/.MainActivity}:
              mAttrs={(0,0)(fillxfill) ty=BASE_APPLICATION
                fl=LAYOUT_IN_SCREEN SECURE LAYOUT_INSET_DECOR HARDWARE_ACCELERATED
                pfl=NO_MOVE_ANIMATION}
            """;
        Assert.True(AgentService.ContainsSecureWindow(dump));
    }

    [Fact]
    public void Hex_secure_flag_is_detected()
    {
        Assert.True(AgentService.ContainsSecureWindow("      fl=LAYOUT_IN_SCREEN 0x2000 LAYOUT_INSET_DECOR"));
    }

    [Fact]
    public void Non_secure_window_and_policy_text_are_not_detected()
    {
        const string dump = """
            mCurrentFocus=Window{651747f u0 dev.emulatorworkspace.sample/.MainActivity}
              fl=LAYOUT_IN_SCREEN LAYOUT_INSET_DECOR SPLIT_TOUCH HARDWARE_ACCELERATED
            mDisableSecureWindows=false
            secure=false
            """;
        Assert.False(AgentService.ContainsSecureWindow(dump));
    }

    [Fact]
    public void Token_lookalike_is_not_detected()
    {
        Assert.False(AgentService.ContainsSecureWindow("      fl=LAYOUT_IN_SCREEN NOT_SECURE"));
        Assert.False(AgentService.ContainsSecureWindow(null));
    }
}

public sealed class AgentSessionTests
{
    private static (AgentService Service, AgentStore Store, TempWorkspace Temp) CreateService()
    {
        var temp = new TempWorkspace();
        var sdk = new SdkLocator(temp.Root);
        var store = new AgentStore(temp.Paths);
        var supervisorStore = new SupervisorStore(temp.Paths);
        var manager = new InstanceManager(temp.Paths, sdk, supervisorStore, new SupervisorEventHub());
        return (new AgentService(temp.Paths, sdk, manager, store), store, temp);
    }

    [Fact]
    public void Expired_session_is_rejected_with_typed_code()
    {
        var (service, store, temp) = CreateService();
        using var _ = temp;
        using var __ = store;
        const string token = "expired-token";
        var now = DateTimeOffset.UtcNow;
        store.CreateSession(new AgentSession(
            "ses-expired", "client", "guest", "observe", AgentStore.Hash(token),
            now.AddHours(-3), now.AddHours(-2), false));

        var error = Assert.Throws<SupervisorOperationException>(() => service.ResolveSession(token));
        Assert.Equal("expired", error.Code);
    }

    [Fact]
    public void Revoked_session_is_rejected_with_typed_code()
    {
        var (service, store, temp) = CreateService();
        using var _ = temp;
        using var __ = store;
        const string token = "revoked-token";
        var now = DateTimeOffset.UtcNow;
        store.CreateSession(new AgentSession(
            "ses-revoked", "client", "guest", "observe", AgentStore.Hash(token),
            now, now.AddHours(1), true));

        var error = Assert.Throws<SupervisorOperationException>(() => service.ResolveSession(token));
        Assert.Equal("revoked", error.Code);
    }

    [Fact]
    public void EnsureActive_rechecks_revocation_in_durable_store()
    {
        var (service, store, temp) = CreateService();
        using var _ = temp;
        using var __ = store;
        const string token = "recheck-token";
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSession(
            "ses-recheck", "client", "guest", "input", AgentStore.Hash(token),
            now, now.AddHours(1), false);
        store.CreateSession(session);
        store.RevokeSession(session.Id);

        var error = Assert.Throws<SupervisorOperationException>(() => service.EnsureActive(session));
        Assert.Equal("revoked", error.Code);
    }

    [Fact]
    public void Event_ring_overflow_returns_resync_without_stale_events()
    {
        var (service, store, temp) = CreateService();
        using var _ = temp;
        using var __ = store;
        for (var index = 0; index < 520; index++)
        {
            service.RecordEvent("guest", "test", new JsonObject { ["index"] = index });
        }

        var poll = service.PollEvents("guest", 1);
        Assert.True(poll["overflow"]!.GetValue<bool>());
        Assert.Empty(poll["events"]!.AsArray());
        Assert.Contains("device_status", poll["resync"]!.GetValue<string>());

        var tail = service.PollEvents("guest", 0);
        Assert.False(tail["overflow"]!.GetValue<bool>());
        Assert.NotEmpty(tail["events"]!.AsArray());
    }

    [Fact]
    public void Reconcile_marks_in_progress_mutation_as_unknown_outcome()
    {
        var (_, store, temp) = CreateService();
        using var _ = temp;
        using var __ = store;
        store.BeginIdempotency("agent-1", "key-1", "input.tap", "digest");
        Assert.Equal(1, store.ReconcileInProgressMutations());

        var record = store.GetIdempotency("agent-1", "key-1");
        Assert.NotNull(record);
        Assert.Equal("unknown_outcome", record!.Value.State);
    }
}

