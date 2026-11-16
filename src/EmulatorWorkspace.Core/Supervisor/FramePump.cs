using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using EmulatorWorkspace.Controller;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Frames;
using EmulatorWorkspace.Core.Instances;

namespace EmulatorWorkspace.Core.Supervisor;

public sealed record FramePumpOptions(string Format, int Width, int Height)
{
    public bool IsRaw => Format.Equals("rgb", StringComparison.OrdinalIgnoreCase)
                         || Format.Equals("bgr", StringComparison.OrdinalIgnoreCase);

    /// <summary>EV-11: the ring's pixel format, or <see cref="FrameRingFormat.None"/> for a PNG.</summary>
    public FrameRingFormat RingFormat => IsRaw ? FrameRingFormat.Rgb24 : FrameRingFormat.None;
}

public sealed record FrameEvent(
    string Instance,
    uint Sequence,
    int Width,
    int Height,
    int Stride,
    string Format,
    int GuestWidth,
    int GuestHeight,
    long Bytes,
    ulong EmulatorTimestampUs,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset WrittenUtc,
    string Path,
    int DroppedSincePrevious,
    int Slot = -1,
    string? MapName = null);

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
/// One latest-frame pipeline per instance. The engine stream is read on a
/// background task and only the newest frame is exposed: either a raw BGR24
/// file for the interactive viewport or a PNG file for one-shot/fallback use.
/// Stale frames are overwritten, never queued.
///
/// EV-11: every raw frame is also published into a per-instance shared-memory
/// ring, so a reader in another process copies it instead of waiting for a
/// 1.94 MB file to be written and read back. The file is written only while a
/// subscriber actually asked for files (the CLI and the MCP client do).
/// </summary>
public sealed class FramePump : IAsyncDisposable
{
    private static readonly Regex PhysicalSize = new(@"Physical size:\s*(\d+)x(\d+)", RegexOptions.Compiled);

    private readonly InstanceRecord _record;
    private readonly string _outputDirectory;
    private readonly FramePumpOptions _options;
    private readonly Func<FrameEvent, Task> _onFrame;
    private readonly Func<string, string, Task> _onClosed;
    /// <summary>EV-11: whether any live subscriber of this stream asked for files.</summary>
    private readonly Func<bool> _filesWanted;
    private readonly int _supervisorPid;
    private readonly object _traceGate = new();
    private readonly object _startGate = new();
    private readonly Queue<FrameSample> _trace = new();
    private readonly Queue<string> _rawFiles = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _start;
    private uint _lastSequence;
    private DateTimeOffset _lastReceived;
    private DateTimeOffset _lastPublished;
    private long _lastFrameTicks;
    private FrameRingWriter? _ring;
    private bool _ringFailedLogged;
    private bool _oversizeLogged;
    private int _guestWidth;
    private int _guestHeight;
    private int _publishedFrames;

