using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EmulatorWorkspace.Core.Engine;

public static class ApkManifestReader
{
    public static async Task<JsonObject> ReadAsync(
        SdkLocator sdk, string apk, CancellationToken cancellationToken)
    {
        const string revision = "35.0.0";
        var aapt2 = Path.Combine(sdk.SdkRoot, "build-tools", revision, "aapt2.exe");
        if (!File.Exists(aapt2))
        {
            return Unavailable($"Android SDK Build-Tools {revision} is required for APK manifest inspection.");
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                aapt2, ["dump", "badging", Path.GetFullPath(apk)],
                timeout: TimeSpan.FromSeconds(8), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0
                ? ParseBadging(result.StandardOutput)
                : Unavailable($"AAPT2 could not inspect this APK (exit {result.ExitCode}).");
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or IOException or InvalidOperationException)
        {
            return Unavailable("AAPT2 did not complete APK manifest inspection.");
        }
    }

    public static JsonObject ParseBadging(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? FirstLine(string prefix) => lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        static string? Value(string? line, string pattern)
        {
            var match = Regex.Match(line ?? "", pattern, RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }

        var packageLine = FirstLine("package: ");
        var package = Value(packageLine, @"^package: name='([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)'");
        if (package is null)
        {
            return Unavailable("AAPT2 did not report a valid manifest package name.");
        }

        var nativeCode = FirstLine("native-code:") ?? "";
        var abis = Regex.Matches(nativeCode, @"'([A-Za-z0-9_-]{1,32})'")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(32);
        return new JsonObject
        {
            ["available"] = true,
            ["package"] = package,
            ["versionCode"] = Value(packageLine, @"\sversionCode='([0-9]{1,20})'"),
            ["minSdk"] = Value(FirstLine("minSdkVersion:") ?? FirstLine("sdkVersion:"), @"^[^:]+:'([A-Za-z0-9_]{1,32})'"),
            ["targetSdk"] = Value(FirstLine("targetSdkVersion:"), @"^targetSdkVersion:'([A-Za-z0-9_]{1,32})'"),
            ["launchableActivity"] = Value(FirstLine("launchable-activity:"), @"^launchable-activity: name='([A-Za-z0-9_.$]{1,256})'"),
            ["nativeAbis"] = new JsonArray(abis.Select(abi => (JsonNode?)JsonValue.Create(abi)).ToArray()),
        };
    }

    private static JsonObject Unavailable(string reason) => new()
    {
        ["available"] = false,
        ["reason"] = reason,
    };
}
