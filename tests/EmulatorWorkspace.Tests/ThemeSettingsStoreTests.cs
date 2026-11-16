using System.IO;
using EmulatorWorkspace.Desktop.Services;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-17: the theme choice is the one persisted preference. These cover the
/// store's contract on its own: a round trip for every writable mode, the three
/// failure shapes (missing, corrupt, unknown) that must fall back instead of
/// throwing, and the atomic write that may not leave a temp file behind.
/// </summary>
public class ThemeSettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "ux17-theme-" + Guid.NewGuid().ToString("N"));

    public ThemeSettingsStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SettingsFile => Path.Combine(_folder, ThemeSettingsStore.FileName);

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.System)]
    public void SaveThenLoad_RoundTripsTheChoice(ThemeMode mode)
    {
        var log = new List<string>();

        Assert.True(ThemeSettingsStore.Save(SettingsFile, mode, log.Add));
        Assert.Equal(mode, ThemeSettingsStore.Load(SettingsFile, log.Add));

        Assert.Contains(log, line => line.Contains("saved", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(log, line => line.Contains("unreadable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_WritesUtf8WithoutABom()
    {
        Assert.True(ThemeSettingsStore.Save(SettingsFile, ThemeMode.Light));

        var bytes = File.ReadAllBytes(SettingsFile);
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        // The documented shape stays human-editable.
        Assert.Contains("\"theme\": \"Light\"", File.ReadAllText(SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_CreatesTheFolderWhenMissing()
    {
        var nested = Path.Combine(_folder, "EmulatorWorkspace", ThemeSettingsStore.FileName);

        Assert.True(ThemeSettingsStore.Save(nested, ThemeMode.Dark));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Save_ReplacesThePreviousFileAndLeavesNoTempFile()
    {
        Assert.True(ThemeSettingsStore.Save(SettingsFile, ThemeMode.Dark));
        Assert.True(ThemeSettingsStore.Save(SettingsFile, ThemeMode.Light));

        Assert.Equal(ThemeMode.Light, ThemeSettingsStore.Load(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".tmp"));
    }

    [Fact]
    public void MissingFile_FallsBackToFollowWindows_AndLogsOneLine()
    {
        var log = new List<string>();

        Assert.Equal(ThemeMode.System, ThemeSettingsStore.Load(SettingsFile, log.Add));

        Assert.Single(log);
        Assert.Contains("not found", log[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptFile_FallsBackToFollowWindows_AndLogsOneLine()
    {
        File.WriteAllText(SettingsFile, "{ \"theme\": ");
        var log = new List<string>();

        Assert.Equal(ThemeMode.System, ThemeSettingsStore.Load(SettingsFile, log.Add));

        Assert.Single(log);
        Assert.Contains("unreadable", log[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownValue_FallsBackToFollowWindows_AndLogsOneLine()
    {
        File.WriteAllText(SettingsFile, "{ \"theme\": \"Sepia\" }");
        var log = new List<string>();

        Assert.Equal(ThemeMode.System, ThemeSettingsStore.Load(SettingsFile, log.Add));

        Assert.Single(log);
        Assert.Contains("unknown theme", log[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sepia", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FileWithoutAThemeString_FallsBackToFollowWindows()
    {
        File.WriteAllText(SettingsFile, "{ \"scale\": 1.5 }");
        var log = new List<string>();

        Assert.Equal(ThemeMode.System, ThemeSettingsStore.Load(SettingsFile, log.Add));
        Assert.Single(log);
    }

    [Fact]
    public void StoredHighContrastIsAPolicy_NotAPinnedTheme()
    {
        // High Contrast always overrides whatever is stored, so pinning it in the
        // file would leave a machine stuck in High Contrast after the Windows
        // policy is switched off. It resolves to "follow Windows" instead.
        File.WriteAllText(SettingsFile, "{ \"theme\": \"HighContrast\" }");
        var log = new List<string>();

        Assert.Equal(ThemeMode.System, ThemeSettingsStore.Load(SettingsFile, log.Add));
        Assert.Single(log);
        Assert.Contains("policy", log[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandEditedFile_ToleratesCommentsAndTrailingCommas()
    {
        File.WriteAllText(SettingsFile, "{\n  // my theme\n  \"theme\": \"Light\",\n}\n");

        Assert.Equal(ThemeMode.Light, ThemeSettingsStore.Load(SettingsFile));
    }

    [Fact]
    public void UnwritableDestination_ReturnsFalseWithoutThrowingAndCleansTheTemp()
    {
        // A directory at the destination path makes the final replace fail.
        var blocked = Path.Combine(_folder, "blocked");
        Directory.CreateDirectory(blocked);
        var log = new List<string>();

        Assert.False(ThemeSettingsStore.Save(blocked, ThemeMode.Dark, log.Add));

        Assert.Single(log);
        Assert.Contains("could not save", log[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(blocked + ".tmp"));
        Assert.True(Directory.Exists(blocked));
    }

    [Theory]
    [InlineData("dark", ThemeMode.Dark)]
    [InlineData("Light", ThemeMode.Light)]
    [InlineData(" System ", ThemeMode.System)]
    [InlineData("Follow Windows", ThemeMode.System)]
    public void TryParse_AcceptsTheStoredSpellings(string value, ThemeMode expected)
    {
        Assert.True(ThemeSettingsStore.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void DefaultPath_IsThePerUserWorkspaceFolder()
    {
        var path = ThemeSettingsStore.DefaultPath;

        Assert.Equal(ThemeSettingsStore.FileName, Path.GetFileName(path));
        Assert.Equal("EmulatorWorkspace", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Name_IsWhatLoadReadsBack()
    {
        foreach (var mode in new[] { ThemeMode.System, ThemeMode.Dark, ThemeMode.Light })
        {
            Assert.True(ThemeSettingsStore.TryParse(ThemeSettingsStore.Name(mode), out var parsed));
            Assert.Equal(mode, parsed);
        }
    }
}