    public FramePump(
        InstanceRecord record,
        string outputDirectory,
        FramePumpOptions options,
        Func<FrameEvent, Task> onFrame,
        Func<string, string, Task> onClosed,
        Func<bool>? filesWanted = null,
        int supervisorPid = 0)
    {
        _record = record;
        _outputDirectory = outputDirectory;
        _options = options;
        _onFrame = onFrame;
        _onClosed = onClosed;
        _filesWanted = filesWanted ?? (() => true);
        _supervisorPid = supervisorPid > 0 ? supervisorPid : Environment.ProcessId;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// UX-18: how many frames this pump has published. A surface can compare it
    /// with the count it saw before to tell a live stream from a stopped one.
    /// </summary>
    public int PublishedFrames => Volatile.Read(ref _publishedFrames);

    /// <summary>
    /// UX-18: when the last frame was published, or null when the pump has not
    /// delivered one. The emulator's image stream is change driven, so this is
    /// the only honest way to tell an idle guest from a broken capture.
    ///
    /// EV-11: a field now. It used to enumerate the 5,000-sample trace under
    /// the trace lock, and `instance.status` paid for that on every poll.
    /// </summary>
    public DateTimeOffset? LastFrameUtc
    {
        get
        {
            var ticks = Volatile.Read(ref _lastFrameTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>EV-11: the shared-memory ring this pump publishes into, or null.</summary>
    public string? MapName => _ring?.Name;

    /// <summary>EV-11: the payload capacity of one ring slot, or 0 when it is not mapped.</summary>
    public int MapCapacity => _ring?.Capacity ?? 0;

    public string OutputPath => System.IO.Path.Combine(
        _outputDirectory,
        _record.Name + (_options.IsRaw ? ".bgr" : ".png"));

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

    /// <summary>
    /// EV-11: single-flight. Two subscribers that arrive while the first start
    /// is still reading the transport identity must not each open a controller
    /// stream, so the first start task is stored and every caller awaits it.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_startGate)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            if (_start is { IsCompleted: false })
            {
                return _start;
            }

            var start = StartCoreAsync(cancellationToken);
            _start = start;
            return start;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            var adb = new AdbClient(ResolveSdk().AdbExe);
            var discovery = await EmulatorDiscovery.ReadAsync(adb, _record.AdbSerial, cancellationToken)
                .ConfigureAwait(false);

            if (discovery.GrpcPort != _record.GrpcPort
                || discovery.AdbPort != _record.ConsolePort + 1
                || discovery.AvdName != _record.AvdName)
            {
                throw new InvalidOperationException(
                    "The advertised controller endpoint does not match the managed instance.");
            }

            var size = await adb.RunAsync(
                _record.AdbSerial,
                new[] { "shell", "wm", "size" },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            var match = PhysicalSize.Match(size.StandardOutput);
            _guestWidth = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 1080;
            _guestHeight = match.Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 2400;

            StartRing();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => RunAsync(discovery, _cts.Token), CancellationToken.None);
        }
        catch
        {
            // A start that failed leaves nothing behind, so the next caller may
            // try again instead of awaiting the same failure forever.
            lock (_startGate)
            {
                _start = null;
            }

            throw;
        }
    }

    /// <summary>
    /// EV-11: one ring per pump. The capacity is one byte per pixel above RGB,
    /// so the measured 540x1200 frame has room to spare; a controller frame
    /// that does not fit keeps the file path instead of being dropped.
    /// </summary>
    private void StartRing()
    {
        if (_options.RingFormat == FrameRingFormat.None || _ring is not null)
        {
            return;
        }

        var width = Math.Max(1, _options.Width);
        var height = Math.Max(1, _options.Height);
        var capacity = checked(width * height * 4);
        var ring = FrameRingWriter.Create(_record.Name, _supervisorPid, capacity, out var error);
        if (ring is null)
        {
            if (!_ringFailedLogged)
            {
                _ringFailedLogged = true;
                SupervisorDiagnostics.Write(
                    $"Frame ring for '{_record.Name}' could not be mapped; frames stay file based: {error}");
            }

            return;
        }

        _ring = ring;
        SupervisorDiagnostics.Write(
            $"Frame ring '{ring.Name}' mapped ({FrameRingCore.SlotCount} slots of {ring.Capacity} bytes).");
    }

    public async Task StopAsync()
    {
        Task? start;
        lock (_startGate)
        {
            start = _start;
            _start = null;
        }

        if (start is not null)
        {
            try
            {
                await start.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A start that failed has no loop to stop.
            }
        }

        if (_cts is null)
        {
            DisposeRing();
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
        DisposeRing();
    }

    private void DisposeRing()
    {
        _ring?.Dispose();
        _ring = null;
    }

    private async Task RunAsync(EmulatorDiscovery discovery, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var channel = EmulatorControlChannel.Connect(discovery.GrpcPort, discovery.GrpcToken, "bearer");
                if (_options.IsRaw)
                {
                    var request = new Android.Emulation.Control.ImageFormat
                    {
                        Format = Android.Emulation.Control.ImageFormat.Types.ImgFormat.Rgb888,
                        Width = (uint)Math.Max(0, _options.Width),
                        Height = (uint)Math.Max(0, _options.Height),
                    };

                    await foreach (var image in channel.StreamImagesAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        attempt = 0;
                        await PublishAsync(
                            image.Image_.Memory,
                            (int)image.Format.Width,
                            (int)image.Format.Height,
                            image.Seq,
                            image.TimestampUs).ConfigureAwait(false);
                    }
                }
                else
                {
                    await foreach (var frame in channel.StreamScreenshotsAsync(_options.Width, cancellationToken).ConfigureAwait(false))
                    {
                        attempt = 0;
                        await PublishAsync(frame.PngBytes, 0, 0, frame.Sequence, frame.TimestampMicroseconds)
                            .ConfigureAwait(false);
                    }
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
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task PublishAsync(ReadOnlyMemory<byte> payload, int width, int height, uint sequence, ulong timestampUs)
    {
        var received = DateTimeOffset.UtcNow;
        // A 60 Hz source can deliver frames less than 16 ms apart when the
        // transport catches up. Leave enough headroom for that jitter.
        var minimumIntervalMs = _options.IsRaw ? 8 : 16;
        if (_lastPublished != default && (received - _lastPublished).TotalMilliseconds < minimumIntervalMs)
        {
            return;
        }

        if (_options.IsRaw)
        {
            if (width <= 0 || height <= 0 || payload.Length != width * height * 3)
            {
                return;
            }
        }
        else if (width == 0)
        {
            (width, height) = ReadPngSize(payload.Span);
            if (width <= 0)
            {
                return;
            }
        }

        var dropped = 0;
        if (_lastSequence != 0 && sequence > _lastSequence + 1)
        {
            dropped = checked((int)(sequence - _lastSequence - 1));
        }

        var stride = width * 3;
        var slot = -1;
        if (_ring is { } ring)
        {
            if (payload.Length <= ring.Capacity)
            {
                slot = ring.Write(payload.Span, width, height, stride,
                    _options.IsRaw ? FrameRingFormat.Rgb24 : FrameRingFormat.None, sequence, dropped);
            }
            else if (!_oversizeLogged)
            {
                _oversizeLogged = true;
                SupervisorDiagnostics.Write(
                    $"Frame {width}x{height} ({payload.Length} bytes) exceeds ring slot " +
                    $"{ring.Capacity}; it stays file based.");
            }
        }

        // EV-11: the file is only a delivery mechanism for subscribers that ask
        // for one. A desktop that reads the ring must not make the supervisor
        // churn *.bgr files in frames/.
        var publishedPath = string.Empty;
        if (FrameFilePolicy.NeedsFile(slot, FilesWanted()))
        {
            publishedPath = await WriteFileAsync(payload, sequence).ConfigureAwait(false);
        }

        _lastPublished = received;
        Interlocked.Increment(ref _publishedFrames);
        Volatile.Write(ref _lastFrameTicks, received.UtcTicks);

        var interval = _lastReceived == default ? 0 : (received - _lastReceived).TotalMilliseconds;
        _lastReceived = received;
        _lastSequence = sequence;

        var sample = new FrameSample(
            sequence, timestampUs, received, width, height, payload.Length, interval, dropped);
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
            sequence,
            width,
            height,
            stride,
            _options.IsRaw ? "rgb24" : "png",
            _guestWidth,
            _guestHeight,
            payload.Length,
            timestampUs,
            received,
            DateTimeOffset.UtcNow,
            publishedPath,
            dropped,
            slot,
            slot >= 0 ? _ring!.Name : null);

        try
        {
            await _onFrame(frameEvent).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A disconnected subscriber must not stop capture.
        }
    }

    private bool FilesWanted() => _filesWanted();

    private async Task<string> WriteFileAsync(ReadOnlyMemory<byte> payload, uint sequence)
    {
        if (_options.IsRaw)
        {
            var publishedPath = System.IO.Path.Combine(_outputDirectory, $"{_record.Name}-{sequence}.bgr");
            await File.WriteAllBytesAsync(publishedPath, payload).ConfigureAwait(false);
            lock (_rawFiles)
            {
                _rawFiles.Enqueue(publishedPath);
                while (_rawFiles.Count > 6)
                {
                    var stale = _rawFiles.Dequeue();
                    try
                    {
                        File.Delete(stale);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            return publishedPath;
        }

        var temp = OutputPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllBytesAsync(temp, payload).ConfigureAwait(false);
        File.Move(temp, OutputPath, overwrite: true);
        return OutputPath;
    }

    private static (int Width, int Height) ReadPngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50)
        {
            return (0, 0);
        }

        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
    }

    private static SdkLocator ResolveSdk() => SdkLocator.Resolve();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
