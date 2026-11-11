using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Device library operations that own files rather than engine processes:
/// versioned profile export/import, ownership-aware duplication, reset to the
/// device baseline, and snapshot metadata with compatibility validation.
/// Every mutation is staged inside the instance's own AVD directory and is
/// recorded so a rollback can restore the previous state.
/// </summary>
public sealed class DeviceLibraryService
{
    public const int ProfileSchemaVersion = 1;

    private static readonly string[] FingerprintKeys =
    {
        "AvdId", "abi.type", "avd.ini.displayname", "disk.dataPartition.size",
        "fastboot.forceColdBoot", "hw.cpu.ncore", "hw.device.name", "hw.gpu.mode",
        "hw.lcd.density", "hw.lcd.height", "hw.lcd.width", "hw.ramSize", "image.sysdir.1",
        "PlayStore.enabled", "tag.display", "tag.id", "target",
    };

    private readonly WorkspacePaths _paths;
    private readonly InstanceManager _manager;

    public DeviceLibraryService(WorkspacePaths paths, InstanceManager manager)
    {
        _paths = paths;
        _manager = manager;
    }

    public string AvdDirectory(InstanceRecord record) =>
        Path.Combine(record.AvdHome, record.AvdName + ".avd");

    public string ConfigPath(InstanceRecord record) =>
        Path.Combine(AvdDirectory(record), "config.ini");

    public string InstanceMetadataPath(InstanceRecord record) =>
        Path.Combine(AvdDirectory(record), "ws-instance.json");

    public string SnapshotDirectory(InstanceRecord record) =>
        Path.Combine(AvdDirectory(record), "snapshots");

    // ---------------------------------------------------------------- profile

    public sealed record DeviceProfile(
        int SchemaVersion,
        string ImagePackage,
        string DeviceId,
        int ConsolePort,
        int GrpcPort,
        string GpuMode,
        bool Headless,
        int Width,
        int Height,
        int Density,
        int RamMb,
        int Cores,
        string DataPartition,
        string ConfigFingerprint,
        string LineageId,
        string? ParentLineageId,
        string EngineVersion);

    /// <summary>Compatibility information for one guest, used by recipes.</summary>
    public JsonObject GuestInfo(InstanceRecord record)
    {
        var metadata = ReadMetadata(record);
        return new JsonObject
        {
            ["instance"] = record.Name,
            ["imagePackage"] = record.ImagePackage,
            ["deviceProfile"] = record.DeviceProfile,
            ["configFingerprint"] = ComputeConfigFingerprint(ReadConfig(record)),
            ["instanceFingerprint"] = ComputeInstanceFingerprint(record),
            ["lineageId"] = Str(metadata, "lineageId"),
        };
    }

    public JsonObject ExportProfile(InstanceRecord record)
    {
        var profile = ReadProfile(record);
        return new JsonObject
        {
            ["schemaVersion"] = profile.SchemaVersion,
            ["exportedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["profile"] = ProfileToJson(profile),
        };
    }

    public static JsonObject ProfileToJson(DeviceProfile profile) => new()
    {
        ["schemaVersion"] = profile.SchemaVersion,
        ["imagePackage"] = profile.ImagePackage,
        ["deviceProfile"] = profile.DeviceId,
        ["consolePort"] = profile.ConsolePort,
        ["grpcPort"] = profile.GrpcPort,
        ["gpuMode"] = profile.GpuMode,
        ["headless"] = profile.Headless,
        ["width"] = profile.Width,
        ["height"] = profile.Height,
        ["density"] = profile.Density,
        ["ramMb"] = profile.RamMb,
        ["cores"] = profile.Cores,
        ["dataPartition"] = profile.DataPartition,
        ["configFingerprint"] = profile.ConfigFingerprint,
        ["lineageId"] = profile.LineageId,
        ["parentLineageId"] = profile.ParentLineageId,
        ["engineVersion"] = profile.EngineVersion,
    };

    public DeviceProfile ReadProfile(InstanceRecord record)
    {
        var config = ReadConfig(record);
        var metadata = ReadMetadata(record);
        return new DeviceProfile(
            ProfileSchemaVersion,
            record.ImagePackage,
            record.DeviceProfile,
            record.ConsolePort,
            record.GrpcPort,
            record.GpuMode,
            record.Headless,
            ConfigInt(config, "hw.lcd.width", 1080),
            ConfigInt(config, "hw.lcd.height", 2400),
            ConfigInt(config, "hw.lcd.density", 420),
            ConfigInt(config, "hw.ramSize", 3072),
            ConfigInt(config, "hw.cpu.ncore", 4),
            config.GetValueOrDefault("disk.dataPartition.size", "2048M"),
            ComputeConfigFingerprint(config),
            Str(metadata, "lineageId") ?? Guid.NewGuid().ToString("N"),
            Str(metadata, "parentLineageId"),
            record.EngineVersion ?? "unknown");
    }

    public static DeviceProfile ParseProfile(JsonObject json)
    {
        var schema = json["schemaVersion"]?.GetValue<int>() ?? 0;
        if (schema != ProfileSchemaVersion)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest,
                $"Unsupported profile schema version {schema}; expected {ProfileSchemaVersion}.");
        }

