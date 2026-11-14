using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Storage;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>Storage/authorization tests with a simulated record; no engine is launched.</summary>
public sealed class MagiskLabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ws-magisk-test-" + Guid.NewGuid().ToString("N"));
    private readonly SupervisorStore _store;
    private readonly MagiskLabService _service;
    private readonly string _transaction = Guid.NewGuid().ToString("N");
    private readonly InstanceRecord _record;
    private readonly string _state;
    public MagiskLabTests()
    {
        var paths = new WorkspacePaths(_root);
        paths.EnsureLayout();
        _store = new SupervisorStore(paths);
        _record = new InstanceRecord { Name = "lab", AvdName = "ws_lab", AvdHome = paths.AvdHome,
            ImagePackage = MagiskLabArtifacts.Image, DeviceProfile = "pixel_6", ConsolePort = 5554, GrpcPort = 8554 };
        _store.UpsertInstance(_record);
        var sdk = new SdkLocator(Path.Combine(_root, "missing-sdk"));
        var manager = new InstanceManager(paths, sdk, _store, new SupervisorEventHub());
        _service = new MagiskLabService(paths, manager, sdk);
        _state = Path.Combine(_root, "magisk-lab", Hash("lab") + ".json");
        var binding = Hash(string.Join('\n', _record.Name, _record.AvdName, _record.AvdHome,
            _record.ImagePackage, _record.CreatedUtc.ToString("O")));
        MagiskLabArtifacts.WriteAtomic(_state, new JsonObject { ["name"] = "lab", ["transaction"] = _transaction,
            ["phase"] = "prepared", ["binding"] = binding });
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [Fact] public async Task ApprovalCannotCrossActorOrAction()
    {
        var token = _service.Approve("lab", _transaction, "install", "alice")["approval"]!.GetValue<string>();
        var other = await Assert.ThrowsAsync<SupervisorOperationException>(() => _service.Install("lab", _transaction, token, "bob", default));
        Assert.Equal(ErrorCodes.ApprovalRequired, other.Code);
        var action = await Assert.ThrowsAsync<SupervisorOperationException>(() => _service.Recover("lab", _transaction, token, "alice", default));
        Assert.Equal(ErrorCodes.ApprovalRequired, action.Code);
    }
    [Fact] public async Task ApprovalIsConsumedBeforeArtifactValidation()
    {
        var token = _service.Approve("lab", _transaction, "install", "alice")["approval"]!.GetValue<string>();
        await Assert.ThrowsAsync<SupervisorOperationException>(() => _service.Install("lab", _transaction, token, "alice", default));
        var replay = await Assert.ThrowsAsync<SupervisorOperationException>(() => _service.Install("lab", _transaction, token, "alice", default));
        Assert.Equal(ErrorCodes.ApprovalRequired, replay.Code);
    }
    [Fact] public async Task ExpiredApprovalIsDenied()
    {
        var token = _service.Approve("lab", _transaction, "install", "alice")["approval"]!.GetValue<string>();
        var state = JsonNode.Parse(File.ReadAllText(_state))!.AsObject();
        state["approvalExpiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        MagiskLabArtifacts.WriteAtomic(_state, state);
        var error = await Assert.ThrowsAsync<SupervisorOperationException>(() => _service.Install("lab", _transaction, token, "alice", default));
        Assert.Equal(ErrorCodes.ApprovalRequired, error.Code);
    }
    [Fact] public void SnapshotAndUnreconciledStartAreFenced()
    {
        Assert.Equal(ErrorCodes.CapabilityUnavailable, Assert.Throws<SupervisorOperationException>(() => _service.Guard("lab", "snapshot.load")).Code);
        Assert.Throws<SupervisorOperationException>(() => _service.Guard("lab", "instance.start"));
        _service.Guard("lab", "magisk.recover");
        _service.Guard("lab", "instance.stop");
    }
    [Fact] public void JournalCannotAttachToRecreatedGuest()
    {
        _store.DeleteInstance("lab");
        _record.CreatedUtc = _record.CreatedUtc.AddMinutes(1);
        _store.UpsertInstance(_record);
        Assert.Throws<SupervisorOperationException>(() => _service.Status("lab"));
    }
    [Fact] public void CorruptPatchCannotReplaceStockBackup()
    {
        var source = Path.Combine(_root, "bad.img");
        File.WriteAllText(source, "corrupt");
        var backup = Path.Combine(_root, "stock.img");
        File.WriteAllText(backup, "preserve");
        Assert.Throws<SupervisorOperationException>(() => MagiskLabArtifacts.CopyVerified(source, backup, MagiskLabArtifacts.PatchHash));
        Assert.Equal("preserve", File.ReadAllText(backup));
    }
    [Fact] public void ApprovalIsNotDisclosedByStatus()
    {
        _service.Approve("lab", _transaction, "install", "alice");
        Assert.Null(_service.Status("lab")["approvalHash"]);
    }
    [Fact] public void ProvisioningCannotUseAPreparedOrUnenrolledGeneration()
    {
        var directory = Path.Combine(_root, "magisk-lab", _transaction);
        Assert.Throws<SupervisorOperationException>(() => _service.RequireProvisioningEnrollment("lab", _transaction, directory));
        var state = JsonNode.Parse(File.ReadAllText(_state))!.AsObject();
        state["phase"] = "activating";
        state["allowProvisioningAdbRoot"] = false;
        MagiskLabArtifacts.WriteAtomic(_state, state);
        Assert.Throws<SupervisorOperationException>(() => _service.RequireProvisioningEnrollment("lab", _transaction, directory));
        state["allowProvisioningAdbRoot"] = true;
        MagiskLabArtifacts.WriteAtomic(_state, state);
        Assert.Throws<SupervisorOperationException>(() => _service.RequireProvisioningEnrollment("lab", "different-transaction", directory));
        // Even opted-in activation cannot root a guest without the enrolled patch.
        Assert.Throws<SupervisorOperationException>(() => _service.RequireProvisioningEnrollment("lab", _transaction, directory));
    }
    [Fact] public async Task InterruptedStagingRecoversWithoutAnEngineOrSdk()
    {
        var state = JsonNode.Parse(File.ReadAllText(_state))!.AsObject();
        state["phase"] = "staging";
        MagiskLabArtifacts.WriteAtomic(_state, state);
        var approval = _service.Approve("lab", _transaction, "recover", "alice")["approval"]!.GetValue<string>();
        var recovered = await _service.Recover("lab", _transaction, approval, "alice", default);
        Assert.Equal("recovered", recovered["phase"]!.GetValue<string>());
        Assert.Equal("staging_aborted_no_guest_changes", recovered["recoveryOutcome"]!.GetValue<string>());
        _service.Guard("lab", "instance.delete");
        Assert.Throws<SupervisorOperationException>(() => _service.Guard("lab", "snapshot.load"));
        _service.ArchiveDeleted("lab");
        Assert.False(File.Exists(_state));
        Assert.True(File.Exists(Path.Combine(_root, "magisk-lab", _transaction, "retired-state.json")));
    }
    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_root, true);
    }

    private sealed class Sink : EmulatorWorkspace.Supervisor.IEventSink
    {
        public Task SendEventAsync(EventEnvelope envelope) => Task.CompletedTask;
        public void TrackSubscription(string id) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreDataValidatesAllNamesAndPreservesFailedOverlays(bool invalid)
    {
        var directory = Path.Combine(_root, "magisk-lab", _transaction);
        var backup = Path.Combine(directory, "data-backup");
        var avd = Path.Combine(_record.AvdHome, _record.AvdName + ".avd");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(avd);
        File.WriteAllText(Path.Combine(backup, "userdata-qemu.img"), "baseline");
        File.WriteAllText(Path.Combine(avd, "userdata-qemu.img"), "failed overlay");
        File.WriteAllText(Path.Combine(avd, "userdata-qemu.img.failed-existing"), "older evidence");
        var inventory = new JsonObject { ["userdata-qemu.img"] = MagiskLabArtifacts.Hash(Path.Combine(backup, "userdata-qemu.img")) };
        if (invalid) inventory["../outside.img"] = "invalid";
        MagiskLabArtifacts.WriteAtomic(Path.Combine(directory, "data-backup.json"), inventory);
        var method = typeof(MagiskLabService).GetMethod("RestoreData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var state = JsonNode.Parse(File.ReadAllText(_state))!.AsObject();
        if (invalid)
        {
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(_service, ["lab", state]));
            Assert.IsType<InvalidDataException>(error.InnerException);
            Assert.Equal("failed overlay", File.ReadAllText(Path.Combine(avd, "userdata-qemu.img")));
            Assert.Single(Directory.GetFiles(avd, "*.failed-*"));
        }
        else
        {
            method.Invoke(_service, ["lab", state]);
            Assert.Equal("baseline", File.ReadAllText(Path.Combine(avd, "userdata-qemu.img")));
            Assert.Contains(Directory.GetFiles(avd, "*.failed-*").Select(File.ReadAllText), value => value == "failed overlay");
        }
        Assert.Equal("older evidence", File.ReadAllText(Path.Combine(avd, "userdata-qemu.img.failed-existing")));
    }

    public static IEnumerable<object[]> AgentPhases()
    {
        foreach (var phase in new[] { "staging", "prepared", "baseline_boot", "activating", "provisioned",
            "verifying_boot_1", "verifying_boot_2", "verified", "rollback_required", "recovery_required", "recovering", "recovering_baseline" })
        foreach (var op in new[] { "magisk.prepare", "magisk.approve", "magisk.install", "magisk.recover", "magisk.status",
            "input.tap", "input.text", "app.install", "app.launch", "app.instrumentation", "recipe.replay", "instance.start" })
            yield return new object[] { phase, op };
    }

    [Theory]
    [MemberData(nameof(AgentPhases))]
    public async Task DispatcherRejectsAgentOnActiveLab(string phase, string operation)
    {
        var paths = new WorkspacePaths(_root);
        var sdk = new SdkLocator(Path.Combine(_root, "missing-sdk"));
        var manager = new InstanceManager(paths, sdk, _store, new SupervisorEventHub());
        using var agents = new AgentStore(paths);
        agents.CreateSession(new AgentSession("test", "test", "lab", string.Join(',', AgentService.AllScopes),
            AgentStore.Hash("test-token"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), false));
        using var journal = new Journal(Path.Combine(_root, "test-journal.jsonl"));
        var dispatcher = new EmulatorWorkspace.Supervisor.OperationDispatcher(paths, sdk, _store, manager, journal,
            new AgentService(paths, sdk, manager, agents), new DeviceLibraryService(paths, manager), new RecipeService(paths), () => { });
        var state = JsonNode.Parse(File.ReadAllText(_state))!.AsObject();
        state["phase"] = phase;
        MagiskLabArtifacts.WriteAtomic(_state, state);
        var result = await dispatcher.ExecuteAsync(new RequestEnvelope { Op = operation, Auth = "test-token",
            Args = new JsonObject { ["name"] = "lab" } }, new PeerInfo(1, 1, "test", "test"), new Sink(), default);
        Assert.Equal(ErrorCodes.ForbiddenScope, result.Code);
        if (operation == "recipe.replay")
        {
            // Exercise the internal entry used by nested replay dispatch as well as the outer fence.
            var dispatch = typeof(EmulatorWorkspace.Supervisor.OperationDispatcher).GetMethod("DispatchAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var nested = (Task<JsonNode?>)dispatch.Invoke(dispatcher, ["input.tap",
                new JsonObject { ["name"] = "lab" }, new Sink(), CancellationToken.None, agents.GetSession("test")])!;
            var denied = await Assert.ThrowsAsync<SupervisorOperationException>(() => nested);
            Assert.Equal(ErrorCodes.ForbiddenScope, denied.Code);
        }
    }

    [Theory]
    [MemberData(nameof(AgentPhases))]
    public void ActiveLabRejectsAgentControl(string phase, string operation)
    {
        var state = JsonNode.Parse(File.ReadAllText(_state))!.AsObject();
        state["phase"] = phase;
        MagiskLabArtifacts.WriteAtomic(_state, state);
        var error = Assert.Throws<SupervisorOperationException>(() => _service.Guard("lab", operation, agent: true));
        Assert.Equal(ErrorCodes.ForbiddenScope, error.Code);
        _service.Guard("lab", "instance.status", agent: true);
        if (phase == "verified")
        {
            _service.Guard("lab", "observation.screenshot", agent: true);
            _service.Guard("lab", "input.tap"); // Operator retains control.
        }
        state["phase"] = "recovered";
        MagiskLabArtifacts.WriteAtomic(_state, state);
        _service.Guard("lab", operation, agent: true);
    }

    [Fact]
    public void ProvisioningRehashesEnrolledPatch()
    {
        var directory = Path.Combine(_root, "magisk-lab", _transaction);
        Directory.CreateDirectory(directory);
        _record.RamdiskPath = Path.Combine(directory, "patched.img");
        File.WriteAllText(_record.RamdiskPath, "swapped after boot");
        _store.UpsertInstance(_record);
        var state = JsonNode.Parse(File.ReadAllText(_state))!.AsObject();
        state["phase"] = "activating";
        state["allowProvisioningAdbRoot"] = true;
        MagiskLabArtifacts.WriteAtomic(_state, state);
        var error = Assert.Throws<SupervisorOperationException>(() => _service.RequireProvisioningEnrollment("lab", _transaction, directory));
        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public void VerifiedLeaseHashesOpenedBytesAndReleasesOnMismatch()
    {
        var path = Path.Combine(_root, "lease.img");
        File.WriteAllText(path, "expected");
        var hash = MagiskLabArtifacts.Hash(path);
        File.WriteAllText(path, "replacement before lease");
        Assert.Throws<SupervisorOperationException>(() => MagiskLabArtifacts.LeaseVerified(path, hash));
        using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void VerifiedLeaseIsRewoundAndReleased()
    {
        var path = Path.Combine(_root, "lease.img");
        File.WriteAllText(path, "expected");
        using (var lease = MagiskLabArtifacts.LeaseVerified(path, MagiskLabArtifacts.Hash(path)))
        {
            Assert.Equal(0, lease.Position);
            Assert.Equal((int)'e', lease.ReadByte());
        }
        File.Move(path, path + ".moved");
    }

    [WindowsFact]
    public void VerifiedLeaseExcludesWindowsWritesAndReplacement()
    {
        var path = Path.Combine(_root, "lease.img");
        File.WriteAllText(path, "expected");
        using var lease = MagiskLabArtifacts.LeaseVerified(path, MagiskLabArtifacts.Hash(path));
        Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
        Assert.Throws<IOException>(() => File.Delete(path));
    }

    [Fact]
    public void MissingLeaseArtifactReturnsInvalidRequest()
    {
        var error = Assert.Throws<SupervisorOperationException>(() =>
            MagiskLabArtifacts.LeaseVerified(Path.Combine(_root, "missing.img"), MagiskLabArtifacts.PatchHash));
        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
    }

    [WindowsFact]
    public void UnavailableLeaseArtifactReturnsInvalidRequest()
    {
        var path = Path.Combine(_root, "busy.img");
        File.WriteAllText(path, "expected");
        var hash = MagiskLabArtifacts.Hash(path);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var error = Assert.Throws<SupervisorOperationException>(() => MagiskLabArtifacts.LeaseVerified(path, hash));
        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("13")]
    [InlineData("127")]
    public void StockProofDoesNotInferDenialFromSuExit(string exit)
    {
        var proof = "magisk_exit=127\nmagisk_absent=0\nsu_exit=" + exit + "\n";
        Assert.True(MagiskGuestAdapter.ValidateShellProof(proof, false));
        Assert.False(MagiskGuestAdapter.ValidateShellProof(proof.Replace("magisk_absent=0", "magisk_absent=1"), false));
        Assert.False(MagiskGuestAdapter.ValidateShellProof(proof + "magiskd\n", false));
        Assert.False(MagiskGuestAdapter.ValidateShellProof(proof.Replace("magisk_exit=127", "magisk_exit=0"), false));
    }

    [Theory]
    [InlineData("01")]
    [InlineData("13")]
    [InlineData("127")]
    public void PatchedProofRequiresExactSuccess(string exit)
    {
        var proof = "30700\nmagisk_exit=0\nuid=0(root)\nsu_exit=0\n";
        Assert.True(MagiskGuestAdapter.ValidateShellProof(proof.Replace("\n", "\r\r\n"), true));
        Assert.False(MagiskGuestAdapter.ValidateShellProof(proof.Replace("su_exit=0", "su_exit=" + exit), true));
    }
}
