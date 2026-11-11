using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Mcp;

/// <summary>
/// Thin MCP stdio server over the local supervisor. Stdout carries JSON-RPC
/// only; diagnostics go to stderr. Engine endpoints, controller tokens, ADB and
/// --direct are deliberately not part of the tool surface.
/// </summary>
internal static class Program
{
    private const string ProtocolVersion = "2024-11-05";

    private static readonly object WriteGate = new();
    private static SupervisorConnection? Connection;
    private static string? AgentToken;
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> InFlight = new();

    private static async Task<int> Main(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("EMULATOR_WORKSPACE_HOME")
                   ?? ReadArg(args, "--root")
                   ?? WorkspacePaths.Resolve().Root;
        AgentToken = Environment.GetEnvironmentVariable("WS_AGENT_TOKEN");
        var tokenFile = Environment.GetEnvironmentVariable("WS_AGENT_TOKEN_FILE");
        if (string.IsNullOrWhiteSpace(AgentToken) && !string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile))
        {
            AgentToken = File.ReadAllText(tokenFile).Trim();
        }

        try
        {
            Connection = await SupervisorConnection.ConnectAsync(root, TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("supervisor connection failed: " + ex.Message);
            return 1;
        }

        Console.Error.WriteLine($"ws-mcp ready root={root} token={(AgentToken is null ? "none" : "paired")}");
        var reader = new StreamReader(Console.OpenStandardInput());
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            _ = HandleLineAsync(line);
        }

