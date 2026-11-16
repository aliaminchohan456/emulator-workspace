using System.IO;
using System.Text;
using System.Text.Json;

namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// The three roles a Magisk lab install has, and the supervisor's pin key for
/// each one. The pins live in the supervisor (one authority), so the desktop
/// can answer "is this the right file?" without a second table of its own.
/// </summary>
public static class LabInputRoles
{
    /// <summary>The patched ramdisk image.</summary>
    public const string Ramdisk = "ramdisk";

    /// <summary>The pinned Magisk manager APK.</summary>
    public const string ManagerApk = "managerApk";

    /// <summary>The pinned root probe APK.</summary>
    public const string ProbeApk = "probeApk";

    /// <summary>Every role, in the order the operator is asked for them.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Ramdisk, ManagerApk, ProbeApk };

    /// <summary>The supervisor's pin key for one role.</summary>
    public static string PinKey(string role) => role switch
    {
        Ramdisk => "patched.img",
        ManagerApk => "manager.apk",
        ProbeApk => "probe.apk",
        _ => "",
    };

    /// <summary>What the operator is told to pick for one role.</summary>
    public static string PickerTitle(string role) => role switch
    {
        Ramdisk => "Select the verified Magisk lab ramdisk",
        ManagerApk => "Select the pinned Magisk manager APK",
        ProbeApk => "Select the lab app root probe APK",
        _ => "Select the lab input",
    };

    /// <summary>The role's name in the consent dialog.</summary>
    public static string ConsentRole(string role) => role switch
    {
        Ramdisk => "Ramdisk",
        ManagerApk => "Manager APK",
        ProbeApk => "Root probe",
        _ => "Lab input",
    };
}

/// <summary>
/// UX-18: the three files a Magisk lab install needs - the patched ramdisk,
/// the pinned Magisk manager APK and the pinned root probe APK.
/// </summary>
/// <param name="Ramdisk">The patched ramdisk image.</param>
/// <param name="ManagerApk">The pinned Magisk manager APK.</param>
/// <param name="ProbeApk">The pinned root probe APK.</param>
public sealed record LabInputs(string Ramdisk, string ManagerApk, string ProbeApk)
{
    /// <summary>No remembered file at all.</summary>
    public static LabInputs Empty { get; } = new("", "", "");

    /// <summary>True when all three files are remembered.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Ramdisk)
        && !string.IsNullOrWhiteSpace(ManagerApk)
        && !string.IsNullOrWhiteSpace(ProbeApk);

    /// <summary>The remembered path for one role, or an empty string.</summary>
    public string For(string role) => role switch
    {
        "ramdisk" => Ramdisk ?? "",
        "managerApk" => ManagerApk ?? "",
        "probeApk" => ProbeApk ?? "",
        _ => "",
    };

    /// <summary>Returns a copy with one role replaced.</summary>
    public LabInputs With(string role, string path) => role switch
    {
        "ramdisk" => this with { Ramdisk = path ?? "" },
        "managerApk" => this with { ManagerApk = path ?? "" },
        "probeApk" => this with { ProbeApk = path ?? "" },
        _ => this,
    };
}

/// <summary>
/// UX-18: the operator's Magisk lab inputs, remembered per Windows account in
/// %LOCALAPPDATA%\EmulatorWorkspace\lab-inputs.json.
///
/// The lab needs the same three files every time, and they are pinned: the
/// supervisor verifies their hashes before it stages anything. Asking the
/// operator to find the same three files again for every guest is the software
/// asking for work it already knows. The remembered paths are only a
/// convenience: the pins stay the authority, and a file that no longer exists
/// or no longer matches simply falls back to the file picker.
///
/// The file is one small object so it can be read and hand-edited without
/// tooling. Reading never throws: a missing, corrupt or unknown file is one
/// log line plus empty inputs. Writing is atomic (temp file in the same
/// folder, then a single replace), so an interrupted write leaves either the
/// old file or the new one, never a partial one.
///
/// No WPF types are used here, so the tests compile this file directly.
/// </summary>
public static class LabInputsStore
{
    /// <summary>The file name inside the workspace settings folder.</summary>
    public const string FileName = "lab-inputs.json";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// %LOCALAPPDATA%\EmulatorWorkspace\lab-inputs.json. The lab inputs are
    /// the operator's files on this machine, not the workspace's state, so they
    /// live in the user's local application data.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmulatorWorkspace",
        FileName);

    /// <summary>
    /// Reads the remembered inputs. Missing, unreadable, corrupt and unknown
    /// values all resolve to empty inputs and leave exactly one diagnostic line
    /// with the caller's logger.
    /// </summary>
    public static LabInputs Load(string path, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                log?.Invoke($"lab inputs: {path} not found; the operator will be asked for the files");
                return LabInputs.Empty;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            using var document = JsonDocument.Parse(text, ReadOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                log?.Invoke($"lab inputs: {path} is not an object; the operator will be asked for the files");
                return LabInputs.Empty;
            }

            var inputs = new LabInputs(
                Read(document.RootElement, "ramdisk"),
                Read(document.RootElement, "managerApk"),
                Read(document.RootElement, "probeApk"));
            log?.Invoke(inputs.IsComplete
                ? $"lab inputs: three files remembered from {path}"
                : $"lab inputs: {path} holds only part of the set; the rest will be asked for");
            return inputs;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException)
        {
            log?.Invoke($"lab inputs: {path} unreadable ({exception.GetType().Name}); the operator will be asked for the files");
            return LabInputs.Empty;
        }
    }

    /// <summary>
    /// Writes the remembered inputs. Returns false (and logs one line) instead
    /// of throwing, so a read-only or full profile cannot break the install
    /// button; the install itself still works with the chosen files.
    /// </summary>
    public static bool Save(string path, LabInputs inputs, Action<string>? log = null)
    {
        var temporary = path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json =
                "{\n"
                + $"  \"ramdisk\": {Quote(inputs.Ramdisk)},\n"
                + $"  \"managerApk\": {Quote(inputs.ManagerApk)},\n"
                + $"  \"probeApk\": {Quote(inputs.ProbeApk)}\n"
                + "}\n";

            // No BOM: the file is plain UTF-8 that any editor and every JSON
            // reader accepts without a leading byte.
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Same-volume replace: MoveFileEx(MOVEFILE_REPLACE_EXISTING) swaps
            // the directory entry, so a crash cannot leave a torn file.
            File.Move(temporary, path, overwrite: true);
            log?.Invoke($"lab inputs: saved to {path}");
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            TryDelete(temporary);
            log?.Invoke($"lab inputs: could not save {path} ({exception.GetType().Name}); the files still work for this session");
            return false;
        }
    }

    private static string Read(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private static string Quote(string? value) =>
        JsonSerializer.Serialize(value ?? "");

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
