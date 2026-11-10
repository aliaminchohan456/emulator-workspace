using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Agent-facing surface: pairing, scoped sessions, bounded observations,
/// semantic selectors, artifacts, approvals, and network shaping. Every call
/// is authorized against the session bound to one disposable managed guest.
/// </summary>
public sealed class AgentService
{
    public const string ScopeObserve = "observe";
    public const string ScopeInput = "input";
    public const string ScopeApp = "app";
    public const string ScopeLifecycle = "lifecycle";
    public const string ScopeNetwork = "network";
    public const string ScopeRestore = "restore";
    public const string ScopeCleanup = "cleanup";
    public const string ScopeArtifact = "artifact";

    public static readonly string[] AllScopes =
    {
        ScopeObserve, ScopeInput, ScopeApp, ScopeLifecycle, ScopeNetwork, ScopeRestore, ScopeCleanup, ScopeArtifact,
    };

    private static readonly Regex BoundsPattern = new(@"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", RegexOptions.Compiled);

    private readonly WorkspacePaths _paths;
    private readonly SdkLocator _sdk;
    private readonly InstanceManager _manager;
    private readonly AgentStore _store;
    private readonly ConcurrentDictionary<string, Observation> _observations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<JsonObject> _events = new();
    private long _eventSequence;
    private readonly string _artifactRoot;

    public AgentService(
        WorkspacePaths paths,
        SdkLocator sdk,
        InstanceManager manager,
        AgentStore store)
    {
        _paths = paths;
        _sdk = sdk;
        _manager = manager;
        _store = store;
        _artifactRoot = Path.Combine(paths.Root, "artifacts", "agent");
        Directory.CreateDirectory(_artifactRoot);
    }

    public AgentStore Store => _store;

    public int Generation(string instance) =>
        _generations.TryGetValue(instance, out var value) ? value : 0;

    public void BumpGeneration(string instance) => _generations.AddOrUpdate(instance, 1, (_, value) => value + 1);

    public void RecordEvent(string instance, string type, JsonObject data)
    {
        var entry = new JsonObject
        {
            ["seq"] = Interlocked.Increment(ref _eventSequence),
            ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["instance"] = instance,
            ["type"] = type,
            ["data"] = data,
        };
        _events.Enqueue(entry);
        while (_events.Count > 500)
        {
            _events.TryDequeue(out _);
        }
    }

    public JsonObject PollEvents(string instance, long cursor)
    {
        var matching = _events.Where(e => e["instance"]?.GetValue<string>() == instance).ToList();
        if (matching.Count == 0)
        {
            return new JsonObject { ["events"] = new JsonArray(), ["cursor"] = cursor, ["overflow"] = false };
        }

        var oldest = matching[0]["seq"]!.GetValue<long>();
        if (cursor > 0 && cursor < oldest - 1)
        {
            return new JsonObject
            {
                ["events"] = new JsonArray(),
                ["cursor"] = matching[^1]["seq"]!.GetValue<long>(),
                ["overflow"] = true,
                ["resync"] = "Call device_status and screen_capture again.",
            };
        }

        var selected = matching
            .Where(e => e["seq"]!.GetValue<long>() > cursor)
            .Take(200)
            .Select(e => e.DeepClone())
            .ToArray();
        var next = selected.Length == 0
            ? cursor
            : selected[^1]!["seq"]!.GetValue<long>();
        return new JsonObject
        {
            ["events"] = new JsonArray(selected),
            ["cursor"] = next,
            ["overflow"] = false,
        };
    }

