using System.IO;
using System.Text;
using System.Text.Json;

namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// UX-17: the operator's theme choice, stored per Windows account in
/// %LOCALAPPDATA%\EmulatorWorkspace\settings.json.
///
/// The file is one small object (<c>{"theme":"Dark"}</c>) so it can be read and
/// hand-edited without tooling. Reading never throws: a missing, corrupt or
/// unknown file is one log line plus the shipped default. Writing is atomic
/// (temp file in the same folder, then a single replace), so an interrupted
/// write leaves either the old file or the new one, never a partial one.
///
/// No WPF types are used here, so the tests compile this file directly.
/// </summary>
public static class ThemeSettingsStore
{
    /// <summary>The file name inside the workspace settings folder.</summary>
    public const string FileName = "settings.json";

    private const string ThemeProperty = "theme";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// %LOCALAPPDATA%\EmulatorWorkspace\settings.json. The workspace root is a
    /// per-project location that a probe can move with --root; the theme belongs
    /// to the person, so it stays in the user's local application data.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmulatorWorkspace",
        FileName);

    /// <summary>
    /// Reads the saved choice. Missing, unreadable, corrupt and unknown values
    /// all resolve to <see cref="ThemeMode.System"/> (follow Windows) and leave
    /// exactly one diagnostic line with the caller's logger.
    /// </summary>
    public static ThemeMode Load(string path, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                log?.Invoke($"theme settings: {path} not found; following Windows");
                return ThemeMode.System;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            using var document = JsonDocument.Parse(text, ReadOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(ThemeProperty, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                log?.Invoke($"theme settings: {path} has no \"{ThemeProperty}\" string; following Windows");
                return ThemeMode.System;
            }

            var stored = (value.GetString() ?? "").Trim();

            // High Contrast is a Windows policy, not a pick: a hand-edited
            // "HighContrast" must not pin the theme after the policy is off.
            if (string.Equals(stored, "HighContrast", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stored, "High Contrast", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"theme settings: \"{stored}\" is a Windows policy; following Windows");
                return ThemeMode.System;
            }

            if (TryParse(stored, out var mode))
            {
                // One line for the evidence trail: which choice this launch used.
                log?.Invoke($"theme settings: {Name(mode)} from {path}");
                return mode;
            }

            log?.Invoke($"theme settings: unknown theme \"{stored}\" in {path}; following Windows");
            return ThemeMode.System;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException)
        {
            log?.Invoke($"theme settings: {path} unreadable ({exception.GetType().Name}); following Windows");
            return ThemeMode.System;
        }
    }

    /// <summary>
    /// Writes the choice. Returns false (and logs one line) instead of throwing,
    /// so a read-only or full profile cannot break a theme click; the choice
    /// still applies to the running session.
    /// </summary>
    public static bool Save(string path, ThemeMode mode, Action<string>? log = null)
    {
        var temporary = path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = $"{{\n  \"{ThemeProperty}\": \"{Name(mode)}\"\n}}\n";

            // No BOM: the file is plain UTF-8 that any editor and every JSON
            // reader accepts without a leading byte.
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Same-volume replace: MoveFileEx(MOVEFILE_REPLACE_EXISTING) swaps
            // the directory entry, so a crash cannot leave a torn file.
            File.Move(temporary, path, overwrite: true);
            log?.Invoke($"theme settings: saved {Name(mode)} to {path}");
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            TryDelete(temporary);
            log?.Invoke($"theme settings: could not save {path} ({exception.GetType().Name}); the choice applies to this session only");
            return false;
        }
    }

    /// <summary>The stored spelling of a mode (what <see cref="Load"/> reads back).</summary>
    public static string Name(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => "Dark",
        ThemeMode.Light => "Light",
        _ => "System",
    };

    /// <summary>Accepts the stored spelling, case-insensitively.</summary>
    public static bool TryParse(string value, out ThemeMode mode)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "dark":
                mode = ThemeMode.Dark;
                return true;
            case "light":
                mode = ThemeMode.Light;
                return true;
            case "system":
            case "follow windows":
            case "followwindows":
                mode = ThemeMode.System;
                return true;
            default:
                mode = ThemeMode.System;
                return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp file is overwritten by the next save.
        }
    }
}
