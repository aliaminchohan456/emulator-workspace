using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>
/// A launched child process with its output captured. Structured argument
/// lists are mandatory across this project so that paths containing spaces or
/// quotes can never change the meaning of a command line.
/// </summary>
public sealed class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(effectiveTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"'{Path.GetFileName(fileName)}' did not exit within {effectiveTimeout.TotalSeconds:N0}s.");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Combined => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;

    public void EnsureSuccess(string what)
    {
        if (ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{what} failed with exit code {ExitCode}.{Environment.NewLine}{Combined.Trim()}");
        }
    }
}

/// <summary>Resolves the installed Android SDK and pinned emulator binaries.</summary>
public sealed class SdkLocator
{
    private static readonly Regex VersionPattern = new(@"Android emulator version\s+([0-9][^\s]*)", RegexOptions.Compiled);

    public SdkLocator(string sdkRoot)
    {
        SdkRoot = Path.GetFullPath(sdkRoot);
    }

    public string SdkRoot { get; }

    public string EmulatorExe => Path.Combine(SdkRoot, "emulator", "emulator.exe");

    public string AvdManager => Path.Combine(SdkRoot, "cmdline-tools", "latest", "bin", "avdmanager.bat");

    public string AdbExe => Path.Combine(SdkRoot, "platform-tools", "adb.exe");

    public string EmulatorCheckExe => Path.Combine(SdkRoot, "emulator", "emulator-check.exe");

    public static SdkLocator Resolve(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return new SdkLocator(explicitRoot);
        }

        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                return new SdkLocator(value);
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(localAppData, "Android", "Sdk");
        if (Directory.Exists(candidate))
        {
            return new SdkLocator(candidate);
        }

        throw new InvalidOperationException(
            "No Android SDK found. Set ANDROID_SDK_ROOT or pass --sdk <path>.");
    }

    public void EnsureToolsPresent()
    {
        foreach (var (label, path) in new[]
                 {
                     ("emulator", EmulatorExe),
                     ("adb", AdbExe),
                     ("avdmanager", AvdManager),
                 })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Required SDK tool '{label}' was not found at {path}.", path);
            }
        }
    }

    public async Task<string> GetEmulatorVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            EmulatorExe,
            new[] { "-version" },
            timeout: TimeSpan.FromSeconds(60),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var match = VersionPattern.Match(result.Combined);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "Could not parse the emulator version from 'emulator -version'." +
                Environment.NewLine + result.Combined.Trim());
        }

        return match.Groups[1].Value;
    }

    /// <summary>The engine's own accelerator probe is the authoritative verdict.</summary>
    public async Task<string> GetAccelerationVerdictAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                EmulatorCheckExe,
                new[] { "accel" },
                timeout: TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var lines = result.Combined
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Contains("WHPX", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("WHP", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("Hypervisor", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("accel", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return lines.Count > 0 ? string.Join(" | ", lines) : result.Combined.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return "emulator-check unavailable: " + ex.Message;
        }
    }

    public static string NormalizeImageId(string image)
    {
        // Accept both the canonical sdkmanager id (semicolons) and the
        // filesystem-style id used by the newer Android CLI.
        var normalized = image.Replace('/', ';').Trim(';');
        var parts = normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || parts[0] != "system-images")
        {
            throw new ArgumentException(
                $"Image must look like 'system-images;android-35;default;x86_64'. Got '{image}'.",
                nameof(image));
        }

        return string.Join(';', parts);
    }

    public static string ImageRelativePath(string image)
    {
        var parts = NormalizeImageId(image).Split(';');
        return Path.Combine(parts[1], parts[2], parts[3]);
    }

    public string ImageDirectory(string image) => Path.Combine(SdkRoot, "system-images", ImageRelativePath(image));

    public bool IsImageInstalled(string image) =>
        File.Exists(Path.Combine(ImageDirectory(image), "system.img"));

    public string ImageRevision(string image)
    {
        var properties = Path.Combine(ImageDirectory(image), "source.properties");
        if (!File.Exists(properties))
        {
            return "unknown";
        }

        foreach (var line in File.ReadAllLines(properties))
        {
            if (line.StartsWith("Pkg.Revision=", StringComparison.Ordinal))
            {
                return line["Pkg.Revision=".Length..].Trim();
            }
        }

        return "unknown";
    }

    public string FormatImageId(string image)
    {
        var parts = NormalizeImageId(image).Split(';');
        return string.Join(';', parts);
    }

    public static string DescribeApiLevel(string image)
    {
        var parts = NormalizeImageId(image).Split(';');
        return parts[1].Replace("android-", "API ", StringComparison.OrdinalIgnoreCase);
    }

    public static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
