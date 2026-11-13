using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;
using Xunit;

namespace EmulatorWorkspace.Tests;

public sealed class AccelerationProbeTests
{
    [Theory]
    [InlineData(0, "0", "WHPX(10.0.26300) is installed and usable.", true)]
    [InlineData(0, "1", "WHPX(10.0.26300) is installed and usable.", false)]
    [InlineData(1, "0", "WHPX(10.0.26300) is installed and usable.", false)]
    [InlineData(0, "0", "WHPX(10.0.26300) is installed but not usable.", false)]
    [InlineData(0, "0", "AEHD is installed and usable.", false)]
    public void RequiresSuccessfulWhpxResult(int exitCode, string status, string verdict, bool expected)
    {
        var result = new ProcessResult(exitCode, $"accel:\r\n{status}\r\n{verdict}\r\naccel\r\n", "");
        Assert.Equal(expected, SdkLocator.IsWhpxUsable(result));
    }
}

public sealed class ApkManifestTests
{
    [Fact]
    public void ReportsDeclaredManifestFieldsAndDistinctNativeAbis()
    {
        var manifest = ApkManifestReader.ParseBadging("""
            package: name='dev.example.fixture' versionCode='12' versionName='1.2'
            minSdkVersion:'28'
            targetSdkVersion:'35'
            launchable-activity: name='dev.example.fixture.MainActivity' label='' icon=''
            native-code: 'arm64-v8a' 'x86_64' 'x86_64'
            """);

        Assert.True(manifest["available"]!.GetValue<bool>());
        Assert.Equal("dev.example.fixture", manifest["package"]!.GetValue<string>());
        Assert.Equal("28", manifest["minSdk"]!.GetValue<string>());
        Assert.Equal("35", manifest["targetSdk"]!.GetValue<string>());
        Assert.Equal("dev.example.fixture.MainActivity", manifest["launchableActivity"]!.GetValue<string>());
        Assert.Equal(new[] { "arm64-v8a", "x86_64" }, manifest["nativeAbis"]!.AsArray().Select(abi => abi!.GetValue<string>()));
    }

    [Fact]
    public void DistinguishesMissingNativeLibrariesFromAnUnavailableManifest()
    {
        var valid = ApkManifestReader.ParseBadging("package: name='dev.example.fixture' versionCode='1'");
        Assert.True(valid["available"]!.GetValue<bool>());
        Assert.Empty(valid["nativeAbis"]!.AsArray());

        var malformed = ApkManifestReader.ParseBadging("package: name='invalid package name'");
        Assert.False(malformed["available"]!.GetValue<bool>());
        Assert.Null(malformed["nativeAbis"]);
    }
}

public sealed class EngineArgumentTests
{
    private static InstanceRecord Record() => new()
    {
        Name = "alpha",
        AvdName = "ws_alpha",
        ImagePackage = "system-images;android-35;default;x86_64",
        DeviceProfile = "pixel_6",
        AvdHome = @"C:\avd",
        ConsolePort = 5556,
        GrpcPort = 8558,
    };

    [Fact]
    public void Arguments_pin_acceleration_headless_and_token_auth()
    {
        var args = EngineArguments.Build(Record(), headless: true, "swiftshader_indirect", coldBoot: true);

        Assert.Contains("-accel", args);
        Assert.Equal("on", args[args.ToList().IndexOf("-accel") + 1]);
        Assert.Contains("-no-window", args);
        Assert.Contains("-no-snapshot-load", args);
        Assert.Contains("-no-snapshot-save", args);
        Assert.Equal("5556", args[args.ToList().IndexOf("-port") + 1]);
        Assert.Equal("8558", args[args.ToList().IndexOf("-grpc") + 1]);
        Assert.Contains("-grpc-use-token", args);
    }

    [Fact]
    public void Arguments_use_avd_name_not_path()
    {
        var args = EngineArguments.Build(Record(), headless: true, "swiftshader_indirect", coldBoot: true);
        var index = args.ToList().IndexOf("-avd");
        Assert.Equal("ws_alpha", args[index + 1]);
    }

    [Fact]
    public void No_token_or_unauthenticated_flags_are_ever_used()
    {
        var args = EngineArguments.Build(Record(), headless: false, "host", coldBoot: false);
        Assert.DoesNotContain("-no-auth", args);
        Assert.DoesNotContain("-grpc-allowlist", args);
    }
}

public sealed class AdbTargetingTests
{
    [Theory]
    [InlineData("emulator-5554")]
    [InlineData("emulator-5584")]
    public void Managed_emulator_serials_are_accepted(string serial)
    {
        Assert.Equal(serial, AdbClient.ValidateSerial(serial));
    }

    [Theory]
    [InlineData("R58M12345AB")]
    [InlineData("192.168.1.10:5555")]
    [InlineData("emulator-5554; rm -rf /")]
    [InlineData("")]
    public void Physical_or_malformed_serials_are_rejected(string serial)
    {
        Assert.Throws<ArgumentException>(() => AdbClient.ValidateSerial(serial));
    }
}

public sealed class ImageIdTests
{
    [Theory]
    [InlineData("system-images;android-35;default;x86_64")]
    [InlineData("system-images/android-35/default/x86_64")]
    public void Both_sdk_image_id_forms_normalize_to_the_canonical_form(string input)
    {
        Assert.Equal(
            "system-images;android-35;default;x86_64",
            SdkLocator.NormalizeImageId(input));
    }

    [Fact]
    public void Invalid_image_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SdkLocator.NormalizeImageId("android-35"));
    }
}
