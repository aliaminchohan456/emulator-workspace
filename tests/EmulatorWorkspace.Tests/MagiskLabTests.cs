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
}
