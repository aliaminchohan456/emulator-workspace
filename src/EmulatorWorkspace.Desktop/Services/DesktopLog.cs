using System.IO;

namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// Minimal file log for diagnosing desktop startup and capture failures. Writes
/// next to the supervisor log so both sides of a session can be compared.
/// </summary>
public static class DesktopLog
{
    private static readonly object Gate = new();
    private static readonly string Path = ResolvePath();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
    }

    private static string ResolvePath()
    {
        var root = Environment.GetEnvironmentVariable("EMULATOR_WORKSPACE_HOME");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmulatorWorkspace");
        }

        return System.IO.Path.Combine(root, "logs", "desktop.log");
    }
}
