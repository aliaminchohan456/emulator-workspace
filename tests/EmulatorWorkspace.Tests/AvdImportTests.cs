using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Storage;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Core.Workspace;
using Xunit;

namespace EmulatorWorkspace.Tests;

public sealed class AvdImportTests
{
    private static (DeviceLibraryService Service, TempWorkspace Temp) Create()
    {
        var temp = new TempWorkspace();
        var sdk = new SdkLocator(temp.Root);
        using var store = new SupervisorStore(temp.Paths);
        var manager = new InstanceManager(temp.Paths, sdk, store, new SupervisorEventHub());
        return (new DeviceLibraryService(temp.Paths, manager), temp);
    }

    private static string CreateExternalAvd(TempWorkspace temp, string name, bool withConfig = true)
    {
        var root = Path.Combine(temp.Root, "external", name);
        Directory.CreateDirectory(root);
        if (withConfig)
        {
            File.WriteAllLines(Path.Combine(root, "config.ini"), new[]
            {
                "AvdId=external-avd",
                "avd.ini.displayname=" + name,
                "hw.device.name=pixel_6",
                "hw.ramSize=3072",
                "image.sysdir.1=android-35\\default\\x86_64",
                "disk.dataPartition.size=2048M",
            });
        }

        return root;
    }

    [Fact]
    public void Preview_reports_the_source_fingerprint_without_writing_to_it()
    {
        var (service, temp) = Create();
        using var _ = temp;
        var source = CreateExternalAvd(temp, "ext1");

        var plan = service.ValidateAvdImport(source, "imported");

        Assert.Equal("imported", plan["targetName"]!.GetValue<string>());
        Assert.Equal(true, plan["readOnly"]!.GetValue<bool>());
        Assert.NotEmpty(plan["sourceFingerprint"]!.GetValue<string>());
        Assert.Equal(0, plan["sourceWritableDiskBytes"]!.GetValue<long>());
        // The source is untouched.
        Assert.True(File.Exists(Path.Combine(source, "config.ini")));
    }

    [Fact]
    public void A_source_inside_the_workspace_is_refused_as_already_managed()
    {
        var (service, temp) = Create();
        using var _ = temp;
        var managed = Path.Combine(temp.Paths.AvdHome, "ws_managed.avd");
        Directory.CreateDirectory(managed);
        File.WriteAllLines(Path.Combine(managed, "config.ini"), new[] { "AvdId=managed" });

        var error = Assert.Throws<SupervisorOperationException>(() =>
            service.ValidateAvdImport(managed, "imported"));

        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
        Assert.Contains("already belongs to this workspace", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_without_config_is_refused()
    {
        var (service, temp) = Create();
        using var _ = temp;
        var source = CreateExternalAvd(temp, "ext2", withConfig: false);

        var error = Assert.Throws<SupervisorOperationException>(() =>
            service.ValidateAvdImport(source, "imported"));

        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public void A_missing_source_is_refused()
    {
        var (service, temp) = Create();
        using var _ = temp;

        var error = Assert.Throws<SupervisorOperationException>(() =>
            service.ValidateAvdImport(Path.Combine(temp.Root, "nope"), "imported"));

        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }

    [Fact]
    public void A_source_with_a_reparse_point_is_refused()
    {
        var (service, temp) = Create();
        using var _ = temp;
        var source = CreateExternalAvd(temp, "ext3");
        var link = Path.Combine(source, "link-target");
        try
        {
            Directory.CreateDirectory(link);
            File.CreateSymbolicLink(Path.Combine(source, "linked"), link);
        }
        catch (Exception)
        {
            return; // symlink creation is not permitted on this host
        }

        var error = Assert.Throws<SupervisorOperationException>(() =>
            service.ValidateAvdImport(source, "imported"));

        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
    }
}