        return 0;
    }

    private static async Task HandleLineAsync(string line)
    {
        JsonObject? request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" },
            }).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            return;
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";
        var parameters = request["params"] as JsonObject ?? new JsonObject();

        if (id is null)
        {
            if (method == "notifications/cancelled")
            {
                var cancelId = parameters["requestId"]?.ToString();
                if (cancelId is not null && InFlight.TryGetValue(cancelId, out var source))
                {
                    source.Cancel();
                }
            }

            return;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    await SendAsync(Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "emulator-workspace",
                            ["version"] = "0.2.0",
                        },
                    })).ConfigureAwait(false);
                    return;

                case "ping":
                    await SendAsync(Result(id, new JsonObject { ["status"] = "ok" })).ConfigureAwait(false);
                    return;

                case "tools/list":
                    await SendAsync(Result(id, new JsonObject { ["tools"] = ToolCatalog() })).ConfigureAwait(false);
                    return;

                case "tools/call":
                    await CallToolAsync(id, parameters).ConfigureAwait(false);
                    return;

                default:
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                    }).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("mcp error: " + ex);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message },
            }).ConfigureAwait(false);
        }
    }

    private static async Task CallToolAsync(JsonNode id, JsonObject parameters)
    {
        var name = parameters["name"]?.GetValue<string>() ?? "";
        var args = parameters["arguments"] as JsonObject ?? new JsonObject();
        var requestId = id.ToString();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        InFlight[requestId] = cts;

        try
        {
            var (op, opArgs, unwrap) = Map(name, args);
            var response = await Connection!.RequestAsync(
                op, opArgs, TimeSpan.FromMinutes(10), cts.Token, AgentToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                await SendAsync(Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = new JsonObject
                        {
                            ["status"] = "error",
                            ["code"] = response.Code,
                            ["message"] = response.Message,
                        }.ToJsonString(),
                    }),
                    ["isError"] = true,
                })).ConfigureAwait(false);
                return;
            }

            var data = response.Data as JsonObject ?? new JsonObject();
            if (name == "pair_complete")
            {
                AgentToken = data["token"]?.GetValue<string>();
                data.Remove("token");
                data["paired"] = true;
            }

            var content = new JsonArray();
            if (name == "screen_capture" && data["previewBase64"] is JsonValue preview)
            {
                var base64 = preview.GetValue<string>();
                data.Remove("previewBase64");
                content.Add(new JsonObject { ["type"] = "text", ["text"] = data.ToJsonString() });
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = base64,
                    ["mimeType"] = "image/png",
                });
            }
            else
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = data.ToJsonString() });
            }

            await SendAsync(Result(id, new JsonObject
            {
                ["content"] = content,
                ["isError"] = false,
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendAsync(Result(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "{\"status\":\"cancelled\"}",
                }),
                ["isError"] = true,
            })).ConfigureAwait(false);
        }
        finally
        {
            InFlight.TryRemove(requestId, out _);
        }
    }

    private static (string Op, JsonObject Args, bool Unwrap) Map(string tool, JsonObject args)
    {
        var mapped = args.DeepClone().AsObject();
        return tool switch
        {
            "pair_request" => (OperationNames.AgentPairRequest, mapped, false),
            "pair_complete" => (OperationNames.AgentPairComplete, mapped, false),
            "device_create_disposable" => (OperationNames.DeviceCreateDisposable, mapped, false),
            "device_wait_ready" => (OperationNames.InstanceWait, WithTimeout(mapped), false),
            "device_status" => (OperationNames.InstanceStatus, mapped, false),
            "screen_capture" => (OperationNames.ObservationScreenshot, mapped, false),
            "ui_tree" => (OperationNames.ObservationTree, mapped, false),
            "ui_tap" => (OperationNames.InputTap, mapped, false),
            "ui_type" => (OperationNames.InputText, mapped, false),
            "ui_assert" => (OperationNames.UiAssert, mapped, false),
            "app_install" => (OperationNames.AppInstall, mapped, false),
            "app_launch" => (OperationNames.AppLaunch, mapped, false),
            "logs_read" => (OperationNames.LogsRead, mapped, false),
            "event_poll" => (OperationNames.EventPoll, mapped, false),
            "network_set_profile" => (OperationNames.NetworkProfile, mapped, false),
            "recipe_validate" => (OperationNames.RecipeValidate, mapped, false),
            "recipe_export" => (OperationNames.RecipeExport, mapped, false),
            "recipe_replay" => (OperationNames.RecipeReplay, mapped, false),
            "snapshot_save" => (OperationNames.SnapshotSave, mapped, false),
            "snapshot_restore" => (OperationNames.SnapshotLoad, mapped, false),
            "artifact_read" => (OperationNames.ArtifactRead, mapped, false),
            "device_cleanup" => (OperationNames.DeviceCleanup, mapped, false),
            _ => throw new InvalidOperationException($"Unknown tool '{tool}'."),
        };
    }

    private static JsonObject WithTimeout(JsonObject args)
    {
        args["timeoutSeconds"] = args["timeoutSeconds"] ?? 600;
        return args;
    }

    private static JsonArray ToolCatalog()
    {
        JsonObject Schema(params string[] required) => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            ["additionalProperties"] = true,
        };

        JsonObject Tool(string name, string description, JsonObject schema) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };

        JsonObject Selector() => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["resourceId"] = new JsonObject { ["type"] = "string" },
                ["text"] = new JsonObject { ["type"] = "string" },
                ["contentDescription"] = new JsonObject { ["type"] = "string" },
                ["className"] = new JsonObject { ["type"] = "string" },
            },
        };

        return new JsonArray
        {
            Tool("pair_request", "Request a local pairing code to show to the operator.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["clientName"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("clientName"),
                }),
            Tool("pair_complete", "Complete a pairing after operator approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["pairingId"] = new JsonObject { ["type"] = "string" },
                        ["code"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("pairingId", "code"),
                }),
            Tool("device_create_disposable", "Return the disposable guest bound to this session.",
                Schema()),
            Tool("device_wait_ready", "Wait for interactive readiness.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["timeoutSeconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 10, ["maximum"] = 900 },
                    },
                }),
            Tool("device_status", "Read managed guest state, boot and foreground information.", Schema()),
            Tool("screen_capture", "Capture a bounded PNG observation; returns a frame ID and artifact ID.", Schema()),
            Tool("ui_tree", "Return the bounded accessibility tree with resource ID, text and content description.", Schema()),
            Tool("ui_tap", "Tap by semantic selector, or by frame-bound coordinates.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["selector"] = Selector(),
                        ["frameId"] = new JsonObject { ["type"] = "string" },
                        ["x"] = new JsonObject { ["type"] = "integer" },
                        ["y"] = new JsonObject { ["type"] = "integer" },
                    },
                }),
            Tool("ui_type", "Type into the focused field or a selector.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject { ["type"] = "string", ["maxLength"] = 500 },
                        ["selector"] = Selector(),
                    },
                    ["required"] = new JsonArray("text"),
                }),
            Tool("ui_assert", "Assert that one selector resolves to exactly one node.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["selector"] = Selector() },
                    ["required"] = new JsonArray("selector"),
                }),
            Tool("app_install", "Install the approved sample APK on the disposable guest.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["apk"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("sample") },
                    },
                }),
            Tool("app_launch", "Launch a package.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["package"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("package"),
                }),
            Tool("logs_read", "Read bounded filtered Logcat.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["lines"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 2000 },
                        ["tag"] = new JsonObject { ["type"] = "string" },
                        ["contains"] = new JsonObject { ["type"] = "string" },
                    },
                }),
            Tool("event_poll", "Poll ordered supervisor events after a cursor; overflow returns a resync directive.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["cursor"] = new JsonObject { ["type"] = "integer" } },
                }),
            Tool("network_set_profile", "Apply a guest-only delay profile (tc netem via 'su 0' on the disposable lab guest).", 
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["delay"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("none", "gprs", "edge", "umts") },
                        ["speed"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("full", "gsm", "hscsd", "gprs", "edge", "umts", "hsdpa") },
                        ["latencyMs"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 0,
                            ["maximum"] = 5000,
                            ["description"] = "Guest egress delay; 0 removes shaping. Defaults to the delay profile.",
                        },
                    },
                }),
            Tool("recipe_validate", "Validate a recorded recipe against this guest before any mutation.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["recipe"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("recipe"),
                }),
            Tool("recipe_export", "Export a recorded recipe with its redacted steps.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["recipeId"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("recipeId"),
                }),
            Tool("recipe_replay", "Replay a recorded recipe on the bound guest; every step re-checks scope and approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["recipe"] = new JsonObject { ["type"] = "string" },
                        ["runtime"] = new JsonObject { ["type"] = "object" },
                        ["approval"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("recipe"),
                }),
            Tool("snapshot_save", "Save a named snapshot on the bound guest.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["tag"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("tag"),
                }),
            Tool("snapshot_restore", "Restore the bound guest snapshot; requires a fresh approval code.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tag"] = new JsonObject { ["type"] = "string" },
                        ["approval"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("tag", "approval"),
                }),
            Tool("artifact_read", "Read an artifact scoped to this session.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["artifactId"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("artifactId"),
                }),
            Tool("device_cleanup", "Delete the disposable guest; requires a fresh approval code.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["approval"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("approval"),
                }),
        };
    }

    private static JsonObject Result(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

    private static async Task SendAsync(JsonObject message)
    {
        var json = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        lock (WriteGate)
        {
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string? ReadArg(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
