namespace EmulatorWorkspace.Core.Engine;

/// <summary>
/// The engine advertises its controller endpoint in a per-process discovery
/// file (<c>pid_&lt;qemu-pid&gt;.ini</c>) that contains a freshly generated gRPC
/// token. The file path is obtained from the instance's own console, so the
/// token is always the one belonging to this exact emulator process and is
/// rotated on every launch. The token is never logged.
/// </summary>
public sealed class EmulatorDiscovery
{
    private EmulatorDiscovery(
        string discoveryPath,
        int adbPort,
        int grpcPort,
        string grpcToken,
        string avdName,
        string avdDirectory,
        string emulatorVersion,
        string emulatorBuild)
    {
        DiscoveryPath = discoveryPath;
        AdbPort = adbPort;
        GrpcPort = grpcPort;
        GrpcToken = grpcToken;
        AvdName = avdName;
        AvdDirectory = avdDirectory;
        EmulatorVersion = emulatorVersion;
        EmulatorBuild = emulatorBuild;
    }

    public string DiscoveryPath { get; }

    public int AdbPort { get; }

    public int GrpcPort { get; }

    public string GrpcToken { get; }

    public string AvdName { get; }

    public string AvdDirectory { get; }

    public string EmulatorVersion { get; }

    public string EmulatorBuild { get; }

    public static async Task<EmulatorDiscovery> ReadAsync(
        AdbClient adb,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var response = await adb.EmulatorConsoleAsync(
            serial,
            new[] { "avd", "discoverypath" },
            cancellationToken).ConfigureAwait(false);

        var path = response
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));

        if (path is null || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "The engine did not advertise a discovery file for this instance. " +
                "The controller cannot be authenticated without it.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        string Require(string key) => values.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new InvalidOperationException($"Discovery file '{path}' is missing '{key}'.");

        return new EmulatorDiscovery(
            path,
            int.Parse(Require("port.adb")),
            int.Parse(Require("grpc.port")),
            Require("grpc.token"),
            Require("avd.id"),
            Require("avd.dir"),
            Require("emulator.version"),
            Require("emulator.build"));
    }
}
