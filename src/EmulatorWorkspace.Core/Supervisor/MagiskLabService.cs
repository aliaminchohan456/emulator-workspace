using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>Durable base-only lab transaction. The dispatcher holds the instance lease.</summary>
public sealed class MagiskLabService
{
    private readonly InstanceManager _manager;
    private readonly SdkLocator _sdk;
    private readonly string _root;
    private readonly string _logs;
    private readonly MagiskGuestAdapter _guest;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    public MagiskLabService(WorkspacePaths paths, InstanceManager manager, SdkLocator sdk)
    {
        _manager = manager; _sdk = sdk;
        _root = Path.Combine(paths.Root, "magisk-lab");
        _logs = paths.LogsDirectory;
        _guest = new MagiskGuestAdapter(manager, sdk, (name, evidence) =>
        {
            var state = Read(name)!;
            MagiskLabArtifacts.WriteAtomic(Path.Combine(DirectoryFor(state), "evidence", Guid.NewGuid().ToString("N") + ".json"), evidence);
        });
    }

    public SemaphoreSlim Gate(string name) => _gates.GetOrAdd(name, _ => new(1, 1));
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private string StatePath(string name) => Path.Combine(_root, Digest(name.ToLowerInvariant()) + ".json");
    private string Binding(string name)
    {
        var r = _manager.Get(name);
        return Digest(string.Join('\n', r.Name, r.AvdName, r.AvdHome, r.ImagePackage, r.CreatedUtc.ToString("O")));
    }
    private JsonObject? Read(string name)
    {
        var path = StatePath(name);
        MagiskLabArtifacts.NoReparse(path);
        if (!File.Exists(path)) return null;
        var state = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Invalid lab journal; preserve it for recovery.");
        if (state["binding"]?.GetValue<string>() != Binding(name))
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Lab journal belongs to a different instance generation.");
        return state;
    }
    private void Write(string name, JsonObject state, string? phase = null)
    {
        if (phase is not null) state["phase"] = phase;
        state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
        MagiskLabArtifacts.WriteAtomic(StatePath(name), state);
        if (phase is not null)
            _ = _manager.Hub.PublishAsync(name, "state", EventNames.Progress,
                new JsonObject { ["phase"] = "magisk_" + phase, ["detail"] = "Magisk lab: " + phase });
    }
    private string DirectoryFor(JsonObject state)
    {
        var id = state["transaction"]!.GetValue<string>();
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid lab transaction ID.");
        var path = Path.Combine(_root, id);
        MagiskLabArtifacts.NoReparse(path);
        return path;
    }
    public JsonObject Status(string name)
    {
        var state = Read(name);
        if (state is null) return new JsonObject { ["name"] = name, ["phase"] = "not_configured" };
        var result = (JsonObject)state.DeepClone();
        result.Remove("approvalHash"); result.Remove("approvalActor");
        result["recoveryRequired"] = state["phase"]?.GetValue<string>() is not ("prepared" or "verified" or "recovered");
        return result;
    }
    public void ArchiveDeleted(string name)
    {
        var path = StatePath(name);
        if (!File.Exists(path)) return;
        MagiskLabArtifacts.NoReparse(path);
        var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (state["phase"]?.GetValue<string>() != "recovered")
            throw new InvalidOperationException("An unrecovered lab journal cannot be retired.");
        var directory = DirectoryFor(state);
        Directory.CreateDirectory(directory); // A crash may precede the staging directory creation.
        File.Move(path, Path.Combine(directory, "retired-state.json"));
    }
    public void Guard(string name, string operation, JsonObject? args = null, bool agent = false)
    {
        if (!File.Exists(StatePath(name))) return;
        var state = Read(name)!;
        if (agent && state["phase"]?.GetValue<string>() != "recovered" &&
            !(operation.StartsWith("observation.", StringComparison.Ordinal) ||
              operation is "instance.status" or "instance.adbIdentity" or "logs.read" or "event.poll" or "artifact.read"))
            throw new SupervisorOperationException(ErrorCodes.ForbiddenScope,
                "Active Magisk labs permit agent observation only. Recover stock before agent control.");
        if (operation.StartsWith("magisk.", StringComparison.Ordinal)) return;
        if (operation.StartsWith("snapshot.", StringComparison.Ordinal))
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable,
                "Snapshots are fenced for this experimental Magisk lab generation; existing recovery snapshots are preserved.");
        var phase = state["phase"]!.GetValue<string>();
        if (operation is "instance.status" or "instance.adbIdentity" or "instance.adbRootState" or "instance.stop") return;
        if (phase == "verified")
        {
            if (operation is "instance.start" or "instance.restart" or "instance.coldboot")
            {
                var patch = Path.Combine(DirectoryFor(state), "patched.img");
                MagiskLabArtifacts.Verify(patch, MagiskLabArtifacts.PatchHash);
                if (_manager.Get(name).RamdiskPath != patch || args?["ramdisk"] is not null ||
                    args?["coldBoot"]?.GetValue<bool>() == false)
                    throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Verified lab starts require the approved patch and a cold boot.");
                return;
            }
            if (operation is "instance.wait" or "instance.verify" or "controller.probe" ||
                operation.StartsWith("app.", StringComparison.Ordinal) || operation.StartsWith("input.", StringComparison.Ordinal) ||
                operation.StartsWith("frame.", StringComparison.Ordinal) || operation.StartsWith("observation.", StringComparison.Ordinal) ||
                operation.StartsWith("log", StringComparison.Ordinal)) return;
        }
        if (phase != "recovered")
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable,
                $"Lab phase '{phase}' permits only lab install/recovery and status/stop operations. Recover stock before other mutations.");
    }

    public async Task<JsonObject> Prepare(string name, JsonObject args, string actor, CancellationToken ct)
    {
        if (Read(name) is not null)
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "This instance already has a lab journal. Recover it; do not replace its backup.");
        var record = _manager.Get(name);
        if (NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc) || record.RamdiskPath is not null)
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Prepare requires a stopped stock instance without a ramdisk override.");
        if (record.ImagePackage != MagiskLabArtifacts.Image || args["allowProvisioningAdbRoot"]?.GetValue<bool>() != true)
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable, "Only the pinned AOSP API 35 lab with explicit provisioning ADB root opt-in is supported.");
        var bootTimeout = args["bootTimeoutSeconds"]?.GetValue<int>() ?? 300;
        if (bootTimeout is < 1 or > 300)
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Lab boot deadline must be between 1 and 300 seconds.");
        await ValidateSdk(ct);
        var image = Path.Combine(_sdk.SdkRoot, "system-images", "android-35", "default", "x86_64");
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        var state = new JsonObject { ["name"] = name, ["transaction"] = id, ["binding"] = Binding(name),
            ["requestedBy"] = actor, ["allowProvisioningAdbRoot"] = true, ["zygisk"] = false,
            ["modules"] = "none", ["engine"] = "37.2.12.0/16428233", ["image"] = record.ImagePackage,
            ["bootTimeoutSeconds"] = bootTimeout,
            ["adbProvisioning"] = new JsonObject { ["rootRequested"] = false, ["unrootAttempted"] = false } };
        Write(name, state, "staging");
        var directory = DirectoryFor(state);
        Directory.CreateDirectory(directory);
        var sources = new Dictionary<string, string>
        {
            ["stock.img"] = Path.Combine(image, "ramdisk.img"),
            ["patched.img"] = Required(args, "patch"),
            ["manager.apk"] = Required(args, "apk"),
            ["probe.apk"] = Required(args, "probe"),
        };
        foreach (var entry in sources)
            MagiskLabArtifacts.CopyVerified(entry.Value, Path.Combine(directory, entry.Key), MagiskLabArtifacts.Pins[entry.Key]);
        MagiskLabArtifacts.StageEnvironment(Path.Combine(directory, "manager.apk"), Path.Combine(directory, "environment"));
        state["inputs"] = new JsonObject(MagiskLabArtifacts.Pins.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value))));
        Write(name, state, "prepared");
        return Status(name);
    }

    private async Task ValidateSdk(CancellationToken ct)
    {
        var engine = await _sdk.GetEmulatorBuildAsync(ct);
        if (engine.Version != "37.2.12.0" || engine.BuildId != "16428233")
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable, "Magisk lab requires Windows engine 37.2.12.0 build 16428233.");
        var image = Path.Combine(_sdk.SdkRoot, "system-images", "android-35", "default", "x86_64");
        foreach (var entry in new Dictionary<string, string>
        {
            ["ramdisk.img"] = MagiskLabArtifacts.StockHash,
            ["kernel-ranchu"] = "a204606cf2f458540c5437e9ba804b5012acf0a2916d00f099a03e1e553e697e",
            ["system.img"] = "8d7b29ad96c15adb663a5a8ced7f3ea40552019626f6a4420c54dc061f569970",
            ["vendor.img"] = "07a890bca988a65cd651edf340f1498d8ee3c2d0fe91659bc81425bcdd284750",
        }) MagiskLabArtifacts.Verify(Path.Combine(image, entry.Key), entry.Value);
    }

    public JsonObject Approve(string name, string transaction, string action, string actor)
    {
        var state = Read(name) ?? throw new SupervisorOperationException(ErrorCodes.NotFound, "No lab transaction exists.");
        if (state["transaction"]?.GetValue<string>() != transaction || action is not ("install" or "recover") ||
            (action == "install" && state["phase"]?.GetValue<string>() != "prepared"))
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Approval must name the exact current transaction and valid action.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        state["approvalHash"] = Digest(token); state["approvalActor"] = actor;
        state["approvalAction"] = action; state["approvalExpiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        Write(name, state);
        return new JsonObject { ["name"] = name, ["transaction"] = transaction, ["action"] = action,
            ["approval"] = token, ["expiresUtc"] = state["approvalExpiresUtc"]!.DeepClone() };
    }
    public static string Required(JsonObject args, string key) => args[key]?.GetValue<string>()
        ?? throw new SupervisorOperationException(ErrorCodes.InvalidRequest, key + " is required.");
    private JsonObject Consume(string name, string transaction, string token, string action, string actor)
    {
        var state = Read(name) ?? throw new SupervisorOperationException(ErrorCodes.NotFound, "No lab transaction exists.");
        if (state["transaction"]?.GetValue<string>() != transaction || state["approvalActor"]?.GetValue<string>() != actor ||
            state["approvalAction"]?.GetValue<string>() != action || state["approvalHash"]?.GetValue<string>() != Digest(token) ||
            !DateTimeOffset.TryParse(state["approvalExpiresUtc"]?.GetValue<string>(), out var expires) || expires <= DateTimeOffset.UtcNow)
            throw new SupervisorOperationException(ErrorCodes.ApprovalRequired, "Exact, unexpired, single-use lab approval is required for this caller, guest, transaction and action.");
        state.Remove("approvalHash"); state.Remove("approvalActor");
        Write(name, state); // durable consumption before the first guest mutation
        return state;
    }
    private async Task Boot(string name, string ramdisk, CancellationToken ct, int timeoutSeconds = 300)
    {
        var previousLog = Path.Combine(_logs, name + ".engine.log");
        if (File.Exists(previousLog))
        {
            var directory = Path.Combine(DirectoryFor(Read(name)!), "evidence");
            Directory.CreateDirectory(directory);
            File.Copy(previousLog, Path.Combine(directory, "engine-" + Guid.NewGuid().ToString("N") + ".log"));
        }
        await _manager.StartAsync(name, "swiftshader_indirect", true, true, ramdisk, ct);
        await _manager.WaitAsync(name, TimeSpan.FromSeconds(timeoutSeconds), ct);
        var status = await _manager.StatusAsync(name, ct);
        if (status["fingerprint"]?.GetValue<string>() != MagiskLabArtifacts.Fingerprint)
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable, "Guest fingerprint differs from the verified lab image.");
    }

    public async Task<JsonObject> Install(string name, string transaction, string token, string actor, CancellationToken ct)
    {
        var state = Consume(name, transaction, token, "install", actor);
        if (state["phase"]?.GetValue<string>() != "prepared")
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Interrupted installs must recover stock before retry.");
        var directory = DirectoryFor(state);
        // Windows denies writes and replacement while this handle is held, through boot and provisioning.
        using var patchLease = MagiskLabArtifacts.LeaseVerified(Path.Combine(directory, "patched.img"), MagiskLabArtifacts.PatchHash);
        foreach (var pin in MagiskLabArtifacts.Pins) MagiskLabArtifacts.Verify(Path.Combine(directory, pin.Key), pin.Value);
        MagiskLabArtifacts.VerifyEnvironment(Path.Combine(directory, "manager.apk"), Path.Combine(directory, "environment"));
        await ValidateSdk(ct);
        Write(name, state, "baseline_boot");
        try
        {
            await Boot(name, Path.Combine(directory, "stock.img"), ct);
            await _guest.PowerOff(name, ct);
            BackupData(name, directory);
            Write(name, state, "activating");
            await Boot(name, Path.Combine(directory, "patched.img"), ct, state["bootTimeoutSeconds"]!.GetValue<int>());
            state["provisioning"] = await _guest.Provision(name, directory, ct,
                () => RequireProvisioningEnrollment(name, transaction, directory),
                proof => { state["adbProvisioning"] = proof.DeepClone(); Write(name, state); });
            var uid = state["provisioning"]!["probeUid"]!.GetValue<int>();
            Write(name, state, "provisioned");
            await _guest.PowerOff(name, ct);
            var boots = new JsonArray();
            state["boots"] = boots;
            for (var i = 0; i < 2; i++)
            {
                Write(name, state, "verifying_boot_" + (i + 1));
                await Boot(name, Path.Combine(directory, "patched.img"), ct, state["bootTimeoutSeconds"]!.GetValue<int>());
                boots.Add(await _guest.Verify(name, uid, true, ct));
                Write(name, state);
                await _guest.PowerOff(name, ct);
            }
            Write(name, state, "verified");
            return Status(name);
        }
        catch (Exception ex)
        {
            state["failure"] = ex.Message; Write(name, state, "rollback_required");
            using var rollback = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            try { await RecoverCore(name, state, rollback.Token); }
            catch (Exception recovery) { state["recoveryFailure"] = recovery.Message; Write(name, state, "recovery_required"); }
            throw new SupervisorOperationException(ErrorCodes.AdbFailure,
                $"Magisk install failed: {ex.Message}. Recovery state: {state["phase"]}. Evidence and stock backup preserved.");
        }
    }

    public void RequireProvisioningEnrollment(string name, string transaction, string directory)
    {
        // Read rechecks the exact instance generation. No general-purpose root capability is granted.
        var enrolled = Read(name);
        if (enrolled is null || enrolled["transaction"]?.GetValue<string>() != transaction ||
            enrolled["phase"]?.GetValue<string>() != "activating" ||
            enrolled["allowProvisioningAdbRoot"]?.GetValue<bool>() != true ||
            _manager.Get(name).ImagePackage != MagiskLabArtifacts.Image ||
            !string.Equals(DirectoryFor(enrolled), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_manager.Get(name).RamdiskPath, Path.Combine(directory, "patched.img"), StringComparison.OrdinalIgnoreCase))
            throw new SupervisorOperationException(ErrorCodes.ApprovalRequired,
                "Temporary provisioning ADB root requires the opted-in, activating lab transaction and its exact patched guest generation.");
        MagiskLabArtifacts.Verify(Path.Combine(directory, "patched.img"), MagiskLabArtifacts.PatchHash);
    }

    private void BackupData(string name, string directory)
    {
        var record = _manager.Get(name);
        var avd = Path.Combine(record.AvdHome, record.AvdName + ".avd");
        var backup = Path.Combine(directory, "data-backup");
        MagiskLabArtifacts.NoReparse(avd);
        if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < 12L * 1024 * 1024 * 1024)
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "At least 12 GiB free is required for a durable lab data backup.");
        Directory.CreateDirectory(backup);
        var inventory = new JsonObject();
        foreach (var file in new[] { "userdata-qemu.img", "userdata-qemu.img.qcow2", "encryptionkey.img", "encryptionkey.img.qcow2", "cache.img", "cache.img.qcow2" })
        {
            var source = Path.Combine(avd, file);
            if (!File.Exists(source)) continue;
            var hash = MagiskLabArtifacts.Hash(source);
            MagiskLabArtifacts.CopyVerified(source, Path.Combine(backup, file), hash);
            inventory[file] = hash;
        }
        MagiskLabArtifacts.WriteAtomic(Path.Combine(directory, "data-backup.json"), inventory);
    }

    private void RestoreData(string name, JsonObject state)
    {
        var directory = DirectoryFor(state);
        var inventoryPath = Path.Combine(directory, "data-backup.json");
        MagiskLabArtifacts.NoReparse(inventoryPath);
        var inventory = JsonNode.Parse(File.ReadAllText(inventoryPath))!.AsObject();
        var allowed = new[] { "userdata-qemu.img", "userdata-qemu.img.qcow2", "encryptionkey.img", "encryptionkey.img.qcow2", "cache.img", "cache.img.qcow2" };
        // Validate the entire recovery point before touching any current overlay.
        foreach (var entry in inventory)
        {
            if (!allowed.Contains(entry.Key)) throw new InvalidDataException("Unexpected recovery file.");
            MagiskLabArtifacts.Verify(Path.Combine(directory, "data-backup", entry.Key), entry.Value!.GetValue<string>());
        }
        var record = _manager.Get(name);
        var avd = Path.Combine(record.AvdHome, record.AvdName + ".avd");
        Write(name, state, "recovering_baseline");
        foreach (var entry in inventory)
        {
            var current = Path.Combine(avd, entry.Key);
            var hash = entry.Value!.GetValue<string>();
            if (File.Exists(current) && MagiskLabArtifacts.Hash(current) == hash) continue;
            var staged = current + ".recovery-" + Guid.NewGuid().ToString("N");
            MagiskLabArtifacts.CopyVerified(Path.Combine(directory, "data-backup", entry.Key), staged, hash);
            if (File.Exists(current)) File.Move(current, current + ".failed-" + Guid.NewGuid().ToString("N"));
            File.Move(staged, current);
        }
        state["dataRecovery"] = "verified_baseline";
        Write(name, state, "recovering");
    }

    public async Task<JsonObject> Recover(string name, string transaction, string token, string actor, CancellationToken ct)
    {
        var state = Consume(name, transaction, token, "recover", actor);
        await RecoverCore(name, state, ct);
        return Status(name);
    }
    private async Task RecoverCore(string name, JsonObject state, CancellationToken ct)
    {
        var directory = DirectoryFor(state);
        var initialPhase = state["phase"]?.GetValue<string>();
        var initialRecord = _manager.Get(name);
        if (initialPhase is "staging" or "prepared" && initialRecord.RamdiskPath is null &&
            !NativeProcess.IsAlive(initialRecord.EnginePid, initialRecord.EngineStartTimeUtc))
        {
            // Prepare only creates private artifacts; no guest mutation has started.
            state["recoveryOutcome"] = "staging_aborted_no_guest_changes";
            Write(name, state, "recovered");
            return;
        }
        var stock = Path.Combine(directory, "stock.img");
        await ValidateSdk(ct);
        // If staging was interrupted before the private stock copy, the pinned SDK remains a recovery source.
        if (!File.Exists(stock)) stock = Path.Combine(_sdk.SdkRoot, "system-images/android-35/default/x86_64/ramdisk.img");
        MagiskLabArtifacts.Verify(stock, MagiskLabArtifacts.StockHash);
        var resumeData = state["phase"]?.GetValue<string>() == "recovering_baseline";
        Write(name, state, resumeData ? "recovering_baseline" : "recovering");
        var record = _manager.Get(name);
        if (NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
        {
            try { await _guest.PowerOff(name, ct); }
            catch { await _manager.StopAsync(name, true, ct); }
        }
        await _manager.StopAsync(name, true, ct);
        if (resumeData) RestoreData(name, state);
        try { await Boot(name, stock, ct); }
        catch (Exception) when (File.Exists(Path.Combine(directory, "data-backup.json")) && !ct.IsCancellationRequested)
        {
            await _manager.StopAsync(name, true, ct);
            RestoreData(name, state);
            await Boot(name, stock, ct);
        }
        var unroot = await _manager.SetAdbRootAsync(name, false, ct);
        state["recoveryAdbUnroot"] = unroot.DeepClone();
        Write(name, state);
        if (unroot["uidAfter"]?.GetValue<string>() != "2000")
            throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Stock recovery could not restore adbd UID 2000.");
        var probeUid = state["dataRecovery"] is null ? state["provisioning"]?["probeUid"]?.GetValue<int>() : null;
        state["stockProof"] = await _guest.Verify(name, probeUid, false, ct);
        await _guest.PowerOff(name, ct);
        // Clear the override via a normal stock boot; never retain a private patch pointer after recovery.
        await Boot(name, "stock", ct);
        await _guest.PowerOff(name, ct);
        Write(name, state, "recovered");
    }
}
