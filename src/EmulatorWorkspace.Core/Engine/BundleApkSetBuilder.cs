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

        bundletool ??= Environment.GetEnvironmentVariable("WS_BUNDLETOOL");
        if (string.IsNullOrWhiteSpace(bundletool) || !File.Exists(bundletool))
        {
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable,
                $"Configure the pinned bundletool {BundletoolVersion} jar with WS_BUNDLETOOL or --bundletool.");
        }
        bundletool = Path.GetFullPath(bundletool);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var java = string.IsNullOrWhiteSpace(javaHome) ? "" : Path.Combine(javaHome, "bin", "java.exe");
        var key = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "debug.keystore");
        if (!File.Exists(java) || !File.Exists(key))
        {
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable,
                "APK set test signing requires JAVA_HOME and the Android debug keystore at .android/debug.keystore.");
        }

        RejectReparsePoints(bundle);
        RejectReparsePoints(bundletool);
        RejectReparsePoints(java);
        RejectReparsePoints(key);
        RejectReparsePoints(evidenceDirectory);
        await using var jarRead = File.Open(bundletool, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(await SHA256.HashDataAsync(jarRead, cancellationToken).ConfigureAwait(false)) != BundletoolSha256)
        {
            throw new SupervisorOperationException(ErrorCodes.CapabilityUnavailable, $"The jar does not match pinned bundletool {BundletoolVersion}.");
        }
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

    private static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "APK set build paths cannot contain reparse points.");
            }
        }
    }
}
