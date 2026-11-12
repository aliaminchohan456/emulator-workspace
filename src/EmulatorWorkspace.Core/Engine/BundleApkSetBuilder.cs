using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;

namespace EmulatorWorkspace.Core.Engine;

public static class BundleApkSetBuilder
{
    private const string BundletoolVersion = "1.18.1";
    private const string BundletoolSha256 = "675786493983787FFA11550BDB7C0715679A44E1643F3FF980A529E9C822595C";

    public static async Task<JsonObject> BuildAsync(
        string bundle, string? bundletool, string evidenceDirectory, bool testSigning, CancellationToken cancellationToken)
    {
        if (!testSigning)
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Choose Android debug test signing explicitly.");
        }

        bundle = Path.GetFullPath(bundle);
        if (!Path.GetExtension(bundle).Equals(".aab", StringComparison.OrdinalIgnoreCase) || !File.Exists(bundle))
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Select an existing Android App Bundle (.aab).");
        }

        bundletool = ResolveBundletool(bundletool);
        var java = ResolveJava();
        var key = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "debug.keystore");
        if (!File.Exists(key))
        {
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable,
                "APK set test signing requires the Android debug keystore at .android/debug.keystore.");
        }

        RejectReparsePoints(bundle);
        RejectReparsePoints(key);
        RejectReparsePoints(evidenceDirectory);
        await using var jarRead = await OpenPinnedBundletoolAsync(bundletool, cancellationToken).ConfigureAwait(false);
        await using var bundleRead = File.Open(bundle, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var keyRead = File.Open(key, FileMode.Open, FileAccess.Read, FileShare.Read);
        var inputHash = Convert.ToHexString(await SHA256.HashDataAsync(bundleRead, cancellationToken).ConfigureAwait(false));
        var keyHash = Convert.ToHexString(await SHA256.HashDataAsync(keyRead, cancellationToken).ConfigureAwait(false));
        var output = Path.Combine(evidenceDirectory, $"test-apks-{Guid.NewGuid():N}.apks");
        var temporary = output + ".partial.apks";
        try
        {
            var result = await ProcessRunner.RunAsync(java,
                ["-jar", bundletool, "build-apks", $"--bundle={bundle}", $"--output={temporary}",
                    $"--ks={key}", "--ks-pass=pass:android", "--ks-key-alias=androiddebugkey", "--key-pass=pass:android"],
                timeout: TimeSpan.FromMinutes(4), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(temporary))
            {
                throw new SupervisorOperationException(ErrorCodes.InstallFailed,
                    $"bundletool could not generate the APK set (exit {result.ExitCode}). Check that the bundle is valid and the debug key uses the standard Android test alias and passwords.");
            }

            using (var zip = ZipFile.OpenRead(temporary))
            {
                if (zip.GetEntry("toc.pb") is null || !zip.Entries.Any(entry => entry.Name.EndsWith(".apk", StringComparison.Ordinal)))
                {
                    throw new SupervisorOperationException(ErrorCodes.InstallFailed, "The APK set is missing its table of contents or APK files.");
                }
            }

            string outputHash;
            await using (var stream = File.OpenRead(temporary))
            {
                outputHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output);
            return new JsonObject
            {
                ["path"] = output,
                ["bytes"] = new FileInfo(output).Length,
                ["sha256"] = outputHash,
                ["bundleSha256"] = inputHash,
                ["signing"] = "android-debug-test",
                ["keystoreSha256"] = keyHash,
                ["bundletoolVersion"] = BundletoolVersion,
            };
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or Win32Exception or InvalidDataException)
        {
            throw new SupervisorOperationException(ErrorCodes.InstallFailed, "APK set generation did not finish: " + ex.Message);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static async Task<(string Bundletool, string[] Splits)> ExtractForDeviceAsync(
        string apkSet, string? bundletool, string scratch, string adb, string serial, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(apkSet).Equals(".apks", StringComparison.OrdinalIgnoreCase) || !File.Exists(apkSet))
        {
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Select an existing APK set (.apks).");
        }

        bundletool = ResolveBundletool(bundletool ?? Environment.GetEnvironmentVariable("WS_BUNDLETOOL")
            ?? @"D:\Phase3Tools\bundletool-1.18.1.jar");
        var java = ResolveJava();
        RejectReparsePoints(apkSet);
        RejectReparsePoints(scratch);
        await using var jarRead = await OpenPinnedBundletoolAsync(bundletool, cancellationToken).ConfigureAwait(false);
        await using var setRead = File.Open(apkSet, FileMode.Open, FileAccess.Read, FileShare.Read);
        var device = Path.Combine(scratch, "device.json");
        var output = Path.Combine(scratch, "splits");
        Directory.CreateDirectory(output);
        try
        {
            using (var zip = new ZipArchive(setRead, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.GetEntry("toc.pb") is null || zip.Entries.Any(entry =>
                        Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(':') ||
                        entry.FullName.Split('/', '\\').Contains("..", StringComparer.Ordinal)))
                {
                    throw new SupervisorOperationException(ErrorCodes.InstallFailed, "The APK set has an invalid table of contents or archive path.");
                }
            }

            var spec = await ProcessRunner.RunAsync(java,
                ["-jar", bundletool, "get-device-spec", $"--adb={adb}", $"--device-id={serial}", $"--output={device}"],
                timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (spec.ExitCode != 0 || !File.Exists(device))
            {
                throw new SupervisorOperationException(ErrorCodes.InstallFailed,
                    $"bundletool could not read the managed guest's device spec (exit {spec.ExitCode}).");
            }

            var extract = await ProcessRunner.RunAsync(java,
                ["-jar", bundletool, "extract-apks", $"--apks={apkSet}", $"--device-spec={device}", $"--output-dir={output}"],
                timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken).ConfigureAwait(false);
            var splits = Directory.GetFiles(output, "*.apk");
            if (extract.ExitCode != 0 || splits.Length == 0)
            {
                throw new SupervisorOperationException(ErrorCodes.InstallFailed,
                    $"bundletool could not select APKs for the guest (exit {extract.ExitCode}).");
            }

            foreach (var split in splits)
            {
                RejectReparsePoints(split);
            }

            return (bundletool, splits);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or Win32Exception or InvalidDataException)
        {
            throw new SupervisorOperationException(ErrorCodes.InstallFailed, "APK set selection did not finish: " + ex.Message);
        }
    }

    private static string ResolveBundletool(string? bundletool)
    {
        bundletool ??= Environment.GetEnvironmentVariable("WS_BUNDLETOOL");
        if (string.IsNullOrWhiteSpace(bundletool) || !File.Exists(bundletool))
        {
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable,
                $"Configure the pinned bundletool {BundletoolVersion} jar with WS_BUNDLETOOL or --bundletool.");
        }

        return Path.GetFullPath(bundletool);
    }

    private static string ResolveJava()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var java = string.IsNullOrWhiteSpace(javaHome) ? "" : Path.Combine(javaHome, "bin", "java.exe");
        if (!File.Exists(java))
        {
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable, "Configure JAVA_HOME for the pinned bundletool.");
        }

        RejectReparsePoints(java);
        return java;
    }

    private static async Task<FileStream> OpenPinnedBundletoolAsync(string bundletool, CancellationToken cancellationToken)
    {
        RejectReparsePoints(bundletool);
        var stream = File.Open(bundletool, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != BundletoolSha256)
            {
                throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable, $"The jar does not match pinned bundletool {BundletoolVersion}.");
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "APK set paths cannot contain reparse points.");
            }
        }
    }
}
