using EmulatorWorkspace.Desktop.Services;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-18: the remembered Magisk lab inputs.
///
/// The lab needs the same three files for every guest and the supervisor pins
/// their hashes. The operator picks them once; the window remembers them so
/// the next install is the consent dialog and nothing else.
/// </summary>
public class LabInputsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ws-lab-inputs-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_directory, "lab-inputs.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AMissingFile_ResolvesToNothingAndSaysSo()
    {
        var lines = new List<string>();

        var inputs = LabInputsStore.Load(Path_, lines.Add);

        Assert.Equal(LabInputs.Empty, inputs);
        Assert.False(inputs.IsComplete);
        Assert.Single(lines);
        Assert.Contains("not found", lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACorruptFile_ResolvesToNothingAndSaysSo()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, "{ this is not json");
        var lines = new List<string>();

        var inputs = LabInputsStore.Load(Path_, lines.Add);

        Assert.Equal(LabInputs.Empty, inputs);
        Assert.Single(lines);
    }

    [Fact]
    public void AFileThatIsNotAnObject_ResolvesToNothing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, "[1, 2, 3]");
        var lines = new List<string>();

        var inputs = LabInputsStore.Load(Path_, lines.Add);

        Assert.Equal(LabInputs.Empty, inputs);
        Assert.Single(lines);
    }

    [Fact]
    public void ThreeFiles_RoundTrip()
    {
        var inputs = new LabInputs(@"D:\lab\patched.img", @"D:\lab\Magisk.apk", @"D:\lab\probe.apk");

        Assert.True(LabInputsStore.Save(Path_, inputs));
        var loaded = LabInputsStore.Load(Path_);

        Assert.Equal(inputs, loaded);
        Assert.True(loaded.IsComplete);
    }

    [Fact]
    public void APartialSet_IsKeptSoOnlyTheMissingFileIsAskedFor()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, "{\"ramdisk\":\"D:\\\\lab\\\\patched.img\"}");

        var loaded = LabInputsStore.Load(Path_);

        Assert.Equal(@"D:\lab\patched.img", loaded.Ramdisk);
        Assert.Equal("", loaded.ManagerApk);
        Assert.False(loaded.IsComplete);
    }

    [Fact]
    public void TheSavedFileIsPlainUtf8WithoutABom()
    {
        Assert.True(LabInputsStore.Save(Path_, new LabInputs("a", "b", "c")));

        var bytes = File.ReadAllBytes(Path_);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("\"ramdisk\"", File.ReadAllText(Path_), StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveFailureDoesNotThrow()
    {
        // A path that cannot be created: the store reports it and the install
        // still works with the chosen files.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "file"), "not a directory");
        var blocked = Path.Combine(_directory, "file", "inputs.json");
        var lines = new List<string>();

        var saved = LabInputsStore.Save(blocked, new LabInputs("a", "b", "c"), lines.Add);

        Assert.False(saved);
        Assert.Single(lines);
    }

    [Fact]
    public void EveryRoleMapsToTheSupervisorsPinKey()
    {
        // The pins live in the supervisor; the desktop asks for the same three.
        Assert.Equal("patched.img", LabInputRoles.PinKey(LabInputRoles.Ramdisk));
        Assert.Equal("manager.apk", LabInputRoles.PinKey(LabInputRoles.ManagerApk));
        Assert.Equal("probe.apk", LabInputRoles.PinKey(LabInputRoles.ProbeApk));
        Assert.Equal(3, LabInputRoles.All.Count);
    }

    [Fact]
    public void ARoleCanBeReplacedWithoutTouchingTheOthers()
    {
        var inputs = new LabInputs("r", "m", "p");

        var updated = inputs.With(LabInputRoles.ManagerApk, "m2");

        Assert.Equal("r", updated.Ramdisk);
        Assert.Equal("m2", updated.ManagerApk);
        Assert.Equal("p", updated.ProbeApk);
    }
}
