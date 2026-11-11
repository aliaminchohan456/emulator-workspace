using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Versioned recipe recorder and validator.
///
/// A recipe is a redacted, replayable description of a guest scenario. The
/// recorder observes the operations that flow through the supervisor registry
/// for one managed guest, keeps only supported operation types with validated
/// arguments, replaces sensitive input text with runtime references and never
/// stores credentials, approval codes or host paths.
/// </summary>
public sealed class RecipeService
{
    public const int SchemaVersion = 1;
    public const int MaxSteps = 64;
    public const int DefaultStepTimeoutSeconds = 60;

    /// <summary>Operation types that may be part of a recipe and their stored arguments.</summary>
    private static readonly Dictionary<string, string[]> SupportedOperations = new(StringComparer.Ordinal)
    {
        ["app.install"] = new[] { "path" },
        ["app.launch"] = new[] { "package" },
        ["app.foreground"] = Array.Empty<string>(),
        ["input.text"] = new[] { "text", "selector" },
        ["input.tap"] = new[] { "selector", "frameId", "x", "y" },
        ["input.swipe"] = new[] { "x1", "y1", "x2", "y2", "durationMs" },
        ["input.key"] = new[] { "key" },
        ["ui.assert"] = new[] { "selector" },
        ["observation.screenshot"] = Array.Empty<string>(),
        ["observation.tree"] = Array.Empty<string>(),
        ["logs.read"] = new[] { "lines", "contains", "tag" },
        ["network.profile"] = new[] { "delay", "speed", "latencyMs" },
        ["sim.geoFix"] = new[] { "latitude", "longitude", "altitude" },
        ["sim.battery"] = new[] { "level", "charging" },
        ["display.rotate"] = new[] { "rotation" },
        ["snapshot.save"] = new[] { "tag" },
        ["snapshot.load"] = new[] { "tag" },
    };

    /// <summary>
    /// Required argument groups per operation. A step is valid when at least
    /// one group is completely present, so a selector tap and a coordinate tap
    /// are both legal forms of the same action.
    /// </summary>
    private static readonly Dictionary<string, string[][]> ArgumentGroups = new(StringComparer.Ordinal)
    {
        ["app.install"] = new[] { new[] { "path" } },
        ["app.launch"] = new[] { new[] { "package" } },
        ["app.foreground"] = Array.Empty<string[]>(),
        ["input.text"] = new[] { new[] { "text" } },
        ["input.tap"] = new[] { new[] { "selector" }, new[] { "x", "y" } },
        ["input.swipe"] = new[] { new[] { "x1", "y1", "x2", "y2" } },
        ["input.key"] = new[] { new[] { "key" } },
        ["ui.assert"] = new[] { new[] { "selector" } },
        ["observation.screenshot"] = Array.Empty<string[]>(),
        ["observation.tree"] = Array.Empty<string[]>(),
        ["logs.read"] = Array.Empty<string[]>(),
        ["network.profile"] = new[] { new[] { "delay" } },
        ["sim.geoFix"] = new[] { new[] { "latitude", "longitude" } },
        ["sim.battery"] = new[] { new[] { "level" } },
        ["display.rotate"] = new[] { new[] { "rotation" } },
        ["snapshot.save"] = new[] { new[] { "tag" } },
        ["snapshot.load"] = new[] { new[] { "tag" } },
    };

