using System.Globalization;
using System.Text.Json;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace EmulatorWorkspace.Core.Storage;

/// <summary>
/// Authoritative durable store for the supervisor. One process (the supervisor)
/// owns the only write connection; every mutation is a transaction. Legacy
/// JSON instance records are imported once and preserved untouched as a
/// fallback if the import fails.
/// </summary>
public sealed class SupervisorStore : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly WorkspacePaths _paths;

    public SupervisorStore(WorkspacePaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(paths.Root);
        DatabasePath = Path.Combine(paths.Root, "supervisor.db");

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA busy_timeout=5000;");
        Execute("PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public string DatabasePath { get; }

    public string? LastMigrationError { get; private set; }

    public IReadOnlyList<InstanceRecord> ListInstances()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM instances ORDER BY name COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            var results = new List<InstanceRecord>();
            while (reader.Read())
            {
                results.Add(Map(reader));
            }

            return results;
        }
    }

    public InstanceRecord? GetInstance(string name)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM instances WHERE name = $name;";
            command.Parameters.AddWithValue("$name", name);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        }
    }

    public void UpsertInstance(InstanceRecord record)
    {
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO instances (
                    name, avd_name, image_package, device_profile, avd_home, console_port, grpc_port,
                    state, engine_pid, engine_start_utc, host_pid, host_start_utc, engine_path,
                    engine_version, engine_arguments, gpu_mode, headless, ramdisk_path,
                    created_utc, updated_utc, last_error)
                VALUES (
                    $name, $avd, $image, $device, $avdHome, $console, $grpc,
                    $state, $pid, $start, $hostPid, $hostStart, $enginePath,
                    $version, $arguments, $gpu, $headless, $ramdisk, $created, $updated, $error)
                ON CONFLICT(name) DO UPDATE SET
                    avd_name = excluded.avd_name,
                    image_package = excluded.image_package,
                    device_profile = excluded.device_profile,
                    avd_home = excluded.avd_home,
                    console_port = excluded.console_port,
                    grpc_port = excluded.grpc_port,
                    state = excluded.state,
                    engine_pid = excluded.engine_pid,
                    engine_start_utc = excluded.engine_start_utc,
                    host_pid = excluded.host_pid,
                    host_start_utc = excluded.host_start_utc,
                    engine_path = excluded.engine_path,
                    engine_version = excluded.engine_version,
                    engine_arguments = excluded.engine_arguments,
                    gpu_mode = excluded.gpu_mode,
                    headless = excluded.headless,
                    ramdisk_path = excluded.ramdisk_path,
                    updated_utc = excluded.updated_utc,
                    last_error = excluded.last_error;
                """;
            Bind(command, record);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        MirrorToJson(record);
    }

    public void DeleteInstance(string name)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM instances WHERE name = $name;";
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }

        var mirror = _paths.InstancePath(name);
        if (File.Exists(mirror))
        {
            File.Delete(mirror);
        }
    }

    public void RecordOperation(
        string id,
        string name,
        string callerKind,
        string callerName,
        string argsJson,
        DateTimeOffset startedUtc,
        DateTimeOffset? finishedUtc,
        string outcome,
        string? code,
        string? message,
        long? durationMs)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operations (id, name, caller_kind, caller_name, args_json, started_utc,
                                        finished_utc, outcome, code, message, duration_ms)
                VALUES ($id, $name, $kind, $caller, $args, $started, $finished, $outcome, $code, $message, $duration)
                ON CONFLICT(id) DO UPDATE SET
                    finished_utc = excluded.finished_utc,
                    outcome = excluded.outcome,
                    code = excluded.code,
                    message = excluded.message,
                    duration_ms = excluded.duration_ms;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$kind", callerKind);
            command.Parameters.AddWithValue("$caller", callerName);
            command.Parameters.AddWithValue("$args", argsJson);
            command.Parameters.AddWithValue("$started", startedUtc.ToString("O"));
            command.Parameters.AddWithValue("$finished", (object?)finishedUtc?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$outcome", outcome);
            command.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
            command.Parameters.AddWithValue("$duration", (object?)durationMs ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    private void Migrate()
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS instances (
                    name TEXT PRIMARY KEY,
                    avd_name TEXT NOT NULL,
                    image_package TEXT NOT NULL,
                    device_profile TEXT NOT NULL,
                    avd_home TEXT NOT NULL,
                    console_port INTEGER NOT NULL,
                    grpc_port INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    engine_pid INTEGER NULL,
                    engine_start_utc TEXT NULL,
                    host_pid INTEGER NULL,
                    host_start_utc TEXT NULL,
                    engine_path TEXT NULL,
                    engine_version TEXT NULL,
                    engine_arguments TEXT NULL,
                    gpu_mode TEXT NULL,
                    headless INTEGER NOT NULL DEFAULT 1,
                    ramdisk_path TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    last_error TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS operations (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    caller_kind TEXT NOT NULL,
                    caller_name TEXT NOT NULL,
                    args_json TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    finished_utc TEXT NULL,
                    outcome TEXT NOT NULL,
                    code TEXT NULL,
                    message TEXT NULL,
                    duration_ms INTEGER NULL
                );
                CREATE INDEX IF NOT EXISTS ix_operations_started ON operations(started_utc);
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
        }

        var current = ReadSchemaVersion();
        if (current == 0)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_info (version) VALUES ($version);";
            command.Parameters.AddWithValue("$version", SchemaVersion);
            command.ExecuteNonQuery();
            current = SchemaVersion;
        }

        if (current > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The supervisor database schema is version {current}, newer than this build ({SchemaVersion}). " +
                "Start the matching supervisor build instead of downgrading the store.");
        }

        if (GetSetting("legacy_import_v1") is null)
        {
            try
            {
                ImportLegacyRecords();
                SetSetting("legacy_import_v1", DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                LastMigrationError = ex.Message;
                // The JSON records and all disks stay untouched. The supervisor
                // continues with an empty database and reports the failure.
                SetSetting("legacy_import_v1_error", ex.ToString());
            }
        }
    }

    private int ReadSchemaVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info ORDER BY rowid DESC LIMIT 1;";
        var value = command.ExecuteScalar();
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private void ImportLegacyRecords()
    {
        var directory = _paths.InstancesDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.GetFiles(directory, "*.json");
        if (files.Length == 0)
        {
            return;
        }

        var store = new InstanceStore(_paths);
        var records = new List<InstanceRecord>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var record = store.TryGet(name);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        using var transaction = _connection.BeginTransaction();
        foreach (var record in records)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO instances (
                    name, avd_name, image_package, device_profile, avd_home, console_port, grpc_port,
                    state, engine_pid, engine_start_utc, host_pid, host_start_utc, engine_path,
                    engine_version, engine_arguments, gpu_mode, headless, ramdisk_path,
                    created_utc, updated_utc, last_error)
                VALUES (
                    $name, $avd, $image, $device, $avdHome, $console, $grpc,
                    $state, $pid, $start, $hostPid, $hostStart, $enginePath,
                    $version, $arguments, $gpu, $headless, $ramdisk, $created, $updated, $error);
                """;
            Bind(command, record);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Bind(SqliteCommand command, InstanceRecord record)
    {
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$avd", record.AvdName);
        command.Parameters.AddWithValue("$image", record.ImagePackage);
        command.Parameters.AddWithValue("$device", record.DeviceProfile);
        command.Parameters.AddWithValue("$avdHome", record.AvdHome);
        command.Parameters.AddWithValue("$console", record.ConsolePort);
        command.Parameters.AddWithValue("$grpc", record.GrpcPort);
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$pid", (object?)record.EnginePid ?? DBNull.Value);
        command.Parameters.AddWithValue("$start", (object?)record.EngineStartTimeUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$hostPid", (object?)record.HostPid ?? DBNull.Value);
        command.Parameters.AddWithValue("$hostStart", (object?)record.HostStartTimeUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$enginePath", (object?)record.EnginePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$arguments", (object?)record.EngineArguments ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", (object?)record.EngineVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$gpu", (object?)record.GpuMode ?? DBNull.Value);
        command.Parameters.AddWithValue("$headless", record.Headless ? 1 : 0);
        command.Parameters.AddWithValue("$ramdisk", (object?)record.RamdiskPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", record.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", record.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$error", (object?)record.LastError ?? DBNull.Value);
    }

    private static InstanceRecord Map(SqliteDataReader reader)
    {
        string? Text(string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        int? Number(string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        }

        return new InstanceRecord
        {
            Name = reader.GetString(reader.GetOrdinal("name")),
            AvdName = reader.GetString(reader.GetOrdinal("avd_name")),
            ImagePackage = reader.GetString(reader.GetOrdinal("image_package")),
            DeviceProfile = reader.GetString(reader.GetOrdinal("device_profile")),
            AvdHome = reader.GetString(reader.GetOrdinal("avd_home")),
            ConsolePort = reader.GetInt32(reader.GetOrdinal("console_port")),
            GrpcPort = reader.GetInt32(reader.GetOrdinal("grpc_port")),
            State = Enum.TryParse<InstanceState>(Text("state"), out var state) ? state : InstanceState.Unknown,
            EnginePid = Number("engine_pid"),
            EngineStartTimeUtc = Text("engine_start_utc"),
            HostPid = Number("host_pid"),
            HostStartTimeUtc = Text("host_start_utc"),
            EnginePath = Text("engine_path"),
            EngineVersion = Text("engine_version"),
            EngineArguments = Text("engine_arguments"),
            GpuMode = Text("gpu_mode"),
            Headless = reader.GetInt32(reader.GetOrdinal("headless")) != 0,
            RamdiskPath = Text("ramdisk_path"),
            CreatedUtc = DateTimeOffset.Parse(Text("created_utc") ?? DateTimeOffset.UtcNow.ToString("O"), CultureInfo.InvariantCulture),
            UpdatedUtc = DateTimeOffset.Parse(Text("updated_utc") ?? DateTimeOffset.UtcNow.ToString("O"), CultureInfo.InvariantCulture),
            LastError = Text("last_error"),
        };
    }

    private void MirrorToJson(InstanceRecord record)
    {
        try
        {
            var target = _paths.InstancePath(record.Name);
            var temp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            };
            File.WriteAllText(temp, JsonSerializer.Serialize(record, options));
            if (File.Exists(target))
            {
                File.Replace(temp, target, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch (IOException)
        {
            // The database is authoritative; the mirror is diagnostic only.
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