        string Text(string name, bool required = true)
        {
            var value = json[name]?.GetValue<string>();
            if (required && string.IsNullOrWhiteSpace(value))
            {
                throw new SupervisorOperationException(
                    ErrorCodes.InvalidRequest, $"The profile is missing '{name}'.");
            }

            return value ?? "";
        }

        int Int(string name, int fallback)
        {
            var node = json[name];
            return node is null ? fallback : node.GetValue<int>();
        }

        return new DeviceProfile(
            schema,
            Text("imagePackage"),
            Text("deviceProfile"),
            Int("consolePort", 0),
            Int("grpcPort", 0),
            Text("gpuMode", false) is { Length: > 0 } gpu ? gpu : "swiftshader_indirect",
            json["headless"]?.GetValue<bool>() ?? true,
            Int("width", 1080),
            Int("height", 2400),
            Int("density", 420),
            Int("ramMb", 3072),
            Int("cores", 4),
            Text("dataPartition", false) is { Length: > 0 } disk ? disk : "2048M",
            Text("configFingerprint", false),
            Text("lineageId", false),
            json["parentLineageId"]?.GetValue<string>(),
            Text("engineVersion", false));
    }

    // ----------------------------------------------------------------- files

    public Dictionary<string, string> ReadConfig(InstanceRecord record)
    {
        var path = ConfigPath(record);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    public string ComputeConfigFingerprint(Dictionary<string, string> config)
    {
        var builder = new StringBuilder();
        foreach (var key in FingerprintKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            builder.Append(key).Append('=').Append(config.GetValueOrDefault(key, "")).Append('\n');
        }

        return Hash(builder.ToString());
    }

    public string ComputeInstanceFingerprint(InstanceRecord record)
    {
        var config = ReadConfig(record);
        var metadata = ReadMetadata(record);
        var lineage = Str(metadata, "lineageId") ?? "";
        var engine = record.EngineVersion ?? "unknown";
        return Hash(string.Join(
            "\n",
            record.ImagePackage,
            record.DeviceProfile,
            engine,
            ComputeConfigFingerprint(config),
            lineage));
    }

    public JsonObject? ReadMetadata(InstanceRecord record)
    {
        var path = InstanceMetadataPath(record);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void WriteMetadata(InstanceRecord record, JsonObject metadata)
    {
        Directory.CreateDirectory(AvdDirectory(record));
        File.WriteAllText(
            InstanceMetadataPath(record),
            metadata.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Str(JsonObject? node, string key) =>
        node?[key] is { } value && value.GetValueKind() != System.Text.Json.JsonValueKind.Null
            ? value.GetValue<string>()
            : null;

    private static int ConfigInt(Dictionary<string, string> config, string key, int fallback) =>
        config.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>Size of the writable disk artifacts of an instance.</summary>
    public long WritableDiskBytes(InstanceRecord record)
    {
        var directory = AvdDirectory(record);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "userdata*", SearchOption.TopDirectoryOnly))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    // ------------------------------------------------------------- lifecycle

    /// <summary>
    /// Creates an independent workspace-owned copy of a stopped instance. The
    /// writable disk is copied file by file, the source tree is validated for
    /// reparse points first, and the copy receives a new disk lineage whose
    /// parent is the source lineage.
    /// </summary>
    public async Task<JsonObject> DuplicateAsync(
        InstanceRecord source, string newName, CancellationToken cancellationToken)
    {
        if (source.State is InstanceState.Running or InstanceState.Starting)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest,
                "The source instance must be stopped so its writable disk is never copied while running.");
        }

        var sourceDirectory = AvdDirectory(source);
        var offenders = new List<string>();
        ValidateNoReparsePoints(sourceDirectory, offenders);
        if (offenders.Count > 0)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest,
                "The source AVD contains reparse points or links and cannot be duplicated: "
                + string.Join(", ", offenders.Take(3)));
        }

        var target = await _manager.CreateAsync(
            newName,
            source.ImagePackage,
            source.DeviceProfile,
            null,
            null,
            source.GpuMode,
            source.Headless,
            cancellationToken).ConfigureAwait(false);

        var targetDirectory = AvdDirectory(target);
        Directory.CreateDirectory(targetDirectory);

        var copied = new JsonArray();
        long copiedBytes = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "userdata*", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(targetDirectory, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
            copiedBytes += new FileInfo(destination).Length;
            copied.Add(Path.GetFileName(file));
        }

        var sourceConfig = ReadConfig(source);
        var targetConfig = ReadConfig(target);
        var targetAvdId = targetConfig.GetValueOrDefault("AvdId", "");
        foreach (var (key, value) in sourceConfig)
        {
            targetConfig[key] = value;
        }

        if (!string.IsNullOrEmpty(targetAvdId))
        {
            targetConfig["AvdId"] = targetAvdId;
        }

        WriteConfig(target, targetConfig);

        var sourceMetadata = ReadMetadata(source);
        if (sourceMetadata is null)
        {
            sourceMetadata = new JsonObject
            {
                ["lineageId"] = "lin-" + ComputeConfigFingerprint(ReadConfig(source))[..16],
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["configFingerprint"] = ComputeConfigFingerprint(ReadConfig(source)),
            };
            WriteMetadata(source, sourceMetadata);
        }

        var lineage = Guid.NewGuid().ToString("N");
        WriteMetadata(target, new JsonObject
        {
            ["lineageId"] = lineage,
            ["parentLineageId"] = Str(sourceMetadata, "lineageId"),
            ["sourceInstance"] = source.Name,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["configFingerprint"] = ComputeConfigFingerprint(targetConfig),
        });

        return new JsonObject
        {
            ["source"] = source.Name,
            ["target"] = target.Name,
            ["targetAvd"] = target.AvdName,
            ["serial"] = target.AdbSerial,
            ["consolePort"] = target.ConsolePort,
            ["grpcPort"] = target.GrpcPort,
            ["copiedFiles"] = copied,
            ["copiedBytes"] = copiedBytes,
            ["sourceFingerprint"] = ComputeInstanceFingerprint(source),
            ["targetFingerprint"] = ComputeInstanceFingerprint(target),
            ["lineageId"] = lineage,
            ["parentLineageId"] = Str(sourceMetadata, "lineageId"),
        };
    }

    /// <summary>
    /// Resets a stopped instance to its device baseline by discarding the
    /// writable disk artifacts. The lineage is preserved so snapshot
    /// compatibility stays meaningful.
    /// </summary>
    public JsonObject Reset(InstanceRecord record)
    {
        if (record.State is InstanceState.Running or InstanceState.Starting)
        {
            throw new SupervisorOperationException(
                ErrorCodes.InvalidRequest, "The instance must be stopped before it is reset.");
        }

        var directory = AvdDirectory(record);
        var removed = new JsonArray();
        long freed = 0;
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "userdata*", SearchOption.TopDirectoryOnly))
            {
                freed += new FileInfo(file).Length;
                File.Delete(file);
                removed.Add(Path.GetFileName(file));
            }
        }

        var fingerprintBefore = ComputeInstanceFingerprint(record);
        var metadata = ReadMetadata(record) ?? new JsonObject();
        metadata["resetUtc"] = DateTimeOffset.UtcNow.ToString("O");
        metadata["configFingerprint"] = ComputeConfigFingerprint(ReadConfig(record));
        WriteMetadata(record, metadata);

        return new JsonObject
        {
            ["name"] = record.Name,
            ["removedFiles"] = removed,
            ["freedBytes"] = freed,
            ["fingerprintBefore"] = fingerprintBefore,
            ["fingerprintAfter"] = ComputeInstanceFingerprint(record),
            ["lineageId"] = Str(metadata, "lineageId"),
            ["resetUtc"] = metadata["resetUtc"]!.GetValue<string>(),
        };
    }

    public void WriteConfig(InstanceRecord record, Dictionary<string, string> config)
    {
        Directory.CreateDirectory(AvdDirectory(record));
        var lines = config
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Key + "=" + entry.Value);
        File.WriteAllLines(ConfigPath(record), lines);
    }

    // ------------------------------------------------------------- snapshots

    public string SnapshotMetadataPath(InstanceRecord record, string tag) =>
        Path.Combine(SnapshotDirectory(record), tag, "ws-snapshot.json");

    public void WriteSnapshotMetadata(InstanceRecord record, string tag)
    {
        var directory = Path.Combine(SnapshotDirectory(record), tag);
        if (!Directory.Exists(directory))
        {
            return;
        }

        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(file).Length;
        }

        var metadata = new JsonObject
        {
            ["tag"] = tag,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["engineVersion"] = record.EngineVersion ?? "unknown",
            ["imagePackage"] = record.ImagePackage,
            ["deviceProfile"] = record.DeviceProfile,
            ["configFingerprint"] = ComputeConfigFingerprint(ReadConfig(record)),
            ["instanceFingerprint"] = ComputeInstanceFingerprint(record),
            ["lineageId"] = Str(ReadMetadata(record), "lineageId"),
            ["bytes"] = bytes,
        };
        File.WriteAllText(
            SnapshotMetadataPath(record, tag),
            metadata.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public JsonObject ListSnapshots(InstanceRecord record)
    {
        var current = ComputeInstanceFingerprint(record);
        var array = new JsonArray();
        var directory = SnapshotDirectory(record);
        if (Directory.Exists(directory))
        {
            foreach (var tagDirectory in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
            {
                var tag = Path.GetFileName(tagDirectory);
                JsonObject? metadata = null;
                var metadataPath = SnapshotMetadataPath(record, tag);
                if (File.Exists(metadataPath))
                {
                    try
                    {
                        metadata = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
                    }
                    catch (Exception)
                    {
                        metadata = null;
                    }
                }

                long bytes = 0;
                foreach (var file in Directory.EnumerateFiles(tagDirectory, "*", SearchOption.AllDirectories))
                {
                    bytes += new FileInfo(file).Length;
                }

                var stored = Str(metadata, "instanceFingerprint");
                var compatible = stored is null || stored == current;
                var reason = compatible
                    ? null
                    : metadata is null
                        ? "No snapshot metadata was recorded for this tag."
                        : "The instance configuration changed after this snapshot was created.";

                array.Add(new JsonObject
                {
                    ["tag"] = tag,
                    ["bytes"] = bytes,
                    ["createdUtc"] = Str(metadata, "createdUtc"),
                    ["engineVersion"] = Str(metadata, "engineVersion"),
                    ["imagePackage"] = Str(metadata, "imagePackage"),
                    ["configFingerprint"] = Str(metadata, "configFingerprint"),
                    ["instanceFingerprint"] = stored,
                    ["compatible"] = compatible,
                    ["incompatibleReason"] = reason,
                });
            }
        }

        return new JsonObject
        {
            ["name"] = record.Name,
            ["currentFingerprint"] = current,
            ["snapshots"] = array,
            ["count"] = array.Count,
        };
    }

    public JsonObject DeleteSnapshot(InstanceRecord record, string tag, bool force)
    {
        var listing = ListSnapshots(record);
        var snapshots = listing["snapshots"]!.AsArray();
        var match = snapshots.FirstOrDefault(node =>
            node!.AsObject()["tag"]!.GetValue<string>() == tag)?.AsObject();
        if (match is null)
        {
            throw new SupervisorOperationException(
                ErrorCodes.NotFound, $"Snapshot '{tag}' does not exist for '{record.Name}'.");
        }

        var compatible = snapshots
            .Select(node => node!.AsObject())
            .Where(node => node["compatible"]!.GetValue<bool>())
            .ToList();
        var newest = compatible
            .OrderByDescending(node => Str(node, "createdUtc") ?? "")
            .FirstOrDefault();
        var isLastRecoveryPoint = newest is not null
            && newest["tag"]!.GetValue<string>() == tag;
        if (isLastRecoveryPoint && !force)
        {
            throw new SupervisorOperationException(
                ErrorCodes.ApprovalRequired,
                $"Snapshot '{tag}' is the last verified recovery point for '{record.Name}'; pass force with an approval to delete it.");
        }

        var directory = Path.Combine(SnapshotDirectory(record), tag);
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(file).Length;
        }

        Directory.Delete(directory, recursive: true);
        return new JsonObject
        {
            ["name"] = record.Name,
            ["tag"] = tag,
            ["deleted"] = true,
            ["freedBytes"] = bytes,
            ["lastRecoveryPoint"] = isLastRecoveryPoint,
        };
    }

    // -------------------------------------------------------------- presets

    public static JsonArray Presets()
    {
        var array = new JsonArray();
        void Add(string id, string label, int width, int height, int density, int ramMb, int cores, string disk)
        {
            array.Add(new JsonObject
            {
                ["id"] = id,
                ["label"] = label,
                ["width"] = width,
                ["height"] = height,
                ["density"] = density,
                ["ramMb"] = ramMb,
                ["cores"] = cores,
                ["dataPartition"] = disk,
                ["estimatedBytes"] = EstimateBytes(disk) * 2,
            });
        }

        Add("ws-phone", "Lab phone (1080x2400, 420 dpi)", 1080, 2400, 420, 3072, 4, "2048M");
        Add("ws-phone-small", "Lab phone (720x1520, 320 dpi)", 720, 1520, 320, 2048, 2, "1536M");
        Add("ws-tablet", "Lab tablet (1600x2560, 320 dpi)", 1600, 2560, 320, 4096, 6, "4096M");
        Add("ws-tablet-large", "Lab tablet (2560x1600, 240 dpi)", 2560, 1600, 240, 6144, 8, "6144M");
        return array;
    }

    public static long EstimateBytes(string dataPartition)
    {
        var digits = new string(dataPartition.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value * 1024L * 1024L : 2048L * 1024 * 1024;
    }

    public JsonObject DiskEstimate(string dataPartition)
    {
        var required = EstimateBytes(dataPartition);
        var reserve = required * 2;
        var drive = new DriveInfo(Path.GetPathRoot(_paths.Root) ?? "C:\\");
        return new JsonObject
        {
            ["root"] = _paths.Root,
            ["dataPartition"] = dataPartition,
            ["requiredBytes"] = required,
            ["withSnapshotReserveBytes"] = reserve,
            ["freeBytes"] = drive.AvailableFreeSpace,
            ["sufficient"] = drive.AvailableFreeSpace >= reserve,
        };
    }

    /// <summary>Rejects symlinks and Windows reparse points inside an AVD tree.</summary>
    public void ValidateNoReparsePoints(string directory, List<string> found)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var info = new FileInfo(entry);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                found.Add(entry);
                continue;
            }

            if (info is DirectoryInfo)
            {
                ValidateNoReparsePoints(entry, found);
            }
        }
    }
}
