using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-01: the pipe client owns its write serialisation and its open/closed
/// state, so concurrent callers cannot interleave frames and a dead pipe fails
/// the next request at once instead of after the ten-minute deadline.
/// </summary>
public class SupervisorConnectionTests
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ConcurrentRequests_AreNeverInterleaved()
    {
        const int count = 64;

        await using var pair = await PipePair.CreateAsync();
        var server = pair.Server;
        var connection = pair.Connection;

        var received = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var broken = 0;

        var serverLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var message = await PipeFraming.ReadAsync(
                        server, SupervisorProtocol.MaxRequestBytes, CancellationToken.None);
                    if (message is null)
                    {
                        break;
                    }

                    var request = JsonSerializer.Deserialize<RequestEnvelope>(
                        message.ToJsonString(), CaseInsensitive);
                    Assert.NotNull(request);
                    received[request!.Id] = JsonArgs.Int(request.Args, "i") ?? -1;
                    await WriteResponseAsync(server, request.Id);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A bad frame length lands here: the reader rejected the frame.
                Interlocked.Exchange(ref broken, 1);
            }
        });

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                gate.Wait();
                return await connection.RequestAsync("test.op", new JsonObject { ["i"] = i });
            }))
            .ToArray();

        gate.Set();

        var responses = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, Volatile.Read(ref broken));
        Assert.Equal(count, received.Count);
        Assert.Equal(count, received.Values.Distinct().Count());
        Assert.Equal(count, responses.Length);

        for (var i = 0; i < count; i++)
        {
            Assert.True(responses[i].Ok, $"request {i} answered {responses[i].Code}");
            Assert.True(received.TryGetValue(responses[i].Id, out var sent), $"response {responses[i].Id} was never sent");
            Assert.Equal(i, sent);
        }

        Assert.True(connection.IsOpen);
        await pair.DisposeAsync();
        await serverLoop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestAfterClose_FailsFast()
    {
        var pair = await PipePair.CreateAsync();
        await using var _ = pair;
        var connection = pair.Connection;

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += () => closed.TrySetResult();

        pair.Server.Dispose();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SupervisorUnavailableException>(
            () => connection.RequestAsync("test.op", new JsonObject()));
        stopwatch.Stop();

        Assert.False(connection.IsOpen);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"the request took {stopwatch.Elapsed.TotalSeconds:0.000}s after close");
    }

    [Fact]
    public async Task PendingRequest_FailsWhenPipeCloses()
    {
        var pair = await PipePair.CreateAsync();
        await using var _ = pair;
        var server = pair.Server;
        var connection = pair.Connection;

        var readOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverLoop = Task.Run(async () =>
        {
            try
            {
                var message = await PipeFraming.ReadAsync(
                    server, SupervisorProtocol.MaxRequestBytes, CancellationToken.None);
                if (message is not null)
                {
                    readOne.TrySetResult();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        });

        var pending = connection.RequestAsync("test.op", new JsonObject());
        await readOne.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        pair.Server.Dispose();

        await Assert.ThrowsAsync<SupervisorUnavailableException>(() => pending);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"the pending request took {stopwatch.Elapsed.TotalSeconds:0.000}s to fail");

        await serverLoop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FrameBytes_AreUnchanged()
    {
        var message = new ResponseEnvelope
        {
            Id = "0123456789abcdef0123456789abcdef",
            Ok = true,
            Code = "ok",
            Message = "fine",
            DurationMs = 42,
        };

        using var stream = new MemoryStream();
        await PipeFraming.WriteAsync(stream, message, CancellationToken.None);

        var expectedJson = JsonSerializer.SerializeToUtf8Bytes(message, PipeFraming.JsonOptions);
        var actual = stream.ToArray();

        Assert.Equal(expectedJson.Length + 4, actual.Length);
        Assert.Equal(expectedJson.Length, BinaryPrimitives.ReadInt32LittleEndian(actual.AsSpan(0, 4)));
        Assert.Equal(expectedJson, actual[4..]);
    }

    [Fact]
    public async Task MalformedFrame_EndsTheReaderWithTheSameErrorHandling()
    {
        var pair = await PipePair.CreateAsync();
        await using var _ = pair;
        var server = pair.Server;
        var connection = pair.Connection;

        var trace = new ConcurrentQueue<string>();
        connection.Trace = line => trace.Enqueue(line);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += () => closed.TrySetResult();

        // A frame whose length prefix is valid and whose payload is not JSON.
        var json = "{\"type\":\"event\",\"event\":"u8.ToArray();
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame.AsSpan(4));
        await server.WriteAsync(frame);
        await server.FlushAsync();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(connection.IsOpen);
        Assert.Contains(trace, line => line.StartsWith("reader error:", StringComparison.Ordinal));

        await Assert.ThrowsAsync<SupervisorUnavailableException>(
            () => connection.RequestAsync("test.op", new JsonObject()));
    }

    private static async Task WriteResponseAsync(Stream stream, string id)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new ResponseEnvelope { Id = id, Ok = true }, PipeFraming.JsonOptions);
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private sealed class PipePair : IAsyncDisposable
    {
        private PipePair(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            Server = server;
            Connection = SupervisorConnection.FromConnectedPipe(client);
        }

        public NamedPipeServerStream Server { get; }

        public SupervisorConnection Connection { get; }

        public static async Task<PipePair> CreateAsync()
        {
            var name = "ew-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(
                ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                var connecting = client.ConnectAsync(5000);
                await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await connecting.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                server.Dispose();
                client.Dispose();
                throw;
            }

            return new PipePair(server, client);
        }

        public async ValueTask DisposeAsync()
        {
            Server.Dispose();
            await Connection.DisposeAsync();
        }
    }
}
