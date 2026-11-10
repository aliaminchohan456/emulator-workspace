using System.Text.Json;
using System.Text.Json.Serialization;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Core.Instances;

/// <summary>
/// Durable instance metadata. Every write goes through a same-volume temporary
/// file followed by an atomic replace, and every mutation is serialized per
/// instance so that two operations can never interleave a read-modify-write.
/// </summary>
public sealed class InstanceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly WorkspacePaths _paths;

    public InstanceStore(WorkspacePaths paths)
    {
        _paths = paths;
        _paths.EnsureLayout();
    }

    public WorkspacePaths Paths => _paths;

    public IReadOnlyList<InstanceRecord> List()
    {
        var result = new List<InstanceRecord>();
        foreach (var file in Directory.EnumerateFiles(_paths.InstancesDirectory, "*.json"))
        {
            try
            {
                var record = ReadFile(file);
                if (record is not null)
                {
                    result.Add(record);
                }
            }
            catch (JsonException)
            {
                // A corrupt record is surfaced by the caller that owns it; list
                // stays usable for every other instance.
            }
        }

        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public InstanceRecord? TryGet(string name)
    {
        var path = _paths.InstancePath(name);
        return File.Exists(path) ? ReadFile(path) : null;
    }

    public InstanceRecord Get(string name) =>
        TryGet(name) ?? throw new InstanceNotFoundException(name);

    public bool Exists(string name) => File.Exists(_paths.InstancePath(name));

    public void Save(InstanceRecord record)
    {
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        record.SchemaVersion = InstanceRecord.CurrentSchemaVersion;

        using var mutex = AcquireMutex(record.Name);
        var path = _paths.InstancePath(record.Name);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var json = JsonSerializer.Serialize(record, SerializerOptions);

        File.WriteAllText(temp, json);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// Applies a mutation under the per-instance lock so concurrent CLI or
    /// supervisor calls cannot lose an update.
    /// </summary>
    public InstanceRecord Update(string name, Action<InstanceRecord> mutate)
    {
        using var mutex = AcquireMutex(name);
        var record = TryGet(name) ?? throw new InstanceNotFoundException(name);
        mutate(record);
        record.UpdatedUtc = DateTimeOffset.UtcNow;

        var path = _paths.InstancePath(name);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, JsonSerializer.Serialize(record, SerializerOptions));
        File.Replace(temp, path, null, ignoreMetadataErrors: true);
        return record;
    }

    public void Delete(string name)
    {
        using var mutex = AcquireMutex(name);
        var path = _paths.InstancePath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IDisposable AcquireMutex(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name: @"Local\EmulatorWorkspace.Instance." + name);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
            {
                mutex.Dispose();
                throw new TimeoutException($"Timed out waiting for the instance lock for '{name}'.");
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died while holding the lock; we own it now.
        }

        return new MutexReleaser(mutex);
    }

    private static InstanceRecord? ReadFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<InstanceRecord>(stream, SerializerOptions);
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

public sealed class InstanceNotFoundException(string name)
    : Exception($"No managed instance named '{name}' exists in this workspace.");
