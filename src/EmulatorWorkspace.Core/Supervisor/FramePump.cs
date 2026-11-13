using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Android.Emulation.Control;
using EmulatorWorkspace.Controller;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Instances;

namespace EmulatorWorkspace.Core.Supervisor;

public sealed record FramePumpOptions(string Format, int Width, int Height)
{
    public bool IsRaw => Format.Equals("rgb", StringComparison.OrdinalIgnoreCase)
                         || Format.Equals("bgr", StringComparison.OrdinalIgnoreCase);
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
/// One latest-frame pipeline per instance. The engine stream is read on a
/// background task and only the newest frame is exposed: either a raw BGR24
/// file for the interactive viewport or a PNG file for one-shot/fallback use.
/// Stale frames are overwritten, never queued.
/// </summary>
public sealed class FramePump : IAsyncDisposable
{
    private static readonly Regex PhysicalSize = new(@"Physical size:\s*(\d+)x(\d+)", RegexOptions.Compiled);

    private readonly InstanceRecord _record;
    private readonly string _outputDirectory;
    private readonly FramePumpOptions _options;
    private readonly Func<FrameEvent, Task> _onFrame;
    private readonly Func<string, string, Task> _onClosed;
    private readonly object _traceGate = new();
    private readonly Queue<FrameSample> _trace = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private uint _lastSequence;
    private DateTimeOffset _lastReceived;
    private DateTimeOffset _lastPublished;
    private readonly Queue<string> _rawFiles = new();
    private int _guestWidth;
    private int _guestHeight;

    public FramePump(
        InstanceRecord record,
        string outputDirectory,
        FramePumpOptions options,
        Func<FrameEvent, Task> onFrame,
        Func<string, string, Task> onClosed)
    {
        _record = record;
        _outputDirectory = outputDirectory;
        _options = options;
        _onFrame = onFrame;
        _onClosed = onClosed;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

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

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(discovery, _cts.Token), CancellationToken.None);
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

                    await StreamRawAsync(channel, request, () => attempt = 0, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await foreach (var frame in channel.StreamScreenshotsAsync(_options.Width, cancellationToken).ConfigureAwait(false))
                    {
                        attempt = 0;
                        await PublishAsync(frame.PngBytes, 0, 0, frame.Sequence, frame.TimestampMicroseconds, DateTimeOffset.UtcNow)
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

    private async Task StreamRawAsync(
        EmulatorControlChannel controller, ImageFormat request, Action onFrame, CancellationToken cancellationToken)
    {
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = Channel.CreateBounded<(Image Frame, DateTimeOffset ReceivedUtc)>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        async Task ReadAsync()
        {
            try
            {
                await foreach (var frame in controller.StreamImagesAsync(request, stream.Token).ConfigureAwait(false))
                {
                    pending.Writer.TryWrite((frame, DateTimeOffset.UtcNow));
                }
            }
            finally
            {
                pending.Writer.TryComplete();
            }
        }

        async Task PublishFramesAsync()
        {
            await foreach (var (frame, receivedUtc) in pending.Reader.ReadAllAsync(stream.Token).ConfigureAwait(false))
            {
                stream.Token.ThrowIfCancellationRequested();
                await PublishAsync(frame.Image_.Memory, (int)frame.Format.Width, (int)frame.Format.Height,
                    frame.Seq, frame.TimestampUs, receivedUtc).ConfigureAwait(false);
                onFrame();
            }
        }

        var read = ReadAsync();
        var publish = PublishFramesAsync();
        var first = await Task.WhenAny(read, publish).ConfigureAwait(false);
        if (first == publish && publish.IsFaulted)
        {
            await stream.CancelAsync().ConfigureAwait(false);
        }
        await Task.WhenAll(publish, read).ConfigureAwait(false);
    }

    private async Task PublishAsync(ReadOnlyMemory<byte> payload, int width, int height, uint sequence, ulong timestampUs,
        DateTimeOffset received)
    {
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

        string publishedPath;
        if (_options.IsRaw)
        {
            publishedPath = System.IO.Path.Combine(_outputDirectory, $"{_record.Name}-{sequence}.bgr");
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
        }
        else
        {
            var temp = OutputPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(temp, payload).ConfigureAwait(false);
            File.Move(temp, OutputPath, overwrite: true);
            publishedPath = OutputPath;
        }

        _lastPublished = received;

        var dropped = 0;
        if (_lastSequence != 0 && sequence > _lastSequence + 1)
        {
            dropped = checked((int)(sequence - _lastSequence - 1));
        }

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
            width * 3,
            _options.IsRaw ? "rgb24" : "png",
            _guestWidth,
            _guestHeight,
            payload.Length,
            timestampUs,
            received,
            DateTimeOffset.UtcNow,
            publishedPath,
            dropped);

        try
        {
            await _onFrame(frameEvent).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A disconnected subscriber must not stop capture.
        }
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