    /// <summary>Argument names that are never stored in a recipe.</summary>
    private static readonly HashSet<string> ForbiddenArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "approval", "token", "auth", "password", "secret", "path", "out", "file", "outDir", "keystore",
    };

    private readonly WorkspacePaths _paths;
    private readonly ConcurrentDictionary<string, Recording> _recordings = new(StringComparer.Ordinal);

    public RecipeService(WorkspacePaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(RecipeDirectory);
    }

    public string RecipeDirectory => Path.Combine(_paths.Root, "recipes");

    public sealed class Recording
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Instance { get; set; } = string.Empty;

        public string Template { get; set; } = string.Empty;

        public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;

        public List<JsonObject> Steps { get; } = new();

        public Dictionary<string, string> RuntimeValues { get; } = new(StringComparer.Ordinal);

        public JsonObject Guest { get; set; } = new();

        public JsonObject Baseline { get; set; } = new();

        public JsonObject App { get; set; } = new();

        public JsonObject Network { get; set; } = new();

        public string? Package { get; set; }

        public string? BaselineSnapshotTag { get; set; }
    }

    // ------------------------------------------------------------- recording

    public Recording StartRecording(string name, string instance, string template, JsonObject guest)
    {
        var recording = new Recording
        {
            Id = "rcp-" + Guid.NewGuid().ToString("N")[..10],
            Name = name,
            Instance = instance,
            Template = template,
            Guest = guest.DeepClone().AsObject(),
        };
        _recordings[recording.Id] = recording;
        return recording;
    }

    public Recording? FindRecording(string id) => _recordings.GetValueOrDefault(id);

    public Recording? ActiveRecordingFor(string instance) =>
        _recordings.Values.FirstOrDefault(recording =>
            recording.Instance.Equals(instance, StringComparison.OrdinalIgnoreCase));

    public void RegisterRuntimeValue(string recordingId, string name, string value)
    {
        if (!_recordings.TryGetValue(recordingId, out var recording))
        {
            throw new SupervisorOperationException(ErrorCodes.NotFound, $"No recipe recording '{recordingId}'.");
        }

        recording.RuntimeValues[name] = value;
    }

    /// <summary>
    /// Captures one completed operation into the active recording. Only
    /// supported operation types are stored, arguments are filtered and
    /// sensitive text is replaced by a runtime reference.
    /// </summary>
    public JsonObject? Capture(string instance, string op, JsonObject? args, JsonNode? result)
    {
        if (!SupportedOperations.ContainsKey(op))
        {
            return null;
        }

        var recording = ActiveRecordingFor(instance);
        if (recording is null || recording.Steps.Count >= MaxSteps)
        {
            return null;
        }

        var stored = new JsonObject
        {
            ["index"] = recording.Steps.Count,
            ["op"] = op,
            ["args"] = FilterArguments(op, args, recording),
            ["timeoutSeconds"] = DefaultStepTimeoutSeconds,
            ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        var expectation = BuildExpectation(op, args, result);
        if (expectation is not null)
        {
            stored["expect"] = expectation;
        }

        recording.Steps.Add(stored);
        if (op == "app.launch" && args?["package"] is { } package)
        {
            recording.Package = package.GetValue<string>();
        }

        if (op == "snapshot.save" && args?["tag"] is { } tag)
        {
            recording.BaselineSnapshotTag = tag.GetValue<string>();
        }

        if (op == "network.profile")
        {
            var profile = new JsonObject
            {
                ["delay"] = args?["delay"]?.DeepClone(),
                ["speed"] = args?["speed"]?.DeepClone(),
                ["latencyMs"] = args?["latencyMs"]?.DeepClone(),
            };

            if (recording.Network["original"] is null)
            {
                recording.Network["original"] = profile.DeepClone();
            }

            recording.Network["applied"] = profile.DeepClone();
        }

        return stored.DeepClone().AsObject();
    }

    private static JsonObject FilterArguments(string op, JsonObject? args, Recording recording)
    {
        var stored = new JsonObject();
        foreach (var key in SupportedOperations[op])
        {
            if (args is null || !args.TryGetPropertyValue(key, out var value) || value is null)
            {
                continue;
            }

            if (key == "path" && op == "app.install")
            {
                // Only the approved APK reference is stored; host paths never
                // enter a recipe.
                var reference = value.GetValue<string>();
                if (reference.Equals("sample", StringComparison.OrdinalIgnoreCase)
                    || reference.EndsWith("ws-sample.apk", StringComparison.OrdinalIgnoreCase))
                {
                    stored["path"] = "sample";
                }

                continue;
            }

            if (ForbiddenArguments.Contains(key))
            {
                continue;
            }

            if (key == "text")
            {
                var literal = value.GetValue<string>();
                var runtimeName = recording.RuntimeValues
                    .FirstOrDefault(pair => pair.Value == literal).Key;
                stored["text"] = runtimeName is null
                    ? "${runtime:unresolved_" + recording.Steps.Count + "}"
                    : "${runtime:" + runtimeName + "}";
                continue;
            }

            if (key == "frameId")
            {
                // Frame ids are guest-local and short lived. Coordinate input is
                // replayed from a fresh observation instead.
                stored["needsObservation"] = true;
                continue;
            }

            if (key == "selector")
            {
                stored["selector"] = value.DeepClone();
                continue;
            }

            stored[key] = value.DeepClone();
        }

        return stored;
    }

    private static JsonObject? BuildExpectation(string op, JsonObject? args, JsonNode? result)
    {
        if (op == "ui.assert")
        {
            var selector = args?["selector"] as JsonObject;
            var pass = result?["pass"]?.GetValue<bool>() ?? false;
            var text = result?["text"]?.GetValue<string>();
            return new JsonObject
            {
                ["type"] = "selectorVisible",
                ["selector"] = selector?.DeepClone(),
                ["pass"] = pass,
                ["text"] = text,
            };
        }

        if (op == "app.launch")
        {
            var package = args?["package"]?.GetValue<string>();
            return new JsonObject
            {
                ["type"] = "packageForeground",
                ["package"] = package,
            };
        }

        return null;
    }

    // ------------------------------------------------------------- exporting

    public JsonObject StopRecording(string recordingId, string? apkSha256)
    {
        if (!_recordings.TryRemove(recordingId, out var recording))
        {
            throw new SupervisorOperationException(ErrorCodes.NotFound, $"No recipe recording '{recordingId}'.");
        }

        var runtimeNames = recording.RuntimeValues.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
        var unresolved = new List<string>();
        foreach (var step in recording.Steps)
        {
            var text = step["args"]?["text"]?.GetValue<string>();
            if (text is not null && text.StartsWith("${runtime:unresolved_", StringComparison.Ordinal))
            {
                unresolved.Add(text);
            }
        }

        var recipe = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["recipeId"] = recording.Id,
            ["name"] = recording.Name,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["guest"] = recording.Guest.DeepClone(),
            ["template"] = new JsonObject
            {
                ["instance"] = recording.Template,
                ["imagePackage"] = recording.Guest["imagePackage"]?.DeepClone(),
                ["deviceProfile"] = recording.Guest["deviceProfile"]?.DeepClone(),
            },
            ["baseline"] = new JsonObject
            {
                ["snapshotTag"] = recording.BaselineSnapshotTag,
                ["configFingerprint"] = recording.Guest["configFingerprint"]?.DeepClone(),
                ["lineageId"] = recording.Guest["lineageId"]?.DeepClone(),
                ["required"] = recording.BaselineSnapshotTag is not null,
            },
            ["app"] = new JsonObject
            {
                ["package"] = recording.Package,
                ["apkSha256"] = apkSha256,
            },
            ["network"] = recording.Network.DeepClone(),
            ["runtime"] = new JsonObject
            {
                ["required"] = new JsonArray(runtimeNames.Select(name => (JsonNode)name!).ToArray()),
                ["unresolved"] = new JsonArray(unresolved.Select(value => (JsonNode)value!).ToArray()),
            },
            ["steps"] = new JsonArray(recording.Steps.Select(step => (JsonNode)step.DeepClone()).ToArray()),
        };

        var path = Path.Combine(RecipeDirectory, recording.Id + ".json");
        File.WriteAllText(
            path,
            recipe.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return new JsonObject
        {
            ["recipeId"] = recording.Id,
            ["path"] = path,
            ["steps"] = recording.Steps.Count,
            ["runtimeRequired"] = new JsonArray(runtimeNames.Select(name => (JsonNode)name!).ToArray()),
            ["unresolvedRuntime"] = new JsonArray(unresolved.Select(value => (JsonNode)value!).ToArray()),
            ["sha256"] = DeviceLibraryService.Hash(File.ReadAllText(path)),
        };
    }

    public JsonObject ExportRecipe(string recipeId)
    {
        var path = Path.Combine(RecipeDirectory, recipeId + ".json");
        if (!File.Exists(path))
        {
            throw new SupervisorOperationException(ErrorCodes.NotFound, $"No recipe '{recipeId}' in this workspace.");
        }

        JsonNode? parsedExport;
        try
        {
            parsedExport = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new SupervisorOperationException(
                ErrorCodes.RecipeInvalid, "The recipe file is not valid JSON: " + ex.Message);
        }

        var recipe = parsedExport as JsonObject
                     ?? throw new SupervisorOperationException(
                         ErrorCodes.RecipeInvalid, "The recipe file is not a JSON object.");
        return new JsonObject
        {
            ["recipeId"] = recipeId,
            ["path"] = path,
            ["recipe"] = recipe,
            ["sha256"] = DeviceLibraryService.Hash(File.ReadAllText(path)),
        };
    }

    /// <summary>
    /// Reads a recipe from an explicit workspace-relative path. Absolute host
    /// paths and paths outside the workspace are refused so a recipe can never
    /// point at private host state.
    /// </summary>
    public JsonObject LoadRecipe(string pathOrId)
    {
        string path;
        if (pathOrId.Contains(Path.DirectorySeparatorChar) || pathOrId.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.GetFullPath(pathOrId);
            var workspace = Path.GetFullPath(_paths.Root) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
            {
                throw new SupervisorOperationException(
                    ErrorCodes.ForbiddenScope, "A recipe may only be read from inside the workspace root.");
            }
        }
        else
        {
            path = Path.Combine(RecipeDirectory, pathOrId + ".json");
        }

        if (!File.Exists(path))
        {
            throw new SupervisorOperationException(ErrorCodes.NotFound, $"No recipe file '{path}'.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new SupervisorOperationException(
                ErrorCodes.RecipeInvalid, "The recipe file is not valid JSON: " + ex.Message);
        }

        return parsed as JsonObject
               ?? throw new SupervisorOperationException(
                   ErrorCodes.RecipeInvalid, "The recipe file is not a JSON object.");
    }

    // ------------------------------------------------------------ validation

    public sealed record ValidationResult(bool Valid, JsonObject Report, IReadOnlyList<string> Errors);

    /// <summary>
    /// Validates the complete recipe before any mutation happens.
    /// </summary>
    public ValidationResult Validate(JsonObject recipe, JsonObject? targetGuest = null)
    {
        var errors = new List<string>();
        var report = new JsonObject();

        var schema = recipe["schemaVersion"]?.GetValue<int>() ?? 0;
        report["schemaVersion"] = schema;
        if (schema != SchemaVersion)
        {
            errors.Add($"unsupported schema version {schema}; expected {SchemaVersion}");
        }

        var steps = recipe["steps"] as JsonArray ?? new JsonArray();
        report["steps"] = steps.Count;
        if (steps.Count == 0)
        {
            errors.Add("the recipe has no steps");
        }

        if (steps.Count > MaxSteps)
        {
            errors.Add($"the recipe has {steps.Count} steps; the limit is {MaxSteps}");
        }

        var index = 0;
        foreach (var node in steps)
        {
            if (node is not JsonObject step)
            {
                errors.Add($"step {index} is not an object");
                index++;
                continue;
            }

            var op = step["op"]?.GetValue<string>() ?? "";
            if (!SupportedOperations.ContainsKey(op))
            {
                errors.Add($"step {index} uses unsupported operation '{op}'");
            }

            var args = step["args"] as JsonObject ?? new JsonObject();
            var groups = ArgumentGroups.TryGetValue(op, out var required) ? required : Array.Empty<string[]>();
            if (groups.Length > 0 && !groups.Any(group => group.All(name => args.ContainsKey(name))))
            {
                var expected = string.Join(" or ", groups.Select(group => string.Join("+", group)));
                errors.Add($"step {index} ('{op}') needs {expected}");
            }

            var timeout = step["timeoutSeconds"]?.GetValue<int>() ?? DefaultStepTimeoutSeconds;
            if (timeout is < 1 or > 900)
            {
                errors.Add($"step {index} has an out-of-range timeout");
            }

            index++;
        }

        var runtime = recipe["runtime"]?["required"] as JsonArray ?? new JsonArray();
        report["runtimeRequired"] = new JsonArray(
            runtime.Select(node => (JsonNode)(node?.GetValue<string>() ?? "")).ToArray());

        if (targetGuest is not null)
        {
            var image = recipe["template"]?["imagePackage"]?.GetValue<string>();
            var profile = recipe["template"]?["deviceProfile"]?.GetValue<string>();
            if (image is not null && targetGuest["imagePackage"]?.GetValue<string>() != image)
            {
                errors.Add("the target guest uses a different system image than the recipe requires");
            }

            if (profile is not null && targetGuest["deviceProfile"]?.GetValue<string>() != profile)
            {
                errors.Add("the target guest uses a different device profile than the recipe requires");
            }

            report["targetCompatible"] = errors.Count == 0;
        }

        report["errors"] = new JsonArray(errors.Select(error => (JsonNode)error!).ToArray());
        return new ValidationResult(errors.Count == 0, report, errors);
    }

    /// <summary>
    /// Resolves runtime references in the step arguments. Values are supplied
    /// explicitly at replay time and never stored in the recipe.
    /// </summary>
    public static JsonObject ResolveRuntime(JsonObject args, IReadOnlyDictionary<string, string> values)
    {
        var resolved = new JsonObject();
        foreach (var property in args)
        {
            if (property.Value is JsonValue value
                && value.TryGetValue<string>(out var text)
                && text.StartsWith("${runtime:", StringComparison.Ordinal))
            {
                var name = text["${runtime:".Length..^1];
                if (!values.TryGetValue(name, out var replacement))
                {
                    throw new SupervisorOperationException(
                        "recipe_missing_runtime_value",
                        $"The runtime value '{name}' is required by this step but was not supplied.");
                }

                resolved[property.Key] = replacement;
                continue;
            }

            resolved[property.Key] = property.Value?.DeepClone();
        }

        return resolved;
    }

/// <summary>
    /// Decides whether a restored baseline needs its fixture relaunched. When
    /// the recipe installs or launches the fixture after the baseline, those
    /// recorded steps carry that work and the guest only has to be interactive
    /// again. A baseline that already contains the fixture must be brought back
    /// by the runner.
    /// </summary>
    public static bool NeedsFixtureRelaunch(JsonArray steps, int baselineIndex)
    {
        for (var index = baselineIndex + 1; index < steps.Count; index++)
        {
            var op = steps[index]?["op"]?.GetValue<string>() ?? "";
            if (op is "app.launch" or "app.install")
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSupportedOperation(string op) => SupportedOperations.ContainsKey(op);
}