    public JsonObject RequestPairing(string clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName) || clientName.Length > 80)
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "clientName is required (max 80 chars).");
        }

        var id = "pair-" + Guid.NewGuid().ToString("N")[..12];
        var code = RandomNumberGenerator.GetHexString(8).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        _store.CreatePairing(new AgentPairing(
            id, clientName, AgentStore.Hash(code), "", null, null, "pending",
            now, now.AddMinutes(2), false));

        return new JsonObject
        {
            ["pairingId"] = id,
            ["code"] = code,
            ["clientName"] = clientName,
            ["expiresUtc"] = now.AddMinutes(2).ToString("O"),
            ["instructions"] = "Show this code to the operator; approve it from the desktop or wsctl, then call pair_complete.",
        };
    }

    public JsonObject ApprovePairing(string pairingId, string scopes, string template)
    {
        var pairing = _store.GetPairing(pairingId)
                      ?? throw new SupervisorOperationException(ErrorCodes.NotFound, "No such pairing request.");
        if (pairing.Status != "pending" || pairing.ExpiresUtc < DateTimeOffset.UtcNow)
        {
            throw new SupervisorOperationException(ErrorCodes.Denied, "The pairing request is not pending or has expired.");
        }

        var templateRecord = _manager.Get(template);
        if (templateRecord.State is InstanceState.Starting or InstanceState.Running)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest,
                "The template guest must be stopped so its writable disk is never copied while running.");
        }

        var requested = scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = requested.Where(s => !AllScopes.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        if (invalid.Count > 0)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, "Unknown scopes: " + string.Join(", ", invalid));
        }

        var instanceName = "ag-" + Guid.NewGuid().ToString("N")[..6];
        var clone = _manager.CreateAsync(
                instanceName,
                templateRecord.ImagePackage,
                templateRecord.DeviceProfile,
                null,
                null,
                templateRecord.GpuMode,
                headless: true,
                CancellationToken.None)
            .GetAwaiter().GetResult();

        _store.UpdatePairing(pairing with
        {
            Scopes = string.Join(',', requested),
            Template = templateRecord.Name,
            Instance = clone.Name,
            Status = "approved",
        });

        return new JsonObject
        {
            ["pairingId"] = pairingId,
            ["clientName"] = pairing.ClientName,
            ["instance"] = clone.Name,
            ["serial"] = clone.AdbSerial,
            ["template"] = templateRecord.Name,
            ["scopes"] = string.Join(',', requested),
            ["status"] = "approved",
        };
    }

    public JsonObject CompletePairing(string pairingId, string code)
    {
        var pairing = _store.GetPairing(pairingId)
                      ?? throw new SupervisorOperationException(ErrorCodes.NotFound, "No such pairing request.");
        if (pairing.Status != "approved" || pairing.Instance is null)
        {
            throw new SupervisorOperationException(ErrorCodes.Denied, "The pairing has not been approved.");
        }

        if (pairing.Used || pairing.ExpiresUtc < DateTimeOffset.UtcNow)
        {
            throw new SupervisorOperationException(ErrorCodes.Denied, "The pairing code is used or expired.");
        }

        if (AgentStore.Hash(code) != pairing.CodeHash)
        {
            throw new SupervisorOperationException(ErrorCodes.Denied, "The pairing code is invalid.");
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var sessionId = "ses-" + Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(2);
        _store.CreateSession(new AgentSession(
            sessionId, pairing.ClientName, pairing.Instance, pairing.Scopes,
            AgentStore.Hash(token), now, expires, false));
        _store.UpdatePairing(pairing with { Status = "completed", Used = true });

        return new JsonObject
        {
            ["session"] = sessionId,
            ["token"] = token,
            ["instance"] = pairing.Instance,
            ["scopes"] = pairing.Scopes,
            ["expiresUtc"] = expires.ToString("O"),
        };
    }

    public AgentSession ResolveSession(string token)
    {
        var session = _store.FindSessionByToken(AgentStore.Hash(token))
                      ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Unknown agent session credential.");
        if (session.Revoked)
        {
            throw new SupervisorOperationException("revoked", "The agent session has been revoked.");
        }

        if (session.ExpiresUtc < DateTimeOffset.UtcNow)
        {
            throw new SupervisorOperationException("expired", "The agent session has expired.");
        }

        return session;
    }

    public void RequireScope(AgentSession session, string scope)
    {
        var scopes = session.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(scope, StringComparer.OrdinalIgnoreCase))
        {
            throw new SupervisorOperationException("forbidden_scope", $"The session does not include scope '{scope}'.");
        }
    }

    /// <summary>
    /// Re-reads the session from durable state immediately before a queued
    /// request or mutation is dispatched. Revocation and expiry are therefore
    /// enforced even if the request was accepted before takeover.
    /// </summary>
    public AgentSession EnsureActive(AgentSession session)
    {
        var current = _store.GetSession(session.Id)
                      ?? throw new SupervisorOperationException(ErrorCodes.Unauthorized, "Unknown agent session.");
        if (current.Revoked)
        {
            throw new SupervisorOperationException(ErrorCodes.Revoked, "The agent session has been revoked.");
        }

        if (current.ExpiresUtc < DateTimeOffset.UtcNow)
        {
            throw new SupervisorOperationException(ErrorCodes.Expired, "The agent session has expired.");
        }

        return current;
    }

    public void BindInstance(AgentSession session, JsonObject? args)
    {
        var requested = JsonArgs.String(args, "name");
        if (!string.IsNullOrEmpty(requested)
            && !requested.Equals(session.Instance, StringComparison.OrdinalIgnoreCase))
        {
            throw new SupervisorOperationException(
                "wrong_instance", $"The session is bound to '{session.Instance}', not '{requested}'.");
        }

        if (args is not null)
        {
            args["name"] = session.Instance;
        }
    }

    public string ApproveAction(string sessionId, string operation)
    {
        var session = _store.GetSession(sessionId)
                      ?? throw new SupervisorOperationException(ErrorCodes.NotFound, "No such agent session.");
        var code = RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
        _store.CreateApproval(
            "apr-" + Guid.NewGuid().ToString("N")[..10],
            session.Id,
            operation,
            operation,
            AgentStore.Hash(code),
            DateTimeOffset.UtcNow.AddMinutes(5));
        return code;
    }

    public void ConsumeApproval(AgentSession session, string operation, JsonObject? args)
    {
        var code = JsonArgs.String(args, "approval");
        if (string.IsNullOrWhiteSpace(code)
            || !_store.ConsumeApproval(session.Id, operation, operation, AgentStore.Hash(code)))
        {
            throw new SupervisorOperationException(
                "approval_required",
                $"Operation '{operation}' needs a fresh single-use approval code bound to this session.");
        }
    }

    public async Task<JsonObject> ScreenshotAsync(AgentSession session, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        var record = _manager.Get(session.Instance);
        var secure = await IsSecureWindowAsync(record, cancellationToken).ConfigureAwait(false);
        var generation = Generation(record.Name);
        if (secure)
        {
            return SecureObservation(record);
        }

        var png = await _manager.CapturePngAsync(record.Name, 1080, "controller", cancellationToken)
            .ConfigureAwait(false);
        if (await IsSecureWindowAsync(record, cancellationToken).ConfigureAwait(false))
        {
            return SecureObservation(record);
        }

        var frameId = "obs-" + Guid.NewGuid().ToString("N")[..12];
        var captured = DateTimeOffset.UtcNow;
        var artifactId = StoreArtifact(session, "screenshot", ".png", png);
        var foreground = await ForegroundAsync(record, cancellationToken).ConfigureAwait(false);
        _observations[frameId] = new Observation(
            record.Name, generation, png, 1080, 2400, captured, artifactId);
        PruneObservations();

        return new JsonObject
        {
            ["instance"] = record.Name,
            ["generation"] = generation,
            ["frameId"] = frameId,
            ["artifactId"] = artifactId,
            ["capturedUtc"] = captured.ToString("O"),
            ["width"] = 1080,
            ["height"] = 2400,
            ["foreground"] = foreground,
            ["previewBase64"] = Convert.ToBase64String(png),
            ["secure"] = false,
        };
    }

    public async Task<JsonObject> UiTreeAsync(AgentSession session, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        var record = _manager.Get(session.Instance);
        if (await IsSecureWindowAsync(record, cancellationToken).ConfigureAwait(false))
        {
            return SecureObservation(record);
        }

        var adb = new AdbClient(_sdk.AdbExe);
        await adb.RunAsync(record.AdbSerial,
            new[] { "shell", "uiautomator", "dump", "/sdcard/ws-ui.xml" },
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var xml = await adb.RunAsync(record.AdbSerial,
            new[] { "shell", "cat", "/sdcard/ws-ui.xml" },
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (await IsSecureWindowAsync(record, cancellationToken).ConfigureAwait(false))
        {
            return SecureObservation(record);
        }

        var nodes = ParseUiNodes(xml.StandardOutput);
        var array = new JsonArray();
        foreach (var node in nodes)
        {
            array.Add(node.ToJson());
        }

        return new JsonObject
        {
            ["instance"] = record.Name,
            ["generation"] = Generation(record.Name),
            ["secure"] = false,
            ["nodes"] = array,
        };
    }

    public async Task<JsonObject> TapAsync(
        AgentSession session, JsonObject args, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        var record = _manager.Get(session.Instance);
        int x;
        int y;
        string? selectorText = null;

        if (args["selector"] is JsonObject selector)
        {
            var node = await ResolveSelectorAsync(record, selector, cancellationToken).ConfigureAwait(false);
            x = node.X;
            y = node.Y;
            selectorText = node.Text;
        }
        else
        {
            var frameId = JsonArgs.String(args, "frameId")
                          ?? throw new SupervisorOperationException(
                              ErrorCodes.InvalidRequest, "frameId is required for coordinate taps.");
            if (!_observations.TryGetValue(frameId, out var observation))
            {
                throw new SupervisorOperationException("stale_observation", "The frame ID is unknown or evicted.");
            }

            if (!observation.Instance.Equals(record.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new SupervisorOperationException("wrong_instance", "The frame belongs to another guest.");
            }

            if (observation.Generation != Generation(record.Name))
            {
                throw new SupervisorOperationException("stale_observation", "The guest generation changed.");
            }

            if ((DateTimeOffset.UtcNow - observation.CapturedUtc).TotalSeconds > 8)
            {
                throw new SupervisorOperationException("stale_observation", "The observation is older than 8 seconds.");
            }

            x = JsonArgs.Int(args, "x") ?? throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, "x is required with frameId.");
            y = JsonArgs.Int(args, "y") ?? throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, "y is required with frameId.");
            if (x < 0 || y < 0 || x >= observation.Width || y >= observation.Height)
            {
                throw new SupervisorOperationException("stale_observation", "Coordinates are outside the observed frame.");
            }
        }

        session = EnsureActive(session);
        await EnsureNotSecureAsync(record, cancellationToken).ConfigureAwait(false);

        await _manager.TapAsync(record.Name, x, y, cancellationToken).ConfigureAwait(false);
        BumpGeneration(record.Name);
        return new JsonObject
        {
            ["instance"] = record.Name,
            ["dispatchedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["x"] = x,
            ["y"] = y,
            ["selectorText"] = selectorText,
        };
    }

    public async Task<JsonObject> TypeAsync(
        AgentSession session, JsonObject args, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        var record = _manager.Get(session.Instance);
        var text = JsonArgs.String(args, "text")
                   ?? throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "text is required.");
        if (text.Length > 500)
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "text is limited to 500 characters.");
        }

        if (args["selector"] is JsonObject selector)
        {
            var node = await ResolveSelectorAsync(record, selector, cancellationToken).ConfigureAwait(false);
            session = EnsureActive(session);
            await EnsureNotSecureAsync(record, cancellationToken).ConfigureAwait(false);
            await _manager.TapAsync(record.Name, node.X, node.Y, cancellationToken).ConfigureAwait(false);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        session = EnsureActive(session);
        await EnsureNotSecureAsync(record, cancellationToken).ConfigureAwait(false);
        var escaped = Uri.EscapeDataString(text).Replace("%20", "%s", StringComparison.Ordinal);
        var adb = new AdbClient(_sdk.AdbExe);
        await adb.RunAsync(record.AdbSerial,
            new[] { "shell", "input", "text", escaped },
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        BumpGeneration(record.Name);
        return new JsonObject { ["instance"] = record.Name, ["typed"] = text.Length };
    }

    public async Task<JsonObject> AssertAsync(
        AgentSession session, JsonObject args, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        var record = _manager.Get(session.Instance);
        if (args["selector"] is not JsonObject selector)
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "selector is required.");
        }

        try
        {
            var node = await ResolveSelectorAsync(record, selector, cancellationToken).ConfigureAwait(false);
            return new JsonObject
            {
                ["pass"] = true,
                ["instance"] = record.Name,
                ["text"] = node.Text,
                ["bounds"] = node.Bounds,
            };
        }
        catch (SupervisorOperationException ex) when (ex.Code == "ambiguous_selector")
        {
            throw;
        }
        catch (SupervisorOperationException ex) when (ex.Code == "selector_not_found")
        {
            return new JsonObject { ["pass"] = false, ["instance"] = record.Name };
        }
    }

    public async Task<JsonObject> LogsAsync(
        AgentSession session, JsonObject args, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        var record = _manager.Get(session.Instance);
        var lines = Math.Clamp(JsonArgs.Int(args, "lines") ?? 200, 1, 2000);
        var adb = new AdbClient(_sdk.AdbExe);
        var result = await adb.RunAsync(record.AdbSerial,
                new[] { "logcat", "-d", "-t", lines.ToString(), "-v", "threadtime" },
                TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var filter = JsonArgs.String(args, "contains");
        var tag = JsonArgs.String(args, "tag");
        var selected = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => string.IsNullOrEmpty(filter) || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(line => string.IsNullOrEmpty(tag) || line.Contains(tag, StringComparison.OrdinalIgnoreCase))
            .Take(2000)
            .ToList();

        var text = string.Join('\n', selected);
        var artifactId = StoreArtifact(session, "logs", ".txt", Encoding.UTF8.GetBytes(text));
        return new JsonObject
        {
            ["instance"] = record.Name,
            ["artifactId"] = artifactId,
            ["lines"] = selected.Count,
            ["text"] = text.Length > 64 * 1024 ? text[.. (64 * 1024)] : text,
        };
    }

    public async Task<JsonObject> NetworkAsync(
        AgentSession session, JsonObject args, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        var record = _manager.Get(session.Instance);
        var delay = JsonArgs.String(args, "delay") ?? "none";
        var speed = JsonArgs.String(args, "speed") ?? "full";
        var requestedLatency = JsonArgs.Int(args, "latencyMs") ?? 0;
        var allowedDelay = new[] { "none", "gprs", "edge", "umts" };
        var allowedSpeed = new[] { "full", "gsm", "hscsd", "gprs", "edge", "umts", "hsdpa" };
        if (!allowedDelay.Contains(delay) || !allowedSpeed.Contains(speed))
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Unsupported delay/speed profile.");
        }

        if (requestedLatency is < 0 or > 5000)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, "latencyMs must be between 0 and 5000.");
        }

        // The emulator console records delay/speed but does not shape virtio
        // traffic on this image. The lab profile therefore applies tc netem to
        // the guest's active interfaces; the console profile is kept as
        // metadata and remains available for engines that do enforce it.
        var latencyMs = requestedLatency > 0
            ? requestedLatency
            : delay switch
            {
                "gprs" => 2500,
                "edge" => 1200,
                "umts" => 600,
                _ => 0,
            };

        var adb = new AdbClient(_sdk.AdbExe);
        await adb.EmulatorConsoleAsync(record.AdbSerial, new[] { "network", "delay", delay }, cancellationToken)
            .ConfigureAwait(false);
        await adb.EmulatorConsoleAsync(record.AdbSerial, new[] { "network", "speed", speed }, cancellationToken)
            .ConfigureAwait(false);

        var interfaces = new JsonArray();
        if (latencyMs > 0)
        {
            var adbUid = await ReadAdbUidAsync(adb, record, cancellationToken).ConfigureAwait(false);
            var viaSu = false;
            if (!adbUid.StartsWith("uid=0(", StringComparison.Ordinal))
            {
                var su = await adb.RunAsync(
                        record.AdbSerial,
                        new[] { "shell", "su", "0", "id" },
                        TimeSpan.FromSeconds(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                viaSu = su.ExitCode == 0
                        && (su.StandardOutput + su.StandardError).Contains("uid=0(", StringComparison.Ordinal);
            }

            if (!adbUid.StartsWith("uid=0(", StringComparison.Ordinal) && !viaSu)
            {
                throw new SupervisorOperationException(
                    ErrorCodes.CapabilityUnavailable,
                    "Guest network shaping needs a root shell. This disposable lab image provides 'su 0'; "
                    + "the ADB daemon itself stays at its original UID.");
            }

            foreach (var iface in new[] { "wlan0", "eth0" })
            {
                var exists = await adb.RunAsync(
                        record.AdbSerial,
                        new[] { "shell", "su", "0", "ip", "link", "show", "dev", iface },
                        TimeSpan.FromSeconds(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (exists.ExitCode != 0)
                {
                    continue;
                }

                var apply = await adb.RunAsync(
                        record.AdbSerial,
                        new[]
                        {
                            "shell", "su", "0", "tc", "qdisc", "replace", "dev", iface,
                            "root", "netem", "delay", latencyMs + "ms",
                        },
                        TimeSpan.FromSeconds(20),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (apply.ExitCode != 0)
                {
                    continue;
                }

                var show = await adb.RunAsync(
                        record.AdbSerial,
                        new[] { "shell", "su", "0", "tc", "qdisc", "show", "dev", iface },
                        TimeSpan.FromSeconds(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!show.StandardOutput.Contains("netem", StringComparison.Ordinal)
                    || !show.StandardOutput.Contains("delay", StringComparison.Ordinal))
                {
                    continue;
                }

                interfaces.Add(iface);
            }

            if (interfaces.Count == 0)
            {
                throw new SupervisorOperationException(
                    ErrorCodes.CapabilityUnavailable,
                    "No guest interface accepted the network shaping profile; baseline traffic is unchanged.");
            }
        }
        else
        {
            await ClearShapingAsync(adb, record, cancellationToken).ConfigureAwait(false);
        }

        return new JsonObject
        {
            ["instance"] = record.Name,
            ["delay"] = delay,
            ["speed"] = speed,
            ["latencyMs"] = latencyMs,
            ["interfaces"] = interfaces,
            ["mechanism"] = latencyMs > 0 ? "tc-netem" : "none",
            ["adbUid"] = await ReadAdbUidAsync(adb, record, cancellationToken).ConfigureAwait(false),
            ["verified"] = true,
        };
    }

    private static async Task<string> ReadAdbUidAsync(
        AdbClient adb, InstanceRecord record, CancellationToken cancellationToken)
    {
        var uid = await adb.RunAsync(
                record.AdbSerial,
                new[] { "shell", "id" },
                TimeSpan.FromSeconds(15),
                cancellationToken)
            .ConfigureAwait(false);
        if (uid.ExitCode != 0)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InstanceUnavailable,
                "The managed ADB transport is not ready: " + (uid.StandardError + uid.StandardOutput).Trim());
        }

        return (uid.StandardOutput + uid.StandardError).Trim();
    }

    private static async Task ClearShapingAsync(
        AdbClient adb, InstanceRecord record, CancellationToken cancellationToken)
    {
        foreach (var iface in new[] { "wlan0", "eth0" })
        {
            try
            {
                await adb.RunAsync(
                        record.AdbSerial,
                        new[] { "shell", "su", "0", "tc", "qdisc", "del", "dev", iface, "root" },
                        TimeSpan.FromSeconds(15),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the interface may not exist or carry no qdisc.
            }
        }
    }

    public async Task<JsonObject> RestoreAsync(
        AgentSession session, JsonObject args, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        ConsumeApproval(session, "snapshot.load", args);
        var tag = JsonArgs.String(args, "tag")
                  ?? throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "tag is required.");
        var result = await _manager.SnapshotAsync(session.Instance, tag, save: false, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var adb = new AdbClient(_sdk.AdbExe);
            var restored = _manager.Get(session.Instance);
            await ClearShapingAsync(adb, restored, cancellationToken).ConfigureAwait(false);
            await adb.EmulatorConsoleAsync(
                    restored.AdbSerial,
                    new[] { "network", "delay", "none" },
                    cancellationToken)
                .ConfigureAwait(false);
            await adb.EmulatorConsoleAsync(
                    restored.AdbSerial,
                    new[] { "network", "speed", "full" },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Restoring the guest is authoritative; shaping reset is best effort.
        }

        BumpGeneration(session.Instance);
        _observations.Clear();
        RecordEvent(session.Instance, "restored", new JsonObject { ["tag"] = tag });
        return result;
    }

    public async Task<JsonObject> CleanupAsync(
        AgentSession session, JsonObject args, CancellationToken cancellationToken)
    {
        session = EnsureActive(session);
        ConsumeApproval(session, "device.cleanup", args);
        var instance = session.Instance;
        await _manager.DeleteAsync(instance, cancellationToken).ConfigureAwait(false);
        _store.RevokeSessionsForInstance(instance);
        BumpGeneration(instance);
        RecordEvent(instance, "cleanup", new JsonObject());
        return new JsonObject { ["instance"] = instance, ["cleaned"] = true };
    }

    public JsonObject ReadArtifact(AgentSession session, string artifactId)
    {
        session = EnsureActive(session);
        var artifact = _store.GetArtifact(artifactId)
                       ?? throw new SupervisorOperationException(ErrorCodes.NotFound, "Unknown artifact ID.");
        if (!artifact.SessionId.Equals(session.Id, StringComparison.Ordinal))
        {
            throw new SupervisorOperationException("wrong_session", "The artifact belongs to another session.");
        }

        if (!artifact.Instance.Equals(session.Instance, StringComparison.OrdinalIgnoreCase))
        {
            throw new SupervisorOperationException("wrong_instance", "The artifact belongs to another guest.");
        }

        if (!File.Exists(artifact.Path) || artifact.Bytes > 4 * 1024 * 1024)
        {
            throw new SupervisorOperationException("artifact_unavailable", "The artifact is missing or too large.");
        }

        var bytes = File.ReadAllBytes(artifact.Path);
        var extension = Path.GetExtension(artifact.Path);
        return new JsonObject
        {
            ["artifactId"] = artifactId,
            ["kind"] = artifact.Kind,
            ["bytes"] = bytes.Length,
            ["base64"] = Convert.ToBase64String(bytes),
            ["text"] = extension is ".txt" or ".json" ? Encoding.UTF8.GetString(bytes) : null,
        };
    }

    private string StoreArtifact(AgentSession session, string kind, string extension, byte[] payload)
    {
        var directory = Path.Combine(_artifactRoot, session.Id);
        Directory.CreateDirectory(directory);
        var id = "art-" + Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(directory, id + extension);
        File.WriteAllBytes(path, payload);
        _store.AddArtifact(id, session.Id, session.Instance, kind, path, payload.Length,
            DateTimeOffset.UtcNow.AddHours(4));
        return id;
    }

    private async Task<UiNode> ResolveSelectorAsync(
        InstanceRecord record, JsonObject selector, CancellationToken cancellationToken)
    {
        await EnsureNotSecureAsync(record, cancellationToken).ConfigureAwait(false);
        var tree = await UiTreeAsyncForRecord(record, cancellationToken).ConfigureAwait(false);
        await EnsureNotSecureAsync(record, cancellationToken).ConfigureAwait(false);
        var resourceId = JsonArgs.String(selector, "resourceId");
        var text = JsonArgs.String(selector, "text");
        var description = JsonArgs.String(selector, "contentDescription");
        var className = JsonArgs.String(selector, "className");

        if (resourceId is null && text is null && description is null && className is null)
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "A selector needs at least one field.");
        }

        var matches = tree.Where(node =>
            (resourceId is null || node.ResourceId == resourceId) &&
            (text is null || node.Text == text) &&
            (description is null || node.Description == description) &&
            (className is null || node.ClassName == className)).ToList();

        if (matches.Count == 0)
        {
            throw new SupervisorOperationException("selector_not_found", "No node matched the selector.");
        }

        if (matches.Count > 1)
        {
            throw new SupervisorOperationException(
                "ambiguous_selector", $"{matches.Count} nodes matched; refine the selector.");
        }

        return matches[0];
    }

    private async Task<List<UiNode>> UiTreeAsyncForRecord(
        InstanceRecord record, CancellationToken cancellationToken)
    {
        var adb = new AdbClient(_sdk.AdbExe);
        await adb.RunAsync(record.AdbSerial,
            new[] { "shell", "uiautomator", "dump", "/sdcard/ws-ui.xml" },
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var xml = await adb.RunAsync(record.AdbSerial,
            new[] { "shell", "cat", "/sdcard/ws-ui.xml" },
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var nodes = ParseUiNodes(xml.StandardOutput);
        return nodes.Select(n => new UiNode(
                n.ResourceId, n.Text, n.Description, n.ClassName,
                n.Left, n.Top, n.Right, n.Bottom, n.Clickable))
            .ToList();
    }

    private static List<UiNode> ParseUiNodes(string xml)
    {
        var nodes = new List<UiNode>();
        try
        {
            var document = XDocument.Parse(xml);
            foreach (var element in document.Descendants("node"))
            {
                var bounds = element.Attribute("bounds")?.Value ?? "";
                var match = BoundsPattern.Match(bounds);
                if (!match.Success)
                {
                    continue;
                }

                var left = int.Parse(match.Groups[1].Value);
                var top = int.Parse(match.Groups[2].Value);
                var right = int.Parse(match.Groups[3].Value);
                var bottom = int.Parse(match.Groups[4].Value);
                nodes.Add(new UiNode(
                    element.Attribute("resource-id")?.Value ?? "",
                    element.Attribute("text")?.Value ?? "",
                    element.Attribute("content-desc")?.Value ?? "",
                    element.Attribute("class")?.Value ?? "",
                    left, top, right, bottom,
                    element.Attribute("clickable")?.Value == "true"));
            }
        }
        catch (Exception)
        {
        }

        return nodes;
    }

    /// <summary>
    /// Detects a FLAG_SECURE window from <c>dumpsys window</c> output. Android 15
    /// prints the flag symbolically in the window's attribute block
    /// (<c>fl=... SECURE ...</c>), not as <c>mSecure=true</c>. Any secure window
    /// blocks observation: the capture path is framebuffer-level and does not
    /// enforce FLAG_SECURE itself.
    /// </summary>
    public static bool ContainsSecureWindow(string? dumpsys)
    {
        if (string.IsNullOrEmpty(dumpsys))
        {
            return false;
        }

        foreach (var raw in dumpsys.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("fl=", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var token in line[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "SECURE" || token == "0x2000" || token == "0x00002000")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonObject SecureObservation(InstanceRecord record, int generation) => new()
    {
        ["status"] = "secure_window",
        ["redacted"] = true,
        ["secure"] = true,
        ["instance"] = record.Name,
        ["generation"] = generation,
        ["nodes"] = new JsonArray(),
        ["message"] = "A FLAG_SECURE window is present; no pixels, tree or selectors are exposed.",
    };

    private JsonObject SecureObservation(InstanceRecord record) =>
        SecureObservation(record, Generation(record.Name));

    private async Task EnsureNotSecureAsync(InstanceRecord record, CancellationToken cancellationToken)
    {
        if (await IsSecureWindowAsync(record, cancellationToken).ConfigureAwait(false))
        {
            throw new SupervisorOperationException(
                ErrorCodes.SecureWindow,
                "A FLAG_SECURE window is present; agent observation and selector input are disabled for this guest.");
        }
    }

    private async Task<bool> IsSecureWindowAsync(InstanceRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var adb = new AdbClient(_sdk.AdbExe);
            var dump = await adb.RunAsync(record.AdbSerial,
                new[] { "shell", "dumpsys", "window" },
                TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return ContainsSecureWindow(dump.StandardOutput);
        }
        catch (Exception)
        {
            // If secure state cannot be determined, fail closed for observations.
            return true;
        }
    }

    private async Task<string?> ForegroundAsync(InstanceRecord record, CancellationToken cancellationToken)
    {
        var adb = new AdbClient(_sdk.AdbExe);
        var dump = await adb.RunAsync(record.AdbSerial,
                new[] { "shell", "dumpsys", "window" },
                TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
        var line = dump.StandardOutput
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("mCurrentFocus=", StringComparison.Ordinal));
        return line?["mCurrentFocus=".Length..].Trim();
    }

    private void PruneObservations()
    {
        foreach (var pair in _observations.OrderByDescending(p => p.Value.CapturedUtc).Skip(40))
        {
            _observations.TryRemove(pair.Key, out _);
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Observation(
        string Instance, int Generation, byte[] Png, int Width, int Height,
        DateTimeOffset CapturedUtc, string ArtifactId);

    private sealed record UiNode(
        string ResourceId, string Text, string Description, string ClassName,
        int Left, int Top, int Right, int Bottom, bool Clickable)
    {
        public int X => (Left + Right) / 2;

        public int Y => (Top + Bottom) / 2;

        public string Bounds => $"[{Left},{Top}][{Right},{Bottom}]";

        public JsonObject ToJson() => new()
        {
            ["resourceId"] = ResourceId,
            ["text"] = Text,
            ["contentDescription"] = Description,
            ["className"] = ClassName,
            ["bounds"] = Bounds,
            ["clickable"] = Clickable,
        };
    }
}
