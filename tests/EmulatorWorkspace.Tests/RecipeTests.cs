using System.Text.Json.Nodes;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
using Xunit;

namespace EmulatorWorkspace.Tests;

public sealed class RecipeServiceTests
{
    private static (RecipeService Service, TempWorkspace Temp) Create()
    {
        var temp = new TempWorkspace();
        return (new RecipeService(temp.Paths), temp);
    }

    [Fact]
    public void Recorded_text_is_replaced_by_a_runtime_reference()
    {
        var (service, temp) = Create();
        using var _ = temp;

        var recording = service.StartRecording("login", "guest-a", "tpl", new JsonObject());
        service.RegisterRuntimeValue(recording.Id, "login_pass", "s3cret-value");

        var step = service.Capture("guest-a", "input.text", new JsonObject
        {
            ["text"] = "s3cret-value",
            ["selector"] = new JsonObject { ["contentDescription"] = "login_password" },
        }, new JsonObject { ["typed"] = 12 });

        Assert.NotNull(step);
        Assert.Equal("${runtime:login_pass}", step!["args"]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("s3cret-value", step.ToJsonString());
    }

    [Fact]
    public void Unregistered_text_is_never_stored()
    {
        var (service, temp) = Create();
        using var _ = temp;

        var recording = service.StartRecording("login", "guest-a", "tpl", new JsonObject());
        var step = service.Capture("guest-a", "input.text", new JsonObject { ["text"] = "plain-secret" }, null);

        Assert.NotNull(step);
        var text = step!["args"]!["text"]!.GetValue<string>();
        Assert.StartsWith("${runtime:unresolved_", text);
        Assert.DoesNotContain("plain-secret", step.ToJsonString());
    }

    [Fact]
    public void Approval_codes_and_host_paths_are_dropped_and_frames_become_observations()
    {
        var (service, temp) = Create();
        using var _ = temp;

        var recording = service.StartRecording("login", "guest-a", "tpl", new JsonObject());
        var step = service.Capture("guest-a", "input.tap", new JsonObject
        {
            ["frameId"] = "obs-1234",
            ["x"] = 540,
            ["y"] = 700,
            ["approval"] = "deadbeef",
            ["path"] = @"C:\Users\someone\secret",
        }, null);

        Assert.NotNull(step);
        var args = step!["args"]!.AsObject();
        Assert.False(args.ContainsKey("approval"));
        Assert.False(args.ContainsKey("path"));
        Assert.False(args.ContainsKey("frameId"));
        Assert.True(args["needsObservation"]!.GetValue<bool>());
    }

    [Fact]
    public void Unsupported_schema_and_operation_are_rejected_before_execution()
    {
        var (service, temp) = Create();
        using var _ = temp;

        var badSchema = service.Validate(new JsonObject
        {
            ["schemaVersion"] = 99,
            ["steps"] = new JsonArray(),
        });
        Assert.False(badSchema.Valid);
        Assert.Contains(badSchema.Errors, error => error.Contains("schema", StringComparison.OrdinalIgnoreCase));

        var badOperation = service.Validate(new JsonObject
        {
            ["schemaVersion"] = RecipeService.SchemaVersion,
            ["steps"] = new JsonArray(new JsonObject { ["op"] = "host.exec", ["args"] = new JsonObject() }),
        });
        Assert.False(badOperation.Valid);
        Assert.Contains(badOperation.Errors, error => error.Contains("host.exec", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_runtime_value_fails_with_a_typed_error()
    {
        var error = Assert.Throws<SupervisorOperationException>(() =>
            RecipeService.ResolveRuntime(
                new JsonObject { ["text"] = "${runtime:login_pass}" },
                new Dictionary<string, string>()));

        Assert.Equal("recipe_missing_runtime_value", error.Code);
    }

    [Fact]
    public void Exported_recipe_keeps_only_safe_references()
    {
        var (service, temp) = Create();
        using var _ = temp;

        var recording = service.StartRecording("login", "guest-a", "tpl", new JsonObject
        {
            ["imagePackage"] = "system-images;android-35;default;x86_64",
            ["deviceProfile"] = "pixel_6",
            ["configFingerprint"] = "abc",
        });
        service.RegisterRuntimeValue(recording.Id, "login_user", "qa-user");
        service.Capture("guest-a", "input.text", new JsonObject { ["text"] = "qa-user" }, null);
        service.Capture("guest-a", "app.launch", new JsonObject { ["package"] = "dev.emulatorworkspace.sample" }, null);

        var stopped = service.StopRecording(recording.Id, "APKSHA");
        var exported = service.ExportRecipe(recording.Id);
        var text = exported["recipe"]!.ToJsonString();

        Assert.DoesNotContain("qa-user", text);
        Assert.Contains("${runtime:login_user}", text);
        Assert.Contains("dev.emulatorworkspace.sample", text);
        Assert.Equal(2, exported["recipe"]!["steps"]!.AsArray().Count);
        Assert.False(string.IsNullOrEmpty(stopped["sha256"]!.GetValue<string>()));
    }

    [Fact]
    public void Replay_plan_requires_a_compatible_target()
    {
        var (service, temp) = Create();
        using var _ = temp;

        var recipe = new JsonObject
        {
            ["schemaVersion"] = RecipeService.SchemaVersion,
            ["template"] = new JsonObject
            {
                ["imagePackage"] = "system-images;android-35;default;x86_64",
                ["deviceProfile"] = "pixel_6",
            },
            ["steps"] = new JsonArray(new JsonObject
            {
                ["op"] = "app.launch",
                ["args"] = new JsonObject { ["package"] = "dev.emulatorworkspace.sample" },
            }),
        };

        var compatible = service.Validate(recipe, new JsonObject
        {
            ["imagePackage"] = "system-images;android-35;default;x86_64",
            ["deviceProfile"] = "pixel_6",
        });
        Assert.True(compatible.Valid);

        var incompatible = service.Validate(recipe, new JsonObject
        {
            ["imagePackage"] = "system-images;android-34;default;x86_64",
            ["deviceProfile"] = "pixel_6",
        });
        Assert.False(incompatible.Valid);
        Assert.Contains(incompatible.Errors, error => error.Contains("system image", StringComparison.Ordinal));
    }

    [Fact]
    public void Baseline_needs_relaunch_when_the_recipe_does_not_install_the_fixture()
    {
        var steps = new JsonArray(
            new JsonObject { ["op"] = "app.install", ["args"] = new JsonObject { ["path"] = "sample" } },
            new JsonObject { ["op"] = "app.launch", ["args"] = new JsonObject { ["package"] = "dev.emulatorworkspace.sample" } },
            new JsonObject { ["op"] = "snapshot.save", ["args"] = new JsonObject { ["tag"] = "recipe-baseline" } },
            new JsonObject { ["op"] = "input.text", ["args"] = new JsonObject { ["text"] = "x" } });

        // The baseline contains the fixture, and no later step installs or
        // launches it, so the runner must bring it back.
        Assert.True(RecipeService.NeedsFixtureRelaunch(steps, 2));
    }

    [Fact]
    public void Baseline_does_not_need_relaunch_when_the_recipe_installs_the_fixture_afterwards()
    {
        var steps = new JsonArray(
            new JsonObject { ["op"] = "snapshot.save", ["args"] = new JsonObject { ["tag"] = "recipe-baseline" } },
            new JsonObject { ["op"] = "app.install", ["args"] = new JsonObject { ["path"] = "sample" } },
            new JsonObject { ["op"] = "app.launch", ["args"] = new JsonObject { ["package"] = "dev.emulatorworkspace.sample" } });

        Assert.False(RecipeService.NeedsFixtureRelaunch(steps, 0));
    }

    [Fact]
    public void Unsupported_operations_are_rejected_as_recipe_steps()
    {
        Assert.False(RecipeService.IsSupportedOperation("host.exec"));
        Assert.True(RecipeService.IsSupportedOperation("input.tap"));
    }
}
