using System.Diagnostics;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Controller;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;

namespace EmulatorWorkspace.Core.Supervisor;

public sealed record FrameEvent(
    string Instance,
    uint Sequence,
    int Width,
    int Height,
    long Bytes,
    ulong EmulatorTimestampUs,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset WrittenUtc,
    string Path,
    int DroppedSincePrevious);

public sealed record FrameSample(
    uint Sequence,
    ulong EmulatorTimestampUs,
    DateTimeOffset ReceivedUtc,
    int Width,
    int Height,
    int Bytes,
    double IntervalMs,
    int Dropped);

/// <summary>
/// One latest-frame pipeline per instance: the engine gRPC stream is read on a
/// background task, the newest frame is written to a single bounded file, and
/// subscribers receive a small event pointing at it. Stale frames are replaced
/// rather than queued.
/// </summary>
public sealed class FramePump : IAsyncDisposable
{
    private readonly InstanceRecord _record;
    private readonly string _outputDirectory;
    private readonly Func<FrameEvent, Task> _onFrame;
    private readonly Func<string, string, Task> _onClosed;
    private readonly object _traceGate = new();
    private readonly Queue<FrameSample> _trace = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private uint _lastSequence;
    private DateTimeOffset _lastReceived;
    private DateTimeOffset _lastPublished;

    public FramePump(
        InstanceRecord record,
        string outputDirectory,
        Func<FrameEvent, Task> onFrame,
        Func<string, string, Task> onClosed)
    {
        _record = record;
        _outputDirectory = outputDirectory;
        _onFrame = onFrame;
        _onClosed = onClosed;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public string OutputPath => System.IO.Path.Combine(_outputDirectory, _record.Name + ".png");

    public IReadOnlyList<FrameSample> Trace
    {
        get
        {
            lock (_traceGate)
            {
                return _trace.ToList();
            }
        }
    }

    public async Task StartAsync(int width, CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        Directory.CreateDirectory(_outputDirectory);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var discovery = await EmulatorDiscovery.ReadAsync(
            new AdbClient(ResolveAdb()),
            _record.AdbSerial,
            cancellationToken).ConfigureAwait(false);

        if (discovery.GrpcPort != _record.GrpcPort || discovery.AdbPort != _record.ConsolePort + 1)
        {
            throw new InvalidOperationException(
                "The advertised controller endpoint does not match the managed instance.");
        }

        _loop = Task.Run(() => RunAsync(discovery, width, _cts.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(EmulatorDiscovery discovery, int width, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var channel = EmulatorControlChannel.Connect(discovery.GrpcPort, discovery.GrpcToken, "bearer");
                await foreach (var frame in channel.StreamScreenshotsAsync(width, cancellationToken).ConfigureAwait(false))
                {
                    attempt = 0;
                    await PublishAsync(frame, cancellationToken).ConfigureAwait(false);
                }

                await _onClosed(_record.Name, "frame stream ended").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt > 5)
                {
                    await _onClosed(_record.Name, ex.Message).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task PublishAsync(ControllerFrame frame, CancellationToken cancellationToken)
    {
        var received = frame.ReceivedUtc;
        if (_lastPublished != default && (received - _lastPublished).TotalMilliseconds < 100)
        {
            return;
        }

        _lastPublished = received;
        var temp = OutputPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllBytesAsync(temp, frame.PngBytes, cancellationToken).ConfigureAwait(false);
        File.Move(temp, OutputPath, overwrite: true);

        var written = DateTimeOffset.UtcNow;
        var dropped = 0;
        if (_lastSequence != 0 && frame.Sequence > _lastSequence + 1)
        {
            dropped = checked((int)(frame.Sequence - _lastSequence - 1));
        }

        var interval = _lastReceived == default ? 0 : (received - _lastReceived).TotalMilliseconds;
        _lastReceived = received;
        _lastSequence = frame.Sequence;

        var (width, height) = ReadPngSize(frame.PngBytes);
        var sample = new FrameSample(
            frame.Sequence,
            frame.TimestampMicroseconds,
            received,
            width,
            height,
            frame.PngBytes.Length,
            interval,
            dropped);
        lock (_traceGate)
        {
            _trace.Enqueue(sample);
            while (_trace.Count > 5000)
            {
                _trace.Dequeue();
            }
        }

        var frameEvent = new FrameEvent(
            _record.Name,
            frame.Sequence,
            width,
            height,
            frame.PngBytes.Length,
            frame.TimestampMicroseconds,
            received,
            written,
            OutputPath,
            dropped);

        try
        {
            await _onFrame(frameEvent).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A disconnected subscriber must not stop the capture loop; the
            // subscription is removed by the session that owned it.
        }
    }

    private static (int Width, int Height) ReadPngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50)
        {
            return (0, 0);
        }

        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
    }

    private static string ResolveAdb()
    {
        var sdk = SdkLocator.Resolve();
        return sdk.AdbExe;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
