using System.Diagnostics;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>
/// Creates AVD definitions through the SDK's own avdmanager and then applies a
/// deterministic lab profile. The profile keeps two concurrent instances inside
/// the host's disk and memory budget and disables features Phase 0 does not
/// exercise (audio, SD card, device frame).
/// </summary>
public sealed class AvdProvisioner
{
    private static readonly (string Key, string Value)[] LabProfile =
    {
        ("disk.dataPartition.size", "2048M"),
        ("hw.sdCard", "no"),
        ("hw.keyboard", "yes"),
        ("hw.gpu.enabled", "yes"),
        ("hw.gpu.mode", "swiftshader_indirect"),
        ("userdata.useQcow2", "yes"),
        ("hw.cpu.ncore", "4"),
        ("hw.ramSize", "3072"),
        ("hw.audioInput", "no"),
        ("hw.audioOutput", "no"),
        ("fastboot.forceColdBoot", "yes"),
        ("fastboot.forceFastBoot", "no"),
        ("firstboot.saveToLocalSnapshot", "no"),
        ("firstboot.bootFromLocalSnapshot", "no"),
        ("firstboot.bootFromDownloadableSnapshot", "no"),
        ("showDeviceFrame", "no"),
        ("vm.heapSize", "256M"),
        ("test.delayAdbTillBootComplete", "1"),
    };

    private readonly SdkLocator _sdk;

    public AvdProvisioner(SdkLocator sdk)
    {
        _sdk = sdk;
    }

    public async Task CreateAsync(
        string avdName,
        string imagePackage,
        string deviceProfile,
        string avdHome,
        string gpuMode = "swiftshader_indirect",
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(avdHome);
        var imageId = SdkLocator.NormalizeImageId(imagePackage);

        var startInfo = new ProcessStartInfo(_sdk.AvdManager)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = avdHome,
        };
        foreach (var argument in new[]
                 {
                     "create", "avd",
                     "-n", avdName,
                     "-k", imageId,
                     "-d", deviceProfile,
                     "--force",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["ANDROID_AVD_HOME"] = avdHome;
        startInfo.Environment["ANDROID_SDK_ROOT"] = _sdk.SdkRoot;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start avdmanager.");
        }

        await process.StandardInput.WriteLineAsync("no").ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"avdmanager failed with exit code {process.ExitCode}.{Environment.NewLine}{output.Trim()}");
        }

        var configPath = Path.Combine(avdHome, avdName + ".avd", "config.ini");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                $"avdmanager reported success but {configPath} does not exist.{Environment.NewLine}{output.Trim()}");
        }

        ApplyLabProfile(configPath, gpuMode);
    }

    public void ApplyLabProfile(string configPath, string gpuMode)
    {
        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new List<string>();
        var overrides = LabProfile
            .Select(pair => pair.Key == "hw.gpu.mode" ? (pair.Key, gpuMode) : pair)
            .ToList();

        foreach (var (key, value) in overrides)
        {
            var index = lines.FindIndex(line =>
            {
                var separator = line.IndexOf('=');
                return separator > 0
                       && string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase);
            });

            if (index >= 0)
            {
                lines[index] = key + "=" + value;
            }
            else
            {
                lines.Add(key + "=" + value);
            }
        }

        File.WriteAllLines(configPath, lines);
    }

    public void Delete(string avdName, string avdHome)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(avdHome, avdName + ".ini"),
                     Path.Combine(avdHome, avdName + ".avd"),
                 })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
