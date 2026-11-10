using Android.Emulation.Control;
using Grpc.Core;
using Grpc.Net.Client;

namespace EmulatorWorkspace.Controller;

/// <summary>
/// Authenticated client for the emulator's own gRPC controller. The engine
/// binds this service to loopback and (with <c>-grpc-use-token</c>) requires the
/// per-user console token on every call.
/// </summary>
public sealed class EmulatorControlChannel : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly Metadata _metadata;
    private readonly EmulatorController.EmulatorControllerClient _controller;
    private readonly SnapshotService.SnapshotServiceClient _snapshots;

    private EmulatorControlChannel(GrpcChannel channel, string? token, string scheme)
    {
        _channel = channel;
        _metadata = new Metadata();
        if (!string.IsNullOrWhiteSpace(token))
        {
            var value = scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase)
                ? "Bearer " + token.Trim()
                : token.Trim();
            _metadata.Add("authorization", value);
        }

        _controller = new EmulatorController.EmulatorControllerClient(channel);
        _snapshots = new SnapshotService.SnapshotServiceClient(channel);
    }

    public static EmulatorControlChannel Connect(int port, string? token, string scheme = "raw")
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Controller port must be 1-65535.");
        }

        var channel = GrpcChannel.ForAddress(
            $"http://127.0.0.1:{port}",
            new GrpcChannelOptions
            {
                MaxReceiveMessageSize = 16 * 1024 * 1024,
            });

        return new EmulatorControlChannel(channel, token, scheme);
    }

    public Task<EmulatorStatus> GetStatusAsync(TimeSpan? deadline = null, CancellationToken cancellationToken = default) =>
        _controller.getStatusAsync(
            new Google.Protobuf.WellKnownTypes.Empty(),
            CreateOptions(deadline, cancellationToken)).ResponseAsync;

    public async Task<byte[]> GetScreenshotPngAsync(
        int? width = null,
        int? height = null,
        TimeSpan? deadline = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png };
        if (width is > 0)
        {
            request.Width = (uint)width.Value;
        }

        if (height is > 0)
        {
            request.Height = (uint)height.Value;
        }

        var image = await _controller.getScreenshotAsync(request, CreateOptions(deadline, cancellationToken))
            .ResponseAsync.ConfigureAwait(false);
        return image.Image_.ToByteArray();
    }

    /// <summary>
    /// Streams screenshots at the engine's natural pace. Each yielded frame
    /// carries the engine sequence number and timestamp so callers can reject
    /// stale frames instead of treating them as current.
    /// </summary>
    public async IAsyncEnumerable<ControllerFrame> StreamScreenshotsAsync(
        int? width = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png };
        if (width is > 0)
        {
            request.Width = (uint)width.Value;
        }

        using var call = _controller.streamScreenshot(request, CreateOptions(null, cancellationToken));
        await foreach (var image in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ControllerFrame(
                image.Image_.ToByteArray(),
                image.Seq,
                image.TimestampUs,
                DateTimeOffset.UtcNow);
        }
    }

    public async Task TapAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        // The engine maps each touch identifier to a Linux multitouch slot and
        // treats pressure 0 as "lift". A real tap needs a contact frame, a short
        // dwell, and an explicit release with the same identifier.
        var down = new Touch
        {
            X = x,
            Y = y,
            Identifier = 1,
            Pressure = 1024,
            TouchMajor = 8,
            TouchMinor = 8,
            Expiration = Touch.Types.EventExpiration.NeverExpire,
        };

        await _controller.sendTouchAsync(
            new TouchEvent { Touches = { down } },
            CreateOptions(TimeSpan.FromSeconds(20), cancellationToken)).ResponseAsync.ConfigureAwait(false);

        await Task.Delay(90, cancellationToken).ConfigureAwait(false);

        var up = new Touch
        {
            X = x,
            Y = y,
            Identifier = 1,
            Pressure = 0,
            Expiration = Touch.Types.EventExpiration.NeverExpire,
        };

        await _controller.sendTouchAsync(
            new TouchEvent { Touches = { up } },
            CreateOptions(TimeSpan.FromSeconds(20), cancellationToken)).ResponseAsync.ConfigureAwait(false);
    }

    public async Task SwipeAsync(
        int fromX,
        int fromY,
        int toX,
        int toY,
        int durationMs,
        CancellationToken cancellationToken = default)
    {
        var steps = Math.Clamp(durationMs / 16, 4, 40);
        var delay = Math.Max(1, durationMs / steps);

        async Task SendAsync(int x, int y, int pressure)
        {
            var touch = new Touch
            {
                X = x,
                Y = y,
                Identifier = 2,
                Pressure = pressure,
                TouchMajor = 8,
                TouchMinor = 8,
                Expiration = Touch.Types.EventExpiration.NeverExpire,
            };
            await _controller.sendTouchAsync(
                new TouchEvent { Touches = { touch } },
                CreateOptions(TimeSpan.FromSeconds(20), cancellationToken)).ResponseAsync.ConfigureAwait(false);
        }

        await SendAsync(fromX, fromY, 1024).ConfigureAwait(false);
        for (var step = 1; step < steps; step++)
        {
            var fraction = step / (double)steps;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            await SendAsync(
                (int)Math.Round(fromX + (toX - fromX) * fraction),
                (int)Math.Round(fromY + (toY - fromY) * fraction),
                1024).ConfigureAwait(false);
        }

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        await SendAsync(toX, toY, 0).ConfigureAwait(false);
    }

    public async Task<SnapshotResult> SaveSnapshotAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var package = new SnapshotPackage { SnapshotId = snapshotId };
        var response = await _snapshots.SaveSnapshotAsync(
            package,
            CreateOptions(TimeSpan.FromMinutes(5), cancellationToken)).ResponseAsync.ConfigureAwait(false);
        return new SnapshotResult(response.Success, response.Err.ToStringUtf8(), snapshotId);
    }

    public async Task<SnapshotResult> LoadSnapshotAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var package = new SnapshotPackage { SnapshotId = snapshotId };
        var response = await _snapshots.LoadSnapshotAsync(
            package,
            CreateOptions(TimeSpan.FromMinutes(5), cancellationToken)).ResponseAsync.ConfigureAwait(false);
        return new SnapshotResult(response.Success, response.Err.ToStringUtf8(), snapshotId);
    }

    public async Task<IReadOnlyList<string>> ListSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var list = await _snapshots.ListSnapshotsAsync(
            new SnapshotFilter { StatusFilter = SnapshotFilter.Types.LoadStatus.All },
            CreateOptions(TimeSpan.FromSeconds(30), cancellationToken)).ResponseAsync.ConfigureAwait(false);

        return list.Snapshots.Select(s => s.SnapshotId).ToList();
    }

    private CallOptions CreateOptions(TimeSpan? deadline, CancellationToken cancellationToken) =>
        new CallOptions(
            _metadata,
            deadline: deadline is null ? null : DateTime.UtcNow.Add(deadline.Value),
            cancellationToken: cancellationToken);

    public void Dispose() => _channel.Dispose();
}

public sealed record ControllerFrame(byte[] PngBytes, uint Sequence, ulong TimestampMicroseconds, DateTimeOffset ReceivedUtc);

public sealed record SnapshotResult(bool Success, string Error, string SnapshotId);


