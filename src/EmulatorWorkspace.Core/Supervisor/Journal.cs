using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Append-only audit journal. Every privileged or mutating operation passes
/// through here before and after execution so interrupted work is reconcilable.
/// The backing file is opened lazily so a process that never performs an
/// operation (for example the engine host) never holds a handle on it.
/// </summary>
public sealed class Journal : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private FileStream? _stream;
    private StreamWriter? _writer;
    private bool _disposed;

    public Journal(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public void Append(string phase, OperationRequest request, OperationResult? result, TimeSpan? duration)
    {
        var entry = new JsonObject
        {
            ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["phase"] = phase,
            ["operation"] = request.Name,
            ["callerKind"] = request.Caller.Kind.ToString(),
            ["callerName"] = request.Caller.Name,
            ["arguments"] = JsonSerializer.SerializeToNode(request.Arguments, JsonDefaults.Options),
        };

        if (result is not null)
        {
            entry["success"] = result.Success;
            entry["code"] = result.Code;
            entry["message"] = result.Message;
        }

        if (duration is not null)
        {
            entry["durationMs"] = Math.Round(duration.Value.TotalMilliseconds, 1);
        }

        var line = entry.ToJsonString(JsonDefaults.Options);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureOpen();
            _writer!.WriteLine(line);
        }
    }

    private void EnsureOpen()
    {
        if (_writer is not null)
        {
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        _stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(_stream) { AutoFlush = true };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _stream?.Dispose();
        }
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
