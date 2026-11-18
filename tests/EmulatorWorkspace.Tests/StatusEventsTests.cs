using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Storage;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-13: the supervisor's additive change streams and the desktop's version
/// and fallback rules. These tests use the real store, manager and hub with a
/// temp workspace; no guest and no pipe are needed.
/// </summary>
public class StatusEventTests
{
    private sealed record Harness(
        TempWorkspace Temp,
        SupervisorStore Store,
        InstanceManager Manager,
        AgentService Agents,
        AgentStore AgentStore,
        StatusEventPublisher Publisher,
        SupervisorEventHub Hub,
        ConcurrentQueue<EventEnvelope> Instances,
        ConcurrentQueue<EventEnvelope> Pairings) : IDisposable
    {
        public void Dispose()
        {
            AgentStore.Dispose();
            Store.Dispose();
            Temp.Dispose();
        }
    }

    private static Harness Create()
    {
        var temp = new TempWorkspace();
        var sdk = new SdkLocator(temp.Root);
        var store = new SupervisorStore(temp.Paths);
        var hub = new SupervisorEventHub();
        var manager = new InstanceManager(temp.Paths, sdk, store, hub);
        var lab = new MagiskLabService(temp.Paths, manager, sdk);
        var publisher = new StatusEventPublisher(hub, manager, lab);
        store.UpsertInstance(TempWorkspace.NewRecord("alpha", 5554, 8554));

        var agentsStore = new AgentStore(temp.Paths);
        var agents = new AgentService(temp.Paths, sdk, manager, agentsStore, publisher);
        var instances = new ConcurrentQueue<EventEnvelope>();
        var pairings = new ConcurrentQueue<EventEnvelope>();
        hub.Add("", "instance", envelope =>
        {
            instances.Enqueue(envelope);
            return Task.CompletedTask;
        });
        hub.Add("", "pairing", envelope =>
        {
            pairings.Enqueue(envelope);
            return Task.CompletedTask;
        });

        return new Harness(temp, store, manager, agents, agentsStore, publisher, hub, instances, pairings);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The event never arrived: " + what);
    }

    [Fact]
    public async Task Reconcile_change_emits_and_a_repeated_state_does_not()
    {
        using var harness = Create();

        var first = await harness.Publisher.PublishInstanceStateAsync("alpha", InstanceState.Starting);
        await WaitForAsync(() => !harness.Instances.IsEmpty, "first instance event");

        var second = await harness.Publisher.PublishInstanceStateAsync("alpha", InstanceState.Starting);
        var third = await harness.Publisher.PublishInstanceStateAsync("alpha", InstanceState.Running);
        await WaitForAsync(() => harness.Instances.Count == 2, "running event");

        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
        Assert.Equal(2, harness.Instances.Count);
        var events = harness.Instances.ToArray();
        Assert.All(events, envelope => Assert.Equal(EventNames.InstanceChanged, envelope.Event));
        Assert.Equal("alpha", events[0].Data!["name"]!.GetValue<string>());
        Assert.Equal("Starting", events[0].Data!["state"]!.GetValue<string>());
        Assert.Equal(1, events[0].Data!["version"]!.GetValue<long>());
        Assert.Equal("Running", events[1].Data!["state"]!.GetValue<string>());
        Assert.Equal(2, events[1].Data!["version"]!.GetValue<long>());
        Assert.True(events[1].Data!["engineAlive"] is not null);
        Assert.True(events[1].Data!["bootCompleted"] is not null);
        Assert.True(events[1].Data!["frameStream"] is JsonObject);
        Assert.True(events[1].Data!["lab"] is JsonObject lab
                    && lab["phase"]!.GetValue<string>() == "not_configured");
    }

