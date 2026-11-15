using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-09: the shared consent dialog prints a digest it read from the selected file.
/// A file it cannot read says so instead of showing a digest, and a digest is never
/// invented. The typed-target gate is a display condition, so its "does this action
/// need a typed name" rule is pinned here too.
/// </summary>
public class ConsentDialogModelTests
{
    [Fact]
    public void HashText_PrintsTheFirstTwelveHexCharacters()
    {
        var artifact = new ConsentArtifact(
            "Ramdisk",
            "ramdisk.img",
            @"C:\lab\ramdisk.img",
            "0123456789abcdeffedcba9876543210");
        Assert.Equal("sha256 0123456789ab…", artifact.HashText);
        Assert.Equal("sha256 0123456789abcdeffedcba9876543210", artifact.HashTip);
    }

    [Fact]
    public void HashText_AdmitsAFileItCouldNotRead()
    {
        var artifact = new ConsentArtifact("Manager APK", "magisk.apk", @"C:\lab\magisk.apk", "");
        Assert.Equal("sha256 not read", artifact.HashText);
        Assert.Contains("could not be read", artifact.HashTip);
    }

    [Fact]
    public async Task ReadAsync_HashesTheRealFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "ux09-consent-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllTextAsync(path, "magisk-lab");
        try
        {
            var artifact = await ConsentArtifact.ReadAsync("Ramdisk", path);

            Assert.Equal("Ramdisk", artifact.Role);
            Assert.Equal(Path.GetFileName(path), artifact.FileName);
            // SHA-256 of the UTF-8 bytes "magisk-lab", computed independently.
            Assert.Equal("01e0262fec8584a4ce808cfb40416225b9121c0b64cdcb57eba24a31bf7c3d63", artifact.Hash);
            Assert.StartsWith("sha256 ", artifact.HashTip);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_KeepsAnUnreadablePathAsAnEmptyHash()
    {
        var path = Path.Combine(Path.GetTempPath(), "ux09-missing-" + Guid.NewGuid().ToString("N") + ".img");

        var artifact = await ConsentArtifact.ReadAsync("Root probe", path);

        Assert.Equal("", artifact.Hash);
        Assert.Equal("sha256 not read", artifact.HashText);
    }

    [Fact]
    public void RequiresTypedTarget_FollowsTheConfirmWord()
    {
        var typed = Model("ag-714499");
        var acknowledgementOnly = Model("");

        Assert.True(typed.RequiresTypedTarget);
        Assert.False(acknowledgementOnly.RequiresTypedTarget);
    }

    [Fact]
    public void FooterNote_IsTheHonestSentenceByDefault()
    {
        var model = Model("ag-714499");
        Assert.Equal("Cancel creates no transaction or journal.", model.FooterNote);
    }

    private static ConsentDialogModel Model(string confirmWord) => new()
    {
        Title = "Title",
        Subtitle = "Subtitle",
        GuestName = "ag-714499",
        GuestMeta = "emulator-5556 · x86_64",
        AcknowledgeText = "I understand.",
        ConfirmWord = confirmWord,
        ConfirmLabel = "Confirm",
    };
}
