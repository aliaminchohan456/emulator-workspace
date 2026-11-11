using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Controller;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Cli;

/// <summary>
/// CLI surface that talks to the persistent supervisor. Output text stays
/// compatible with the Phase 0 scripts while every mutation now goes through
/// one versioned registry, one policy, and one lifecycle owner.
/// </summary>
internal static class SupervisorCommands
{
    public static async Task<int> RunAsync(Arguments args)
    {
        var paths = WorkspacePaths.Resolve(args.Optional("root"));
        paths.EnsureLayout();

        await using var client = await SupervisorConnection.ConnectAsync(paths.Root, TimeSpan.FromSeconds(20))
            .ConfigureAwait(false);
        var tokenFile = args.Optional("token-file");
        if (!string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile))
        {
            var raw = File.ReadAllText(tokenFile).Trim();
            try
            {
                var parsed = JsonNode.Parse(raw);
                client.DefaultAuth = parsed?["token"]?.GetValue<string>() ?? raw;
            }
            catch (Exception)
            {
                client.DefaultAuth = raw;
            }
        }

        try
        {
            return await DispatchAsync(client, args, paths).ConfigureAwait(false);
        }
        catch (SupervisorUnavailableException ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 3;
        }
    }

    private static async Task<int> DispatchAsync(SupervisorConnection client, Arguments args, WorkspacePaths paths)
    {
        var command = args.Command;
        if (command == "agent-pair-request")
        {
            var request = await client.RequestAsync(
                OperationNames.AgentPairRequest,
                new JsonObject { ["clientName"] = args.Optional("client-name") ?? "wsctl-agent" },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return PrintResponse(request);
        }

        if (command == "agent-approve")
        {
            var request = await client.RequestAsync(
                OperationNames.AgentPairApprove,
                new JsonObject
                {
                    ["pairingId"] = args.Optional("pairing") ?? throw new ArgumentException("--pairing is required."),
                    ["scopes"] = args.Optional("scopes") ?? string.Join(',', EmulatorWorkspace.Core.Supervisor.AgentService.AllScopes),
                    ["template"] = args.Optional("template") ?? throw new ArgumentException("--template is required."),
                },
                TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            return PrintResponse(request);
        }

        if (command == "agent-complete")
        {
            var request = await client.RequestAsync(
                OperationNames.AgentPairComplete,
                new JsonObject
                {
                    ["pairingId"] = args.Optional("pairing") ?? throw new ArgumentException("--pairing is required."),
                    ["code"] = args.Optional("code") ?? throw new ArgumentException("--code is required."),
                },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!request.Ok)
            {
                Console.Error.WriteLine($"FAILED [{request.Code}] {request.Message}");
                return 5;
            }

            var sessionData = request.Data!.AsObject();
            var outFile = args.Optional("out");
            if (outFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
                File.WriteAllText(outFile, sessionData.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"session={sessionData["session"]} instance={sessionData["instance"]} scopes={sessionData["scopes"]}");
                Console.WriteLine($"token file: {Path.GetFullPath(outFile)}");
            }
            else
            {
                Console.WriteLine(sessionData.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            return 0;
        }

        if (command == "agent-pair")
        {
            var clientName = args.Optional("client-name") ?? "wsctl-agent";
            var template = args.Optional("template") ?? throw new ArgumentException("--template is required.");
            var scopes = args.Optional("scopes") ?? string.Join(',', EmulatorWorkspace.Core.Supervisor.AgentService.AllScopes);
            var begin = await client.RequestAsync(OperationNames.AgentPairRequest,
                new JsonObject { ["clientName"] = clientName }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!begin.Ok) { return PrintResponse(begin); }
            var pairingId = begin.Data!["pairingId"]!.GetValue<string>();
            var code = begin.Data!["code"]!.GetValue<string>();
            var approve = await client.RequestAsync(OperationNames.AgentPairApprove,
                new JsonObject { ["pairingId"] = pairingId, ["scopes"] = scopes, ["template"] = template },
                TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            if (!approve.Ok) { return PrintResponse(approve); }
            var complete = await client.RequestAsync(OperationNames.AgentPairComplete,
                new JsonObject { ["pairingId"] = pairingId, ["code"] = code },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!complete.Ok) { return PrintResponse(complete); }
            var outFile = args.Optional("out") ?? Path.Combine(paths.Root, "agent-session.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, complete.Data!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"paired {clientName} -> {complete.Data!["instance"]} scopes={complete.Data!["scopes"]}");
            Console.WriteLine($"token file: {Path.GetFullPath(outFile)}");
            return 0;
        }

        if (command == "agent-sessions")
        {
            return PrintResponse(await client.RequestAsync(OperationNames.AgentSessionList, null,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false));
        }

        if (command == "agent-revoke")
        {
            return PrintResponse(await client.RequestAsync(OperationNames.AgentSessionRevoke,
                new JsonObject { ["session"] = args.Optional("session") ?? throw new ArgumentException("--session is required.") },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false));
        }

        if (command == "agent-takeover")
        {
            return PrintResponse(await client.RequestAsync(OperationNames.AgentTakeover,
                new JsonObject { ["name"] = args.Optional("name") ?? throw new ArgumentException("--name is required.") },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false));
        }

        if (command == "agent-approve-action")
        {
            return PrintResponse(await client.RequestAsync(OperationNames.AgentApproveAction,
                new JsonObject
                {
                    ["session"] = args.Optional("session") ?? throw new ArgumentException("--session is required."),
                    ["operation"] = args.Optional("operation") ?? "snapshot.load",
                },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false));
        }

        if (command == "call")
        {
            var callOp = args.Optional("op") ?? throw new ArgumentException("--op is required.");
            JsonObject? callArgs = null;
            var rawArgs = args.Optional("args");
            var argsFile = args.Optional("args-file");
            if (!string.IsNullOrWhiteSpace(argsFile) && File.Exists(argsFile))
            {
                rawArgs = File.ReadAllText(argsFile).TrimStart('\uFEFF');
            }

            if (!string.IsNullOrWhiteSpace(rawArgs))
            {
                callArgs = JsonNode.Parse(rawArgs)?.AsObject();
            }

            var callResponse = await client.RequestAsync(callOp, callArgs, TimeSpan.FromMinutes(10),
                idempotencyKey: args.Optional("idempotency")).ConfigureAwait(false);
            if (!callResponse.Ok)
            {
                Console.Error.WriteLine($"FAILED [{callResponse.Code}] {callResponse.Message}");
                return 5;
            }

            Console.WriteLine(callResponse.Data?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (command == "input-latency")
        {
            return await RunInputLatencyAsync(client, args, paths).ConfigureAwait(false);
        }

        if (command == "capture-bench")
        {
            return await RunCaptureBenchAsync(client, args, paths).ConfigureAwait(false);
        }

        if (command == "frame-trace")
        {
            return await RunFrameTraceAsync(client, args, paths).ConfigureAwait(false);
        }

        var name = args.Optional("name") ?? "";
        var requestArgs = new JsonObject();
        if (args.Has("agent"))
        {
            requestArgs["_agent"] = true;
        }

        string op;
        TimeSpan deadline = TimeSpan.FromMinutes(2);

        switch (command)
        {
            case "doctor":
                op = OperationNames.Doctor;
                deadline = TimeSpan.FromMinutes(2);
                break;
            case "list":
                op = OperationNames.InstanceList;
                break;
            case "create":
                op = OperationNames.InstanceCreate;
                requestArgs["name"] = name;
                requestArgs["image"] = args.Optional("image");
                requestArgs["device"] = args.Optional("device");
                requestArgs["console"] = args.OptionalInt("console");
                requestArgs["grpc"] = args.OptionalInt("grpc");
                requestArgs["gpu"] = args.Optional("gpu");
                requestArgs["headless"] = !args.Has("windowed");
                deadline = TimeSpan.FromMinutes(5);
                break;
            case "start":
                op = OperationNames.InstanceStart;
                requestArgs["name"] = name;
                requestArgs["gpu"] = args.Optional("gpu");
                requestArgs["headless"] = !args.Has("windowed");
                requestArgs["coldBoot"] = true;
                requestArgs["ramdisk"] = args.Optional("ramdisk");
                break;
            case "stop":
                op = OperationNames.InstanceStop;
                requestArgs["name"] = name;
                requestArgs["force"] = args.Has("force");
                deadline = TimeSpan.FromMinutes(2);
                break;
            case "restart":
                op = OperationNames.InstanceRestart;
                requestArgs["name"] = name;
                deadline = TimeSpan.FromMinutes(3);
                break;
            case "coldboot":
                op = OperationNames.InstanceColdBoot;
                requestArgs["name"] = name;
                break;
            case "wait":
            {
                var timeoutSeconds = args.OptionalInt("timeout") ?? 480;
                op = OperationNames.InstanceWait;
                requestArgs["name"] = name;
                requestArgs["timeoutSeconds"] = timeoutSeconds;
                deadline = TimeSpan.FromSeconds(timeoutSeconds + 60);
                break;
            }
            case "status":
                op = OperationNames.InstanceStatus;
                requestArgs["name"] = name;
                break;
            case "frame":
                op = OperationNames.FrameSnapshot;
                requestArgs["name"] = name;
                requestArgs["width"] = args.OptionalInt("width");
                requestArgs["via"] = args.Optional("via");
                deadline = TimeSpan.FromSeconds(90);
                break;
            case "tap":
                op = OperationNames.InputTap;
                requestArgs["name"] = name;
                requestArgs["x"] = args.RequiredInt("x");
                requestArgs["y"] = args.RequiredInt("y");
                break;
            case "duplicate":
                op = OperationNames.InstanceDuplicate;
                requestArgs["name"] = name;
                requestArgs["target"] = args.Optional("target") ?? throw new ArgumentException("--target is required.");
                deadline = TimeSpan.FromMinutes(5);
                break;
            case "reset":
                op = OperationNames.InstanceReset;
                requestArgs["name"] = name;
                break;
            case "adb-root":
            case "adb-unroot":
                op = OperationNames.InstanceAdbRoot;
                requestArgs["name"] = name;
                requestArgs["root"] = command == "adb-root";
                deadline = TimeSpan.FromMinutes(3);
                break;
            case "adb-root-state":
                op = OperationNames.InstanceAdbRootState;
                requestArgs["name"] = name;
                break;
            case "package-info":
                op = OperationNames.AppPackageInfo;
                requestArgs["name"] = name;
                requestArgs["package"] = args.Optional("package")
                    ?? throw new ArgumentException("--package is required.");
                break;
            case "package-permissions":
                op = OperationNames.AppPackagePermissions;
                requestArgs["name"] = name;
                requestArgs["package"] = args.Optional("package")
                    ?? throw new ArgumentException("--package is required.");
                break;
            case "deep-link":
                op = OperationNames.AppDeepLink;
                requestArgs["name"] = name;
                requestArgs["uri"] = args.Optional("uri")
                    ?? throw new ArgumentException("--uri is required.");
                break;
            case "instrumentation":
                op = OperationNames.AppInstrumentation;
                requestArgs["name"] = name;
                requestArgs["package"] = args.Optional("package")
                    ?? throw new ArgumentException("--package is required.");
                requestArgs["runner"] = args.Optional("runner") ?? "";
                deadline = TimeSpan.FromMinutes(5);
                break;
            case "logcat-export":
                op = OperationNames.LogcatExport;
                requestArgs["name"] = name;
                requestArgs["lines"] = int.TryParse(args.Optional("lines"), out var exportLines)
                    ? exportLines
                    : 500;
                requestArgs["tag"] = args.Optional("tag") ?? "";
                deadline = TimeSpan.FromMinutes(3);
                break;
            case "crash-report":
                op = OperationNames.DiagnosticsCrash;
                requestArgs["name"] = name;
                requestArgs["package"] = args.Optional("package") ?? "";
                deadline = TimeSpan.FromMinutes(3);
                break;
            case "bugreport":
                op = OperationNames.DiagnosticsBugreport;
                requestArgs["name"] = name;
                deadline = TimeSpan.FromMinutes(15);
                break;
            case "install-apks":
            {
                // A targeted APK-set deployment: the device is resolved from a
                // managed instance so the external tool can never address an
                // unrelated target.
                var apksPath = args.Optional("apks") ?? throw new ArgumentException("--apks is required.");
                if (!File.Exists(apksPath))
                {
                    throw new ArgumentException($"The APK set '{apksPath}' does not exist.");
                }

                var targetName = args.Optional("name") ?? throw new ArgumentException("--name is required.");
                var status = await client.RequestAsync(
                        OperationNames.InstanceStatus,
                        new JsonObject { ["name"] = targetName },
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);
                if (!status.Ok)
                {
                    Console.Error.WriteLine($"FAILED [{status.Code}] {status.Message}");
                    return 5;
                }

                var serial = status.Data?["serial"]?.GetValue<string>() ?? "";
                if (serial.Length == 0)
                {
                    Console.Error.WriteLine("FAILED [instance_not_found] The instance has no ADB serial.");
                    return 5;
                }

                var bundletool = args.Optional("bundletool")
                                  ?? Environment.GetEnvironmentVariable("WS_BUNDLETOOL")
                                  ?? @"D:\Phase3Tools\bundletool-1.18.1.jar";
                if (!File.Exists(bundletool))
                {
                    Console.Error.WriteLine(
                        $"FAILED [capability_unavailable] bundletool was not found at '{bundletool}'. "
                        + "Pass --bundletool or set WS_BUNDLETOOL.");
                    return 5;
                }

                var info = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "java",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                info.ArgumentList.Add("-jar");
                info.ArgumentList.Add(bundletool);
                info.ArgumentList.Add("install-apks");
                info.ArgumentList.Add($"--apks={Path.GetFullPath(apksPath)}");
                info.ArgumentList.Add($"--device-id={serial}");

                using var process = System.Diagnostics.Process.Start(info)!;
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(300000);
                var exit = process.ExitCode;
                var output = (stdout + stderr).Trim();
                if (exit != 0)
                {
                    Console.Error.WriteLine($"FAILED [install_failed] bundletool exited with {exit}");
                    Console.Error.WriteLine(output);
                    return 5;
                }

                Console.WriteLine(JsonSerializer.Serialize(new JsonObject
                {
                    ["instance"] = targetName,
                    ["serial"] = serial,
                    ["apks"] = Path.GetFullPath(apksPath),
                    ["bundletool"] = bundletool,
                    ["installed"] = true,
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(output);
                return 0;
            }
            case "import-avd":
                op = OperationNames.InstanceImportAvd;
                requestArgs["source"] = args.Optional("source") ?? throw new ArgumentException("--source is required.");
                requestArgs["target"] = args.Optional("target") ?? throw new ArgumentException("--target is required.");
                requestArgs["preview"] = !args.Has("apply");
                deadline = TimeSpan.FromMinutes(5);
                break;
            case "profile-export":
                op = OperationNames.InstanceProfileExport;
                requestArgs["name"] = name;
                break;
            case "presets":
                op = OperationNames.InstancePresets;
                break;
            case "adb-identity":
                op = OperationNames.InstanceAdbIdentity;
                requestArgs["name"] = name;
                break;
            case "input-hold":
                op = OperationNames.InstanceInputHold;
                requestArgs["name"] = name;
                requestArgs["held"] = !args.Has("release");
                break;
            case "input-gate":
                op = OperationNames.InstanceInputGate;
                requestArgs["name"] = name;
                break;
            case "snapshots":
                op = OperationNames.SnapshotList;
                requestArgs["name"] = name;
                break;
            case "snapshot-delete":
                op = OperationNames.SnapshotDelete;
                requestArgs["name"] = name;
                requestArgs["tag"] = args.Optional("tag") ?? throw new ArgumentException("--tag is required.");
                requestArgs["force"] = args.Has("force");
                break;
            case "recipe-start":
                op = OperationNames.RecipeRecordStart;
                requestArgs["name"] = name;
                requestArgs["recipe"] = args.Optional("recipe") ?? "recipe";
                break;
            case "recipe-runtime":
                op = OperationNames.RecipeRecordRuntime;
                requestArgs["recipeId"] = args.Optional("recipe-id") ?? throw new ArgumentException("--recipe-id is required.");
                requestArgs["runtime"] = args.Optional("runtime") ?? throw new ArgumentException("--runtime is required.");
                requestArgs["value"] = args.Optional("value") ?? "";
                break;
            case "recipe-stop":
                op = OperationNames.RecipeRecordStop;
                requestArgs["recipeId"] = args.Optional("recipe-id") ?? throw new ArgumentException("--recipe-id is required.");
                break;
            case "recipe-export":
                op = OperationNames.RecipeExport;
                requestArgs["recipeId"] = args.Optional("recipe-id") ?? throw new ArgumentException("--recipe-id is required.");
                break;
            case "recipe-validate":
                op = OperationNames.RecipeValidate;
                requestArgs["recipe"] = args.Optional("recipe") ?? throw new ArgumentException("--recipe is required.");
                if (!string.IsNullOrEmpty(name)) { requestArgs["name"] = name; }
                break;
            case "recipe-replay":
                op = OperationNames.RecipeReplay;
                requestArgs["recipe"] = args.Optional("recipe") ?? throw new ArgumentException("--recipe is required.");
                if (!string.IsNullOrEmpty(name)) { requestArgs["name"] = name; }
                var runtimeFile = args.Optional("runtime-file");
                if (!string.IsNullOrEmpty(runtimeFile) && File.Exists(runtimeFile))
                {
                    requestArgs["runtime"] = JsonNode.Parse(File.ReadAllText(runtimeFile))?.AsObject();
                }
                else if (!string.IsNullOrEmpty(args.Optional("runtime")))
                {
                    var pair = args.Optional("runtime")!.Split('=', 2);
                    requestArgs["runtime"] = new JsonObject { [pair[0]] = pair.Length > 1 ? pair[1] : "" };
                }

                var approvalCode = args.Optional("approval");
                if (!string.IsNullOrEmpty(approvalCode)) { requestArgs["approval"] = approvalCode; }
                // A replay restores a baseline, which can fall back to a cold
                // boot, so the client deadline is generous.
                deadline = TimeSpan.FromMinutes(30);
                break;
            case "snapshot-save":
            case "snapshot-load":
                op = command == "snapshot-save" ? OperationNames.SnapshotSave : OperationNames.SnapshotLoad;
                requestArgs["name"] = name;
                requestArgs["tag"] = args.Optional("tag");
                deadline = TimeSpan.FromMinutes(3);
                break;
            case "verify-target":
                op = "instance.verify";
                requestArgs["name"] = name;
                break;
            case "controller-probe":
                op = "controller.probe";
                requestArgs["name"] = name;
                deadline = TimeSpan.FromSeconds(45);
                break;
            case "destroy":
                op = OperationNames.InstanceDelete;
                requestArgs["name"] = name;
                deadline = TimeSpan.FromMinutes(2);
                break;
            case "supervisor-status":
                op = OperationNames.SupervisorStatus;
                break;
            case "supervisor-stop":
                op = OperationNames.SupervisorShutdown;
                break;
            case "rotate":
                op = OperationNames.DisplayRotate;
                requestArgs["name"] = name;
                requestArgs["rotation"] = args.RequiredInt("rotation");
                break;
            case "install":
                op = OperationNames.AppInstall;
                requestArgs["name"] = name;
                var apkArg = args.Optional("apk") ?? throw new ArgumentException("--apk is required.");
                requestArgs["path"] = apkArg.Equals("sample", StringComparison.OrdinalIgnoreCase)
                    ? "sample"
                    : Path.GetFullPath(apkArg);
                deadline = TimeSpan.FromMinutes(10);
                break;
            case "launch":
                op = OperationNames.AppLaunch;
                requestArgs["name"] = name;
                requestArgs["package"] = args.Optional("package") ?? throw new ArgumentException("--package is required.");
                break;
            case "foreground":
                op = OperationNames.AppForeground;
                requestArgs["name"] = name;
                break;
            default:
                throw new ArgumentException($"Unknown command '{command}'. Run 'wsctl --help'.");
        }

        var response = await client.RequestAsync(op, requestArgs, deadline).ConfigureAwait(false);
        if (!response.Ok)
        {
            var prefix = response.Code == ErrorCodes.Denied ? "DENIED" : "FAILED";
            Console.Error.WriteLine($"{prefix} [{response.Code}] {response.Message}");
            return response.Code == ErrorCodes.Denied ? 4 : 5;
        }

        var data = response.Data?.AsObject() ?? new JsonObject();
        switch (command)
        {
            case "doctor":
                PrintDoctor(data, args.Optional("json"));
                break;
            case "list":
                foreach (var instance in data["instances"]!.AsArray())
                {
                    var item = instance!.AsObject();
                    Console.WriteLine(
                        $"{item["name"]}\t{item["serial"]}\tgrpc:{item["grpcPort"]}\t{item["state"]}\t{item["imagePackage"]}");
                }

                break;
            case "create":
                Console.WriteLine($"created {data["name"]}");
                Console.WriteLine($"  avd       : {data["avdName"]} ({data["deviceProfile"]})");
                Console.WriteLine($"  image     : {data["imagePackage"]}");
                Console.WriteLine($"  adb serial: {data["serial"]}");
                Console.WriteLine($"  controller: 127.0.0.1:{data["grpcPort"]} (token auth)");
                Console.WriteLine($"  avd home  : {data["avdHome"] ?? paths.AvdHome}");
                break;
            case "start":
                Console.WriteLine(
                    $"{data["name"]} starting: pid {data["pid"]}, adb {data["serial"]}, controller {data["grpcPort"]}");
                Console.WriteLine($"  log: {data["logPath"]}");
                break;
            case "wait":
            {
                var status = await client.RequestAsync(
                    OperationNames.InstanceStatus,
                    new JsonObject { ["name"] = name },
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var s = status.Ok ? status.Data!.AsObject() : new JsonObject { ["fingerprint"] = "" };
                Console.WriteLine($"{name} boot_completed=1 in {data["launchedToBootSeconds"]}s");
                Console.WriteLine($"  interactive readiness: {data["launchedToInteractiveSeconds"]}s");
                Console.WriteLine($"  serial     : {data["serial"]}");
                Console.WriteLine($"  fingerprint: {s["fingerprint"]}");
                break;
            }
            case "status":
                if (args.Has("json"))
                {
                    Console.WriteLine(data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.WriteLine(
                        $"{data["name"]}: state={data["state"]} engineAlive={data["engineAlive"]} pid={data["pid"]}");
                    Console.WriteLine(
                        $"  serial={data["serial"]} console={data["consolePort"]} controller={data["grpcPort"]}");
                    Console.WriteLine($"  adb={data["adbState"]} boot_completed={data["bootCompleted"]}");
                    if (data["fingerprint"] is not null)
                    {
                        Console.WriteLine($"  fingerprint={data["fingerprint"]}");
                        Console.WriteLine($"  abi={data["abi"]} sdk={data["sdkLevel"]}");
                    }

                    if (data["lastError"] is not null)
                    {
                        Console.WriteLine("  lastError=" + data["lastError"]);
                    }
                }

                break;
            case "frame":
            {
                var outPath = args.Optional("out") ?? Path.Combine(paths.EvidenceDirectory, name + "-frame.png");
                var bytes = Convert.FromBase64String(data["pngBase64"]!.GetValue<string>());
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                await File.WriteAllBytesAsync(outPath, bytes).ConfigureAwait(false);
                var via = args.Optional("via") ?? "controller";
                Console.WriteLine($"frame {name} via {via}: {bytes.Length} bytes -> {Path.GetFullPath(outPath)}");
                break;
            }
            case "tap":
                Console.WriteLine($"tap {name} at {data["x"]},{data["y"]} via controller");
                break;
            case "snapshot-save":
            case "snapshot-load":
                Console.WriteLine(
                    $"snapshot {(command == "snapshot-save" ? "save" : "load")} {name} " +
                    $"tag={data["tag"]} via=adb in {data["seconds"]}s");
                break;
            case "verify-target":
                Console.WriteLine($"instance       : {data["name"]}");
                Console.WriteLine(
                    $"expected serial: {data["expectedSerial"]} (console {data["consolePort"]}, adb {int.Parse(data["consolePort"]!.ToString()) + 1})");
                Console.WriteLine($"engine alive   : {data["engineAlive"]} (pid {data["pid"]})");
                Console.WriteLine($"get-serialno   : {data["transport"]}");
                Console.WriteLine($"adb devices    : {data["deviceLine"]}");
                if (data["otherEmulators"] is JsonArray others && others.Count > 0)
                {
                    Console.WriteLine(
                        $"other emulators: {string.Join(" | ", others.Select(o => o!.GetValue<string>()))} (excluded from this operation)");
                }

                break;
            case "controller-probe":
                Console.WriteLine($"controller port : {data["port"]}");
                Console.WriteLine($"discovery file  : {data["discoveryPath"]}");
                Console.WriteLine(
                    $"advertised id   : avd={data["avdName"]} adb={data["adbPort"]} build={data["build"]}");
                Console.WriteLine($"engine bind line: {data["advertisedEndpoint"]}");
                Console.WriteLine(
                    $"socket endpoints: {(data["socketEndpoints"]!.AsArray().Count == 0 ? "no listener row observed" : string.Join(", ", data["socketEndpoints"]!.AsArray().Select(e => e!.GetValue<string>())))}");
                Console.WriteLine($"token           : {data["tokenLength"]} chars from discovery (value not logged)");
                Console.WriteLine($"no-token call   : {data["unauthenticated"]}");
                Console.WriteLine($"forged-token    : {data["forged"]}");
                Console.WriteLine(
                    $"authed status   : version={data["statusVersion"]} booted={data["booted"]} uptimeMs={data["uptimeMs"]}");
                Console.WriteLine($"authed frame    : {data["frameBytes"]} bytes (png)");
                Console.WriteLine($"identity match  : {data["identityMatches"]}");
                Console.WriteLine($"binding         : {(data["loopbackOnly"]!.GetValue<bool>() ? "loopback-only" : "CHECK REQUIRED")}");
                Console.WriteLine($"lan probe       : {(data["lanReachable"]!.GetValue<bool>() ? "REACHABLE (unexpected)" : "refused/unreachable")}");
                break;
            case "destroy":
                Console.WriteLine($"destroyed {data["name"]}");
                break;
            case "supervisor-status":
                Console.WriteLine($"supervisor : pid {data["pid"]} protocol {data["version"]}");
                Console.WriteLine($"root       : {data["root"]}");
                Console.WriteLine($"user       : {data["user"]} session {data["session"]}");
                Console.WriteLine($"database   : {data["database"]}");
                Console.WriteLine($"instances  : {data["instances"]}");
                if (data["migrationError"] is not null)
                {
                    Console.WriteLine("migration  : " + data["migrationError"]);
                }

                break;
            case "supervisor-stop":
                Console.WriteLine("supervisor shutdown requested; running guests are left for reattachment");
                break;
            case "rotate":
                Console.WriteLine($"{name} rotation set to {data["rotation"]}");
                break;
            case "install":
                Console.WriteLine($"installed {data["apk"]} ({data["bytes"]} bytes) on {name}");
                break;
            case "launch":
                Console.WriteLine($"launched {data["package"]} on {name} ({data["component"] ?? "monkey fallback"})");
                break;
            case "foreground":
                Console.WriteLine($"{name} foreground: {data["foreground"]}");
                break;
            default:
                Console.WriteLine(data.ToJsonString());
                break;
        }

        return 0;
    }

    private sealed record LatestFrame(string Path, int Width, int Height, uint Seq);

    private static async Task<int> RunInputLatencyAsync(
        SupervisorConnection client, Arguments args, WorkspacePaths paths)
    {
        var name = args.Optional("name") ?? throw new ArgumentException("--name is required.");
        var samplesRequested = args.OptionalInt("samples") ?? 20;
        var x = args.OptionalInt("x") ?? 540;
        var y = args.OptionalInt("y") ?? 720;
        var outPath = args.Optional("out")
                      ?? Path.Combine(paths.Root, $"input-latency-{DateTime.Now:HHmmss}.json");

        LatestFrame? latest = null;
        var gate = new object();
        void OnEvent(EventEnvelope envelope)
        {
            if (envelope.Event != "frame" || envelope.Data is not JsonObject data)
            {
                return;
            }

            var path = data["path"]?.GetValue<string>();
            if (path is null)
            {
                return;
            }

            lock (gate)
            {
                latest = new LatestFrame(
                    path,
                    data["width"]?.GetValue<int>() ?? 0,
                    data["height"]?.GetValue<int>() ?? 0,
                    (uint)(data["seq"]?.GetValue<int>() ?? 0));
            }
        }

        int ReadColor(LatestFrame frame)
        {
            try
            {
                using var stream = new FileStream(frame.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                if (frame.Width <= 0 || frame.Height <= 0 || bytes.Length != frame.Width * frame.Height * 3)
                {
                    return -1;
                }

                var px = frame.Width / 2;
                var py = (int)(frame.Height * 0.30);
                var index = (py * frame.Width + px) * 3;
                return (bytes[index] << 16) | (bytes[index + 1] << 8) | bytes[index + 2];
            }
            catch (Exception)
            {
                return -1;
            }
        }

        client.EventReceived += OnEvent;
        var subscribe = await client.RequestAsync(
            "frame.subscribe",
            new JsonObject { ["name"] = name, ["format"] = "rgb", ["width"] = 540, ["height"] = 1200 },
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!subscribe.Ok)
        {
            Console.Error.WriteLine($"FAILED [{subscribe.Code}] {subscribe.Message}");
            return 5;
        }

        var subscription = subscribe.Data!["subscription"]!.GetValue<string>();
        var waitStart = DateTimeOffset.UtcNow;
        while (latest is null && (DateTimeOffset.UtcNow - waitStart).TotalSeconds < 15)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (latest is null)
        {
            Console.Error.WriteLine("No frame arrived; is the guest running and awake?");
            return 5;
        }

        var samples = new List<JsonObject>();
        var values = new List<double>();
        var timeouts = 0;
        for (var trial = 1; trial <= samplesRequested; trial++)
        {
            LatestFrame current;
            lock (gate)
            {
                current = latest!;
            }

            var baseline = ReadColor(current);
            if (baseline < 0)
            {
                await Task.Delay(100).ConfigureAwait(false);
                continue;
            }

            var clock = Stopwatch.StartNew();
            var tap = await client.RequestAsync(
                "input.tap",
                new JsonObject { ["name"] = name, ["x"] = x, ["y"] = y },
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!tap.Ok)
            {
                samples.Add(new JsonObject { ["trial"] = trial, ["error"] = tap.Code, ["message"] = tap.Message });
                timeouts++;
                continue;
            }

            double? measured = null;
            while (clock.ElapsedMilliseconds < 2500)
            {
                LatestFrame candidate;
                lock (gate)
                {
                    candidate = latest!;
                }

                if (candidate.Seq != current.Seq)
                {
                    var color = ReadColor(candidate);
                    if (color >= 0 && color != baseline)
                    {
                        measured = clock.Elapsed.TotalMilliseconds;
                        break;
                    }
                }

                await Task.Delay(5).ConfigureAwait(false);
            }

            if (measured is null)
            {
                timeouts++;
                samples.Add(new JsonObject { ["trial"] = trial, ["timeout"] = true });
            }
            else
            {
                values.Add(measured.Value);
                samples.Add(new JsonObject
                {
                    ["trial"] = trial,
                    ["ms"] = Math.Round(measured.Value, 2),
                    ["timeout"] = false,
                });
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        await client.RequestAsync(
            "frame.unsubscribe",
            new JsonObject { ["subscription"] = subscription },
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        client.EventReceived -= OnEvent;

        values.Sort();
        double Percentile(double fraction)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            var index = Math.Clamp((int)Math.Ceiling(fraction * values.Count) - 1, 0, values.Count - 1);
            return Math.Round(values[index], 2);
        }

        var summary = new JsonObject
        {
            ["name"] = name,
            ["metric"] = "dispatch-to-captured-frame-with-response",
            ["compositionProxy"] = false,
            ["trials"] = samplesRequested,
            ["valid"] = values.Count,
            ["timeouts"] = timeouts,
            ["p50Ms"] = Percentile(0.50),
            ["p95Ms"] = Percentile(0.95),
            ["minMs"] = values.Count == 0 ? 0 : Math.Round(values[0], 2),
            ["maxMs"] = values.Count == 0 ? 0 : Math.Round(values[^1], 2),
            ["samples"] = new JsonArray(samples.Select(s => (JsonNode)s).ToArray()),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"input-latency {name}: valid={values.Count}/{samplesRequested} timeouts={timeouts} " +
            $"p50={summary["p50Ms"]}ms p95={summary["p95Ms"]}ms");
        Console.WriteLine($"  samples: {Path.GetFullPath(outPath)}");
        return values.Count >= Math.Ceiling(samplesRequested * 0.8) ? 0 : 2;
    }
    private static async Task<int> RunCaptureBenchAsync(
        SupervisorConnection client, Arguments args, WorkspacePaths paths)
    {
        var name = args.Optional("name") ?? throw new ArgumentException("--name is required.");
        var format = (args.Optional("format") ?? "rgba").ToLowerInvariant();
        var width = args.OptionalInt("width") ?? 0;
        var height = args.OptionalInt("height") ?? 0;
        var seconds = args.OptionalInt("seconds") ?? 8;
        var outPath = args.Optional("out")
                      ?? Path.Combine(paths.Root, $"capture-bench-{format}-{width}-{DateTime.Now:HHmmss}.json");

        var status = await client.RequestAsync(
            "instance.status",
            new JsonObject { ["name"] = name },
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!status.Ok)
        {
            Console.Error.WriteLine($"FAILED [{status.Code}] {status.Message}");
            return 5;
        }

        var serial = status.Data!["serial"]!.GetValue<string>();
        var sdk = SdkLocator.Resolve();
        var adb = new AdbClient(sdk.AdbExe);
        var discovery = await EmulatorDiscovery.ReadAsync(adb, serial).ConfigureAwait(false);

        using var channel = EmulatorControlChannel.Connect(discovery.GrpcPort, discovery.GrpcToken, "bearer");
        var request = new Android.Emulation.Control.ImageFormat();
        request.Format = format switch
        {
            "png" => Android.Emulation.Control.ImageFormat.Types.ImgFormat.Png,
            "rgb" => Android.Emulation.Control.ImageFormat.Types.ImgFormat.Rgb888,
            _ => Android.Emulation.Control.ImageFormat.Types.ImgFormat.Rgba8888,
        };
        request.Width = (uint)Math.Max(0, width);
        request.Height = (uint)Math.Max(0, height);

        var samples = new List<(DateTimeOffset At, int Bytes, int Width, int Height, uint Seq, ulong Ts)>();
        byte[]? firstPayload = null;
        var firstWidth = 0;
        var firstHeight = 0;
        var started = DateTimeOffset.UtcNow;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        try
        {
            await foreach (var image in channel.StreamImagesAsync(request, cts.Token).ConfigureAwait(false))
            {
                var payload = image.Image_.ToByteArray();
                samples.Add((DateTimeOffset.UtcNow, payload.Length, (int)image.Format.Width, (int)image.Format.Height, image.Seq, image.TimestampUs));
                if (firstPayload is null)
                {
                    firstPayload = payload;
                    firstWidth = (int)image.Format.Width;
                    firstHeight = (int)image.Format.Height;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Cancelled)
        {
        }

        var intervals = new List<double>();
        for (var i = 1; i < samples.Count; i++)
        {
            intervals.Add((samples[i].At - samples[i - 1].At).TotalMilliseconds);
        }

        intervals.Sort();
        double Percentile(double fraction)
        {
            if (intervals.Count == 0)
            {
                return 0;
            }

            var index = Math.Clamp((int)Math.Ceiling(fraction * intervals.Count) - 1, 0, intervals.Count - 1);
            return Math.Round(intervals[index], 2);
        }

        var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
        var bytesTotal = samples.Sum(s => (long)s.Bytes);
        var expected = format == "rgba" && firstWidth > 0
            ? firstWidth * firstHeight * 4L
            : format == "rgb" && firstWidth > 0
                ? firstWidth * firstHeight * 3L
                : 0;

        var summary = new JsonObject
        {
            ["name"] = name,
            ["format"] = format,
            ["requestedWidth"] = width,
            ["requestedHeight"] = height,
            ["seconds"] = Math.Round(elapsed, 2),
            ["frames"] = samples.Count,
            ["fps"] = samples.Count == 0 ? 0 : Math.Round(samples.Count / elapsed, 2),
            ["firstWidth"] = firstWidth,
            ["firstHeight"] = firstHeight,
            ["firstBytes"] = firstPayload?.Length ?? 0,
            ["expectedRawBytes"] = expected,
            ["bytesPerSecond"] = elapsed <= 0 ? 0 : (long)(bytesTotal / elapsed),
            ["p50IntervalMs"] = Percentile(0.50),
            ["p95IntervalMs"] = Percentile(0.95),
            ["samples"] = new JsonArray(samples.Select(s => (JsonNode)new JsonObject
            {
                ["atUtc"] = s.At.ToString("O"),
                ["bytes"] = s.Bytes,
                ["width"] = s.Width,
                ["height"] = s.Height,
                ["seq"] = s.Seq,
                ["timestampUs"] = s.Ts,
            }).ToArray()),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (firstPayload is not null && format != "png")
        {
            File.WriteAllBytes(outPath + ".raw", firstPayload);
        }
        else if (firstPayload is not null)
        {
            File.WriteAllBytes(outPath + ".png", firstPayload);
        }

        Console.WriteLine(
            $"capture-bench {name} {format} req={width}x{height}: frames={samples.Count} fps={summary["fps"]} " +
            $"dims={firstWidth}x{firstHeight} bytes={firstPayload?.Length} rawExpected={expected} " +
            $"p50={summary["p50IntervalMs"]}ms p95={summary["p95IntervalMs"]}ms");
        Console.WriteLine($"  samples: {Path.GetFullPath(outPath)}");
        return 0;
    }
    private static int PrintResponse(ResponseEnvelope response)
    {
        if (!response.Ok)
        {
            Console.Error.WriteLine($"FAILED [{response.Code}] {response.Message}");
            return 5;
        }

        Console.WriteLine(response.Data?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> RunFrameTraceAsync(
        SupervisorConnection client, Arguments args, WorkspacePaths paths)
    {
        var name = args.Optional("name") ?? throw new ArgumentException("--name is required.");
        var seconds = args.OptionalInt("seconds") ?? 10;
        var width = args.OptionalInt("width") ?? 1080;
        var outPath = args.Optional("out")
                      ?? Path.Combine(paths.Root, $"frame-trace-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        var samples = new List<(DateTimeOffset Received, uint Seq, int Width, int Height, int Bytes, int Dropped)>();
        var gate = new object();
        void OnEvent(EventEnvelope envelope)
        {
            if (envelope.Event != "frame" || envelope.Data is not System.Text.Json.Nodes.JsonObject data)
            {
                return;
            }

            var received = DateTimeOffset.TryParse(data["receivedUtc"]?.GetValue<string>(), out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;
            lock (gate)
            {
                samples.Add((
                    received,
                    (uint)(data["seq"]?.GetValue<int>() ?? 0),
                    data["width"]?.GetValue<int>() ?? 0,
                    data["height"]?.GetValue<int>() ?? 0,
                    data["bytes"]?.GetValue<int>() ?? 0,
                    data["dropped"]?.GetValue<int>() ?? 0));
            }
        }

        client.EventReceived += OnEvent;
        var subscribe = await client.RequestAsync(
            "frame.subscribe",
            new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["width"] = width },
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!subscribe.Ok)
        {
            Console.Error.WriteLine($"FAILED [{subscribe.Code}] {subscribe.Message}");
            return 5;
        }

        var subscription = subscribe.Data!["subscription"]!.GetValue<string>();
        var started = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        await client.RequestAsync(
            "frame.unsubscribe",
            new System.Text.Json.Nodes.JsonObject { ["subscription"] = subscription },
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        client.EventReceived -= OnEvent;

        List<(DateTimeOffset Received, uint Seq, int Width, int Height, int Bytes, int Dropped)> captured;
        lock (gate)
        {
            captured = samples.ToList();
        }

        var intervals = new List<double>();
        var dropped = 0;
        for (var index = 1; index < captured.Count; index++)
        {
            intervals.Add((captured[index].Received - captured[index - 1].Received).TotalMilliseconds);
            dropped += captured[index].Dropped;
        }

        intervals.Sort();
        double Percentile(double fraction)
        {
            if (intervals.Count == 0)
            {
                return 0;
            }

            var index = Math.Clamp((int)Math.Ceiling(fraction * intervals.Count) - 1, 0, intervals.Count - 1);
            return Math.Round(intervals[index], 2);
        }

        var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
        var summary = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = name,
            ["seconds"] = Math.Round(elapsed, 2),
            ["frames"] = captured.Count,
            ["fps"] = captured.Count == 0 ? 0 : Math.Round(captured.Count / elapsed, 2),
            ["p50IntervalMs"] = Percentile(0.50),
            ["p95IntervalMs"] = Percentile(0.95),
            ["maxIntervalMs"] = intervals.Count == 0 ? 0 : Math.Round(intervals[^1], 2),
            ["dropped"] = dropped,
            ["avgBytes"] = captured.Count == 0 ? 0 : (int)captured.Average(s => s.Bytes),
            ["samples"] = new System.Text.Json.Nodes.JsonArray(captured.Select(s =>
                (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["receivedUtc"] = s.Received.ToString("O"),
                    ["seq"] = s.Seq,
                    ["width"] = s.Width,
                    ["height"] = s.Height,
                    ["bytes"] = s.Bytes,
                    ["dropped"] = s.Dropped,
                }).ToArray()),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"frame-trace {name}: frames={captured.Count} fps={summary["fps"]} p50={summary["p50IntervalMs"]}ms p95={summary["p95IntervalMs"]}ms dropped={dropped}");
        Console.WriteLine($"  samples: {Path.GetFullPath(outPath)}");
        return 0;
    }
    private static void PrintDoctor(JsonObject data, string? jsonPath)
    {
        if (jsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            File.WriteAllText(jsonPath, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        var sdk = data["androidSdk"]!.AsObject();
        var gpu = data["gpu"]!.AsObject();
        Console.WriteLine($"Host        : {data["machineName"]} / {data["operatingSystem"]!["full"]}");
        Console.WriteLine($"Architecture: {data["architecture"]} (process {data["processArchitecture"]})");
        Console.WriteLine($"CPU         : {data["processorName"]} / {data["logicalProcessors"]} logical processors");
        Console.WriteLine(
            $"Memory      : {GiB(data["totalMemoryGiB"])} GiB total, {GiB(data["availableMemoryGiB"])} GiB available");
        Console.WriteLine(
            $"Disk        : {GiB(data["workspaceDriveFreeGiB"])} GiB free on {Path.GetPathRoot(data["workspaceRoot"]!.GetValue<string>())}");
        Console.WriteLine($"GPU         : {gpu["name"]} driver {gpu["driverVersion"]}");
        Console.WriteLine($"Acceleration: {data["accelerationVerdict"]}");
        Console.WriteLine($"Hyper-V     : {data["hyperV"]}");
        Console.WriteLine($"SDK         : {sdk["root"]}");
        Console.WriteLine($"Emulator    : {sdk["emulatorVersion"]}");
        Console.WriteLine($"ADB         : {sdk["adbVersion"]}");
        Console.WriteLine("Images      :");
        foreach (var image in sdk["images"]!.AsArray())
        {
            var bytes = image!["systemImageBytes"]!.GetValue<long>();
            Console.WriteLine(
                $"  {image["packageId"]}  rev {image["revision"]}  {bytes / 1024d / 1024d / 1024d:N2} GiB");
        }

        if (jsonPath is not null)
        {
            Console.WriteLine($"JSON        : {Path.GetFullPath(jsonPath)}");
        }
    }

    private static string GiB(JsonNode? node) =>
        (node?.GetValue<double>() ?? 0).ToString("N1", CultureInfo.InvariantCulture);
}
