using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>Fixed lab commands only. No caller-supplied guest or host shell.</summary>
public sealed class MagiskGuestAdapter(InstanceManager manager, SdkLocator sdk, Action<string, JsonObject>? evidence = null)
{
    private readonly AdbClient _adb = new(sdk.AdbExe);

    private async Task<ProcessResult> Run(string name, string[] arguments, CancellationToken ct)
    {
        var record = manager.Get(name);
        var pid = record.EnginePid;
        var started = record.EngineStartTimeUtc;
        if (started is null || !NativeProcess.IsAlive(pid, started))
            throw new SupervisorOperationException(ErrorCodes.NotRunning, "Lab guest has no verified live process.");
        var identity = await manager.AdbIdentityAsync(name, ct);
        var transport = ulong.Parse(identity["transportId"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var avd = await _adb.RunOnTransportAsync(transport, ["emu", "avd", "name"], TimeSpan.FromSeconds(15), ct);
        if (avd.ExitCode != 0 || avd.StandardOutput.Split('\n')[0].Trim() != record.AvdName ||
            !NativeProcess.IsAlive(pid, started) || manager.Get(name).EngineStartTimeUtc != started)
            throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Lab transport/process identity changed.");
        var result = await _adb.RunOnTransportAsync(transport, arguments, TimeSpan.FromSeconds(90), ct);
        evidence?.Invoke(name, new JsonObject
        {
            ["transportId"] = transport.ToString(CultureInfo.InvariantCulture), ["pid"] = pid, ["startedUtc"] = started,
            ["arguments"] = new JsonArray(arguments.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["exitCode"] = result.ExitCode,
            ["stdout"] = result.StandardOutput[..Math.Min(64000, result.StandardOutput.Length)],
            ["stderr"] = result.StandardError[..Math.Min(16000, result.StandardError.Length)],
        });
        return result;
    }

    private async Task<string> Shell(string name, string command, CancellationToken ct)
    {
        var result = await Run(name, ["shell", command.Replace("\r\n", "\n")], ct);
        if (result.ExitCode != 0)
            throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Lab command failed: " + result.Combined[..Math.Min(2000, result.Combined.Length)]);
        return result.StandardOutput.Replace("\r\n", "\n");
    }

    public async Task PowerOff(string name, CancellationToken ct)
    {
        await Shell(name, "sync", ct);
        await Run(name, ["shell", "reboot -p"], ct);
        var end = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < end)
        {
            var record = manager.Get(name);
            if (!NativeProcess.IsAlive(record.EnginePid, record.EngineStartTimeUtc))
            {
                await manager.StopAsync(name, false, ct);
                return;
            }
            await Task.Delay(1000, ct);
        }
        throw new SupervisorOperationException(ErrorCodes.Timeout, "Guest did not power off gracefully; recovery is required.");
    }

    internal async Task<JsonObject> Provision(string name, string directory, CancellationToken ct,
        Action requireEnrollment, Action<JsonObject> journal)
    {
        requireEnrollment();
        foreach (var pin in MagiskLabArtifacts.Pins)
            MagiskLabArtifacts.Verify(Path.Combine(directory, pin.Key), pin.Value);
        MagiskLabArtifacts.VerifyEnvironment(Path.Combine(directory, "manager.apk"), Path.Combine(directory, "environment"));
        await manager.InstallAsync(name, Path.Combine(directory, "manager.apk"), ct);
        await manager.InstallAsync(name, Path.Combine(directory, "probe.apk"), ct);
        var text = await Shell(name, "pm list packages -U " + MagiskLabArtifacts.ProbePackage, ct);
        var match = Regex.Match(text.Trim(), @"\Apackage:dev\.emulatorworkspace\.suprobe uid:(\d+)\z");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var uid) || uid < 10000)
            throw new SupervisorOperationException(ErrorCodes.AdbFailure, "App probe did not resolve to an unprivileged app UID.");
        var pushed = await Run(name, ["push", Path.Combine(directory, "environment") + "/.", "/data/local/tmp/ws-magisk-env/"], ct);
        if (pushed.ExitCode != 0) throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Lab payload push failed.");
        var rootEvidence = new JsonObject { ["rootRequested"] = false, ["unrootAttempted"] = false };
        var provisioning = new JsonObject { ["probeUid"] = uid };
        requireEnrollment();
        rootEvidence["rootRequested"] = true;
        journal(rootEvidence); // Persist intent before restarting adbd.
        try
        {
            var before = await manager.SetAdbRootAsync(name, true, ct);
            rootEvidence["root"] = before.DeepClone();
            journal(rootEvidence);
            if (before["uidAfter"]?.GetValue<string>() != "0")
                throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Provisioning adbd did not become root.");
            var output = await Shell(name, $$"""
                set -ex
                test "$(id -u)" = 0
                validate_dir() {
                    test ! -L "$1"
                    test -d "$1"
                    test "$(stat -c '%u:%g' "$1")" = 0:0
                    label=$(ls -Zd "$1")
                    context=${label%% *}
                    case "$1:$context" in
                        /data/adb:u:object_r:adb_data_file:s0) ;;
                        /data/adb/magisk:u:object_r:system_file:s0|/data/adb/magisk:u:object_r:adb_data_file:s0|/data/adb/magisk:u:object_r:magisk_file:s0) ;;
                        *) echo "Unexpected lab directory SELinux context: $label"; exit 1 ;;
                    esac
                    echo "DIRECTORY $1"
                    stat -c '%u:%g %a' "$1"
                    printf '%s\n' "$label"
                }
                if test -e /data/adb || test -L /data/adb; then validate_dir /data/adb; fi
                if test -e /data/adb/magisk || test -L /data/adb/magisk; then
                    validate_dir /data/adb/magisk
                    entries=$(ls -A /data/adb/magisk)
                    test -z "$entries"
                fi
                mkdir -p /data/adb/magisk
                validate_dir /data/adb
                validate_dir /data/adb/magisk
                chmod 700 /data/adb
                cp -r /data/local/tmp/ws-magisk-env/. /data/adb/magisk/
                chmod -R 755 /data/adb/magisk
                chown -R 0:0 /data/adb/magisk
                /debug_ramdisk/magisk --sqlite 'REPLACE INTO settings (key,value) VALUES("zygisk",0)'
                /debug_ramdisk/magisk --sqlite 'REPLACE INTO policies (uid,policy,until,logging,notification) VALUES(2000,2,0,0,0)'
                /debug_ramdisk/magisk --sqlite 'REPLACE INTO policies (uid,policy,until,logging,notification) VALUES({{uid}},2,0,0,0)'
                cd /data/adb/magisk
                find . -type f | sort | xargs sha256sum
                sync
                """, ct);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "environment"), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Path.Combine(directory, "environment"), file).Replace('\\', '/');
                if (!output.Contains(MagiskLabArtifacts.Hash(file) + "  ./" + relative, StringComparison.Ordinal))
                    throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Provisioned payload hash mismatch.");
            }
            provisioning["payloadHashes"] = output;
        }
        finally
        {
            // Cancellation must not intentionally leave adbd rooted.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                rootEvidence["unrootAttempted"] = true;
                JsonObject after;
                try { journal(rootEvidence); }
                finally { after = await manager.SetAdbRootAsync(name, false, cleanup.Token); }
                rootEvidence["unroot"] = after.DeepClone();
                rootEvidence["adbdUid2000Verified"] = after["uidAfter"]?.GetValue<string>() == "2000";
                journal(rootEvidence);
                if (rootEvidence["adbdUid2000Verified"]!.GetValue<bool>() != true)
                    throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Could not restore adbd UID 2000.");
            }
            catch (Exception ex)
            {
                rootEvidence["unrootError"] = ex.Message;
                journal(rootEvidence);
                throw;
            }
        }
        return provisioning;
    }

    public async Task<JsonObject> Verify(string name, int? uid, bool patched, CancellationToken ct)
    {
        var text = await Shell(name,
            "id; getprop sys.boot_completed; magisk -V; su -c id; echo su_exit=$?; ps -A -o USER,UID,PID,NAME", ct);
        if (!text.StartsWith("uid=2000(shell)", StringComparison.Ordinal) ||
            !Regex.IsMatch(text, @"shell\s+2000\s+\d+\s+adbd") || !text.Contains("\n1\n"))
            throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Boot or independent adbd UID check failed.");
        var rootOk = patched
            ? text.Contains("\n30700\n") && text.Contains("uid=0(root)") && text.Contains("su_exit=0")
            : !text.Contains("magiskd") && text.Contains("su_exit=1");
        if (!rootOk) throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Magisk shell proof failed.");
        var probe = uid.HasValue ? await manager.InstrumentationAsync(name, MagiskLabArtifacts.ProbePackage,
            MagiskLabArtifacts.ProbePackage + "/.SuProbe", ct) : null;
        if (patched && !uid.HasValue)
            throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Patched verification requires the enrolled app UID.");
        var appProof = uid.HasValue ? MagiskProbeEvidence.Validate(probe?["output"]?.GetValue<string>(), uid.Value, patched) : null;
        string? inventory = null;
        if (patched)
        {
            inventory = await Shell(name, """
                su -c 'echo MODULES; find /data/adb/modules /data/adb/modules_update /data/adb/post-fs-data.d /data/adb/service.d -mindepth 1 2>/dev/null; echo SETTINGS; magisk --sqlite "SELECT * FROM settings"; echo ZYGOTE; for p in $(pidof zygote64); do grep -i zygisk /proc/$p/maps; done; echo HOSTS; grep " /system/etc/hosts " /proc/mounts; echo END'
                """, ct);
            if (!inventory.Contains("MODULES\nSETTINGS\n") || !inventory.Contains("key=zygisk|value=0") ||
                !inventory.Contains("ZYGOTE\nHOSTS\nEND"))
                throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Base-only inventory or Zygisk-off proof failed.");
        }
        return new JsonObject { ["shell"] = text, ["app"] = probe, ["appProof"] = appProof, ["inventory"] = inventory, ["verified"] = true };
    }
}