    [Fact]
    public async Task Lab_phase_write_emits_even_when_the_lifecycle_word_did_not_move()
    {
        using var harness = Create();
        var record = harness.Store.GetInstance("alpha")!;
        record.State = InstanceState.Running;
        harness.Store.UpsertInstance(record);
        await harness.Publisher.PublishInstanceStateAsync("alpha", InstanceState.Running);
        await WaitForAsync(() => harness.Instances.Count == 1, "running event");

        var version = await harness.Publisher.PublishLabPhaseAsync("alpha");
        await WaitForAsync(() => harness.Instances.Count == 2, "lab event");

        Assert.Equal(2, version);
        var lab = harness.Instances.ToArray()[1];
        Assert.Equal(EventNames.InstanceChanged, lab.Event);
        Assert.Equal(2, lab.Data!["version"]!.GetValue<long>());
        Assert.Equal("Running", lab.Data!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pairing_create_approve_deny_and_expire_each_emit_a_newer_version()
    {
        using var harness = Create();

        var created = await harness.Publisher.PublishPairingAsync(1, "pair-1", "created");
        var approved = await harness.Publisher.PublishPairingAsync(0, "pair-1", "approved");
        var denied = await harness.Publisher.PublishPairingAsync(1, "pair-2", "denied");
        var expired = await harness.Publisher.PublishPairingAsync(0, "pair-3", "expired");
        await WaitForAsync(() => harness.Pairings.Count == 4, "four pairing events");

        Assert.Equal([1L, 2L, 3L, 4L], [created, approved, denied, expired]);
        var events = harness.Pairings.ToArray();
        Assert.All(events, envelope => Assert.Equal(EventNames.PairingChanged, envelope.Event));
        Assert.Equal(["created", "approved", "denied", "expired"],
            events.Select(envelope => envelope.Data!["change"]!.GetValue<string>()));
        Assert.Equal([1, 0, 1, 0],
            events.Select(envelope => envelope.Data!["pendingCount"]!.GetValue<int>()));
        Assert.Equal("pair-1", events[0].Data!["id"]!.GetValue<string>());
        Assert.Equal(4, harness.Publisher.PairingVersion);
    }

    [Fact]
    public async Task Agent_service_emits_on_create_deny_and_expire()
    {
        using var harness = Create();

        var request = harness.Agents.RequestPairing("probe-client");
        var id = request["pairingId"]!.GetValue<string>();
        await WaitForAsync(() => harness.Pairings.Count == 1, "created pairing event");
        Assert.Equal("created", harness.Pairings.ToArray()[0].Data!["change"]!.GetValue<string>());

        harness.Agents.DenyPairing(id);
        await WaitForAsync(() => harness.Pairings.Count == 2, "denied pairing event");
        Assert.Equal("denied", harness.Pairings.ToArray()[1].Data!["change"]!.GetValue<string>());

        // A request that was left pending past its two-minute window is marked
        // and announced by the sweep the next read performs.
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        harness.AgentStore.CreatePairing(new AgentPairing(
            "pair-expired", "probe-client", AgentStore.Hash("code"), "", null, null, "pending",
            expiredAt.AddMinutes(-2), expiredAt, false));
        Assert.Equal(1, harness.Agents.ExpirePairings());
        await WaitForAsync(() => harness.Pairings.Count == 3, "expired pairing event");
        Assert.Equal("expired", harness.Pairings.ToArray()[2].Data!["change"]!.GetValue<string>());
        Assert.Equal("expired", harness.AgentStore.GetPairing("pair-expired")!.Status);
    }
}

/// <summary>
/// EV-13 desktop rules: newest version wins, a stale event changes nothing, and
/// polling returns to the old interval while the stream is unavailable.
/// </summary>
public class StatusEventRuleTests
{
    [Fact]
    public void Newest_instance_version_wins_and_a_stale_one_is_ignored()
    {
        var versions = new StatusEventVersions();

        Assert.True(versions.AcceptInstance("alpha", 7));
        Assert.False(versions.AcceptInstance("alpha", 6));
        Assert.False(versions.AcceptInstance("alpha", 7));
        Assert.True(versions.AcceptInstance("alpha", 8));
        Assert.Equal(8, versions.InstanceVersion("alpha"));
    }

    [Fact]
    public void Seed_never_moves_a_version_backwards()
    {
        var versions = new StatusEventVersions();
        versions.AcceptInstance("alpha", 9);

        versions.SeedInstance("alpha", 4);
        Assert.Equal(9, versions.InstanceVersion("alpha"));

        versions.SeedInstance("alpha", 12);
        Assert.Equal(12, versions.InstanceVersion("alpha"));
        Assert.False(versions.AcceptInstance("alpha", 11));
        Assert.True(versions.AcceptInstance("alpha", 13));
    }

    [Fact]
    public void Pairing_versions_use_the_same_newest_wins_rule()
    {
        var versions = new StatusEventVersions();
        Assert.True(versions.AcceptPairing(3));
        Assert.False(versions.AcceptPairing(2));
        versions.SeedPairing(3);
        Assert.False(versions.AcceptPairing(3));
        Assert.True(versions.AcceptPairing(4));
        Assert.Equal(4, versions.PairingVersion);
    }

    [Fact]
    public void Reconnect_resets_the_counters_a_restarted_supervisor_owns()
    {
        var versions = new StatusEventVersions();
        versions.AcceptInstance("alpha", 7);
        versions.AcceptPairing(6);

        versions.Reset();

        Assert.Equal(0, versions.InstanceVersion("alpha"));
        Assert.Equal(0, versions.PairingVersion);
        Assert.True(versions.AcceptInstance("alpha", 1));
        Assert.True(versions.AcceptPairing(1));
    }

    [Fact]
    public void Polling_falls_back_while_the_stream_is_unavailable()
    {
        Assert.Equal(StatusEventCadence.StatusFallback,
            StatusEventCadence.Poll(false, StatusEventCadence.StatusSafetyNet, StatusEventCadence.StatusFallback));
        Assert.Equal(StatusEventCadence.LibraryFallback,
            StatusEventCadence.Poll(false, StatusEventCadence.LibrarySafetyNet, StatusEventCadence.LibraryFallback));
        Assert.Equal(StatusEventCadence.BadgeFallback,
            StatusEventCadence.Poll(false, StatusEventCadence.BadgeSafetyNet, StatusEventCadence.BadgeFallback));
        Assert.Equal(TimeSpan.FromSeconds(2), StatusEventCadence.StatusFallback);
        Assert.Equal(TimeSpan.FromSeconds(10), StatusEventCadence.BadgeFallback);
    }

    [Fact]
    public void Polling_uses_the_safety_net_while_the_stream_is_live()
    {
        Assert.Equal(TimeSpan.FromSeconds(30),
            StatusEventCadence.Poll(true, StatusEventCadence.StatusSafetyNet, StatusEventCadence.StatusFallback));
        Assert.Equal(TimeSpan.FromSeconds(60),
            StatusEventCadence.Poll(true, StatusEventCadence.BadgeSafetyNet, StatusEventCadence.BadgeFallback));
    }

    [Fact]
    public void Badge_poll_switches_between_sixty_and_ten_seconds()
    {
        var poll = new PendingPairingPoll();
        poll.OnConnected();
        Assert.Equal(TimeSpan.FromSeconds(10), poll.CurrentInterval);

        poll.SetEventSubscription(true);
        Assert.True(poll.EventDriven);
        Assert.Equal(TimeSpan.FromSeconds(60), poll.CurrentInterval);

        poll.SetEventSubscription(false);
        Assert.False(poll.EventDriven);
        Assert.Equal(TimeSpan.FromSeconds(10), poll.CurrentInterval);
        Assert.True(poll.ShouldPoll);
    }
}
