using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

public sealed class SupervisorLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ws-locator-" + Guid.NewGuid().ToString("N"));
    public SupervisorLocatorTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "EmulatorWorkspace.slnx"), "");
    }
    private string Add(string configuration, string extension = "exe")
    {
        var file = Path.Combine(_root, "src", "EmulatorWorkspace.Supervisor", "bin", configuration,
            "net10.0-windows", "EmulatorWorkspace.Supervisor." + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "fixture, never executed");
        return file;
    }
    [Fact] public void ReleaseWinsWhenBothBuildsExist()
    {
        Add("Debug"); var release = Add("Release");
        Assert.Equal(release, SupervisorLocator.ResolveLaunchTargetFrom(_root, null).FileName);
    }
    [Fact] public void ReleaseDllWinsOverDebugExecutable()
    {
        Add("Debug"); var release = Add("Release", "dll");
        var target = SupervisorLocator.ResolveLaunchTargetFrom(_root, null);
        Assert.Equal("dotnet", target.FileName);
        Assert.Equal(release, Assert.Single(target.Args));
    }
    [Fact] public void InvalidExplicitPathDoesNotSilentlyFallBack()
    {
        Add("Release");
        Assert.Throws<SupervisorUnavailableException>(() => SupervisorLocator.ResolveLaunchTargetFrom(_root, Path.Combine(_root, "missing.exe")));
    }
    [Fact] public void ExplicitAndPackagedTargetsTakePrecedence()
    {
        var debug = Add("Debug"); Add("Release");
        Assert.Equal(debug, SupervisorLocator.ResolveLaunchTargetFrom(_root, debug).FileName);
        var packaged = Path.Combine(_root, "EmulatorWorkspace.Supervisor.exe"); File.WriteAllText(packaged, "fixture");
        Assert.Equal(packaged, SupervisorLocator.ResolveLaunchTargetFrom(_root, null).FileName);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
