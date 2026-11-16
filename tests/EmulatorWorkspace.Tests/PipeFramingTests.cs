using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;
using Xunit;
using Xunit.Abstractions;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-09: a frame is read once into a pooled buffer, its head is peeked in place
/// and the envelope is deserialised once with one shared case-insensitive
/// options instance. These tests pin the three envelope shapes on that path, the
/// error behaviour the document read had, and the allocation the old
/// <c>ToJsonString</c> round-trip cost per frame.
/// </summary>
public class PipeFramingTests
{
    private const int Iterations = 10_000;
    private const int Warmup = 500;

    private readonly ITestOutputHelper _output;

    public PipeFramingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ResponseEnvelope_RoundTripsThroughThePooledRead()
    {
        var sent = new ResponseEnvelope
        {
            Id = "0123456789abcdef0123456789abcdef",
            Ok = false,
            Code = ErrorCodes.Timeout,
            Message = "The guest did not answer within 2000 ms.",
            DurationMs = 2003,
            Data = new JsonObject { ["path"] = @"C:\ws\artifacts\shot.png", ["bytes"] = 8192 },
        };

        var framed = await FrameAsync(sent);
        await using var stream = new MemoryStream(framed, writable: false);
        using var frame = await PipeFraming.ReadAsync(
            stream, SupervisorProtocol.MaxResponseBytes, ArrayPool<byte>.Shared, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(framed.Length - 4, frame!.Length);

        var (type, id) = PipeFraming.PeekHead(frame.Span);
        Assert.Equal("response", type);
        Assert.Equal(sent.Id, id);

        var read = JsonSerializer.Deserialize<ResponseEnvelope>(frame.Span, PipeFraming.ReadJsonOptions);
        Assert.NotNull(read);
        Assert.Equal(sent.Id, read!.Id);
        Assert.False(read.Ok);
        Assert.Equal(sent.Code, read.Code);
        Assert.Equal(sent.Message, read.Message);
        Assert.Equal(sent.DurationMs, read.DurationMs);
        Assert.Equal(@"C:\ws\artifacts\shot.png", read.Data!["path"]!.GetValue<string>());
        Assert.Equal(8192, read.Data["bytes"]!.GetValue<int>());
    }

    [Fact]
    public async Task FrameEventEnvelope_RoundTripsThroughThePooledRead()
    {
        var sent = FrameEvent(1234);
        var pool = new TrackingPool();

        var framed = await FrameAsync(sent);
        using var stream = new MemoryStream(framed, writable: false);
        var frame = await PipeFraming.ReadAsync(
            stream, SupervisorProtocol.MaxResponseBytes, pool, CancellationToken.None);

        Assert.NotNull(frame);
        var (type, _) = PipeFraming.PeekHead(frame!.Span);
        Assert.Equal("event", type);

        var read = JsonSerializer.Deserialize<EventEnvelope>(frame.Span, PipeFraming.ReadJsonOptions);

        // The lease goes back before the envelope is read: a typed deserialize
        // copies everything it keeps, so overwriting the returned buffer must not
        // touch the envelope the client is about to dispatch.
        var rented = pool.LastRented;
        frame.Dispose();
        Assert.Equal(1, pool.Returns);
        Array.Fill(rented!, (byte)0xFF);

        Assert.NotNull(read);
        Assert.Equal(EventNames.Frame, read!.Event);
        Assert.Equal(sent.Subscription, read.Subscription);

        var data = Assert.IsType<JsonObject>(read.Data);
        Assert.Equal(1234, data["seq"]!.GetValue<int>());
        Assert.Equal(1080, data["width"]!.GetValue<int>());
        Assert.Equal(2400, data["height"]!.GetValue<int>());
        Assert.Equal(3240, data["stride"]!.GetValue<int>());
        Assert.Equal("png", data["format"]!.GetValue<string>());
        Assert.Equal(1080, data["guestWidth"]!.GetValue<int>());
        Assert.Equal(2400, data["guestHeight"]!.GetValue<int>());
        Assert.Equal(245760, data["bytes"]!.GetValue<int>());
        Assert.Equal(12345678901234, data["emulatorTimestampUs"]!.GetValue<long>());
        Assert.Equal("2026-11-16T09:00:00.0000000+00:00", data["receivedUtc"]!.GetValue<string>());
        Assert.Equal("2026-11-16T09:00:00.0100000+00:00", data["writtenUtc"]!.GetValue<string>());
        Assert.Equal(@"C:\ws\frames\ev09-a\00001234.png", data["path"]!.GetValue<string>());
        Assert.Equal(0, data["dropped"]!.GetValue<int>());
    }

    [Fact]
    public async Task CancelEnvelope_PeekReadsTypeAndIdWithoutDeserialising()
    {
        var sent = new CancelEnvelope { Id = "cancel-target-id" };

        var framed = await FrameAsync(sent);
        await using var stream = new MemoryStream(framed, writable: false);
        using var frame = await PipeFraming.ReadAsync(
            stream, SupervisorProtocol.MaxRequestBytes, ArrayPool<byte>.Shared, CancellationToken.None);

        Assert.NotNull(frame);
        var (type, id) = PipeFraming.PeekHead(frame!.Span);
        Assert.Equal("cancel", type);
        Assert.Equal("cancel-target-id", id);

        // The request loop only needs the head for a cancel; the shape still
        // deserialises the same way for a caller that wants the object.
        var read = JsonSerializer.Deserialize<CancelEnvelope>(frame.Span, PipeFraming.ReadJsonOptions);
        Assert.Equal("cancel-target-id", read!.Id);
    }

    [Fact]
    public void PeekHead_ReadsTheHeadAndSkipsNestedValues()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"args":{"type":"nested","id":"nested"},"type":"request","id":"abc","list":[{"id":"x"}],"n":3}""");

        var (type, id) = PipeFraming.PeekHead(payload);

        Assert.Equal("request", type);
        Assert.Equal("abc", id);
    }

    [Fact]
    public void PeekHead_KeepsTheScalarTextTheDocumentReadReturned()
    {
        var number = PipeFraming.PeekHead(Encoding.UTF8.GetBytes("""{"type":5,"id":true}"""));
        Assert.Equal("5", number.Type);
        Assert.Equal("true", number.Id);

        var nulls = PipeFraming.PeekHead(Encoding.UTF8.GetBytes("""{"type":null,"id":{}}"""));
        Assert.Null(nulls.Type);
        Assert.Null(nulls.Id);

        // A head that is an object is skipped; the property after it is still read.
        var afterObject = PipeFraming.PeekHead(Encoding.UTF8.GetBytes("""{"id":{},"type":"event"}"""));
        Assert.Null(afterObject.Id);
        Assert.Equal("event", afterObject.Type);

        var noHead = PipeFraming.PeekHead(Encoding.UTF8.GetBytes("""{"event":"x"}"""));
        Assert.Null(noHead.Type);
        Assert.Null(noHead.Id);
    }

    [Fact]
    public async Task PooledRead_ReturnsTheBufferOnDispose()
    {
        var pool = new TrackingPool();
        var framed = await FrameAsync(FrameEvent(7));

        using var stream = new MemoryStream(framed, writable: false);
        var frame = await PipeFraming.ReadAsync(
            stream, SupervisorProtocol.MaxResponseBytes, pool, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(1, pool.Rentals);
        Assert.Equal(0, pool.Returns);

        var firstBuffer = pool.LastRented;
        Assert.Equal(framed.Length - 4, frame!.Payload.Length);
        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, pool.Returns);

        // The same rented array goes out again: the read really is pooled.
        using var second = new MemoryStream(framed, writable: false);
        var again = await PipeFraming.ReadAsync(
            second, SupervisorProtocol.MaxResponseBytes, pool, CancellationToken.None);
        Assert.NotNull(again);
        Assert.True(ReferenceEquals(firstBuffer, pool.LastRented));
        Assert.Equal(2, pool.Rentals);
        again!.Dispose();
        Assert.Equal(2, pool.Returns);
    }

    [Fact]
    public async Task PooledRead_CleanEndOfStreamIsNull()
    {
        await using var stream = new MemoryStream(Array.Empty<byte>(), writable: false);
        var frame = await PipeFraming.ReadAsync(
            stream, SupervisorProtocol.MaxResponseBytes, ArrayPool<byte>.Shared, CancellationToken.None);
        Assert.Null(frame);
    }

    [Fact]
    public async Task PooledRead_KeepsTheDocumentReadErrorBehaviour()
    {
        // Malformed JSON: the document read threw JsonException; the pooled read
        // and the head peek throw the same type, so a client still reports a
        // reader error and a session still ends.
        var malformed = Encoding.UTF8.GetBytes("""{"type":"event","event":""");
        await using var stream = new MemoryStream(Framed(malformed), writable: false);
        using var frame = await PipeFraming.ReadAsync(
            stream, SupervisorProtocol.MaxResponseBytes, ArrayPool<byte>.Shared, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.ThrowsAny<JsonException>(() => PipeFraming.PeekHead(frame!.Span));

        await using var oldStream = new MemoryStream(Framed(malformed), writable: false);
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            PipeFraming.ReadAsync(oldStream, SupervisorProtocol.MaxResponseBytes, CancellationToken.None));

        // A well-formed frame that is not an object: the same IOException text.
        var array = Encoding.UTF8.GetBytes("[1,2,3]");
        var objectError = await Assert.ThrowsAsync<IOException>(() =>
            PipeFraming.ReadAsync(
                new MemoryStream(Framed(array), writable: false),
                SupervisorProtocol.MaxResponseBytes,
                CancellationToken.None));
        Assert.Equal("Supervisor frame was not a JSON object.", objectError.Message);

        var peekError = Assert.Throws<IOException>(() => PipeFraming.PeekHead(array));
        Assert.Equal("Supervisor frame was not a JSON object.", peekError.Message);

        // An empty or whitespace payload carries no token: still a JsonException,
        // exactly as the document parse reported it.
        Assert.ThrowsAny<JsonException>(() => PipeFraming.PeekHead(ReadOnlySpan<byte>.Empty));
        Assert.ThrowsAny<JsonException>(() => PipeFraming.PeekHead("   "u8));
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            PipeFraming.ReadAsync(
                new MemoryStream(Framed("  "u8.ToArray()), writable: false),
                SupervisorProtocol.MaxResponseBytes,
                CancellationToken.None));
    }

    [Fact]
    public async Task FrameEventParse_AllocationsDropByHalfAtLeast()
    {
        var framed = await FrameAsync(FrameEvent(1));
        var before = MeasureOld(framed);
        var after = MeasureAfter(framed);

        var reduction = 1 - ((double)after.BytesPerMessage / before.BytesPerMessage);

        _output.WriteLine(
            $"before {before.BytesPerMessage:0} B/msg {before.MicrosecondsPerMessage:0.00} us/msg; " +
            $"after {after.BytesPerMessage:0} B/msg {after.MicrosecondsPerMessage:0.00} us/msg; " +
            $"reduction {reduction:P1}");

        var path = WriteReport(framed, before, after, reduction);
        _output.WriteLine($"written to {path}");

        Assert.True(before.BytesPerMessage > 0);
        Assert.True(
            after.BytesPerMessage < before.BytesPerMessage * 0.5,
            $"the pooled read allocated {after.BytesPerMessage:0} B/msg against {before.BytesPerMessage:0} B/msg " +
            $"({reduction:P1} fewer)");
    }

    private static Measurement MeasureOld(byte[] framed)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var stream = new MemoryStream(framed, writable: false);

        for (var i = 0; i < Warmup; i++)
        {
            stream.Position = 0;
            ParseOld(stream, options);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++)
        {
            stream.Position = 0;
            ParseOld(stream, options);
        }

        stopwatch.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        return new Measurement(bytes, Iterations, stopwatch.Elapsed);
    }

    private static Measurement MeasureAfter(byte[] framed)
    {
        using var stream = new MemoryStream(framed, writable: false);

        for (var i = 0; i < Warmup; i++)
        {
            stream.Position = 0;
            ParseAfter(stream);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++)
        {
            stream.Position = 0;
            ParseAfter(stream);
        }

        stopwatch.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        return new Measurement(bytes, Iterations, stopwatch.Elapsed);
    }

    /// <summary>What SupervisorConnection.ReadLoopAsync did before EV-09.</summary>
    private static void ParseOld(Stream stream, JsonSerializerOptions options)
    {
        var message = PipeFraming.ReadAsync(stream, SupervisorProtocol.MaxResponseBytes, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.NotNull(message);
        var type = JsonArgs.String(message, "type") ?? "response";
        if (type != "event")
        {
            return;
        }

        var envelope = JsonSerializer.Deserialize<EventEnvelope>(message!.ToJsonString(), options);
        Assert.NotNull(envelope);
    }

    /// <summary>What SupervisorConnection.ReadLoopAsync does after EV-09.</summary>
    private static void ParseAfter(Stream stream)
    {
        using var frame = PipeFraming.ReadAsync(
                stream, SupervisorProtocol.MaxResponseBytes, ArrayPool<byte>.Shared, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.NotNull(frame);

        var (peekedType, _) = PipeFraming.PeekHead(frame!.Span);
        if ((peekedType ?? "response") != "event")
        {
            return;
        }

        var envelope = JsonSerializer.Deserialize<EventEnvelope>(frame.Span, PipeFraming.ReadJsonOptions);
        Assert.NotNull(envelope);
    }

    private static async Task<byte[]> FrameAsync<T>(T message)
    {
        using var stream = new MemoryStream();
        await PipeFraming.WriteAsync(stream, message, CancellationToken.None);
        return stream.ToArray();
    }

    private static byte[] Framed(byte[] payload)
    {
        var framed = new byte[payload.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(4));
        return framed;
    }

    private static EventEnvelope FrameEvent(int sequence) => new()
    {
        Type = "event",
        Event = EventNames.Frame,
        Subscription = "frame-0123456789abcdef",
        Data = new JsonObject
        {
            ["seq"] = sequence,
            ["width"] = 1080,
            ["height"] = 2400,
            ["stride"] = 3240,
            ["format"] = "png",
            ["guestWidth"] = 1080,
            ["guestHeight"] = 2400,
            ["bytes"] = 245760,
            ["emulatorTimestampUs"] = 12345678901234L,
            ["receivedUtc"] = "2026-11-16T09:00:00.0000000+00:00",
            ["writtenUtc"] = "2026-11-16T09:00:00.0100000+00:00",
            ["path"] = @"C:\ws\frames\ev09-a\00001234.png",
            ["dropped"] = 0,
        },
    };

    private static string? WriteReport(byte[] framed, Measurement before, Measurement after, double reduction)
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        var directory = Path.Combine(root, "artifacts", "ev09");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pipe-json-allocations.txt");

        var text = new StringBuilder()
            .AppendLine("EV-09 pipe JSON single parse - allocation measurement")
            .AppendLine($"run: {DateTimeOffset.UtcNow:O}")
            .AppendLine($"host: {Environment.OSVersion.VersionString} / .NET {Environment.Version}")
            .AppendLine($"dotnet: {Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "system"}")
            .AppendLine("envelope: event/frame, 13 data fields (seq, width, height, stride, format, guestWidth,")
            .AppendLine("          guestHeight, bytes, emulatorTimestampUs, receivedUtc, writtenUtc, path, dropped)")
            .AppendLine($"framed bytes: {framed.Length} (4-byte header + {framed.Length - 4} JSON)")
            .AppendLine($"iterations: {Iterations} after {Warmup} warm-up frames per path")
            .AppendLine("measured with GC.GetAllocatedBytesForCurrentThread() on the test thread")
            .AppendLine()
            .AppendLine("before: PipeFraming.ReadAsync (new byte[] + JsonNode document), JsonArgs.String,")
            .AppendLine("        JsonObject.ToJsonString(), JsonSerializer.Deserialize(new JsonSerializerOptions)")
            .AppendLine($"  allocated  {before.Bytes,14:N0} B total   {before.BytesPerMessage,10:N0} B/message")
            .AppendLine($"  elapsed    {before.Elapsed.TotalMilliseconds,14:N1} ms        {before.MicrosecondsPerMessage,10:N2} us/message")
            .AppendLine()
            .AppendLine("after:  PipeFraming.ReadAsync (ArrayPool rent), PipeFraming.PeekHead (Utf8JsonReader),")
            .AppendLine("        JsonSerializer.Deserialize(ReadOnlySpan<byte>, PipeFraming.ReadJsonOptions)")
            .AppendLine($"  allocated  {after.Bytes,14:N0} B total   {after.BytesPerMessage,10:N0} B/message")
            .AppendLine($"  elapsed    {after.Elapsed.TotalMilliseconds,14:N1} ms        {after.MicrosecondsPerMessage,10:N2} us/message")
            .AppendLine()
            .AppendLine($"reduction: {reduction.ToString("P1", CultureInfo.InvariantCulture)} of allocated bytes per message")
            .ToString();

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmulatorWorkspace.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private readonly record struct Measurement(long Bytes, int Count, TimeSpan Elapsed)
    {
        public double BytesPerMessage => (double)Bytes / Count;

        public double MicrosecondsPerMessage => Elapsed.TotalMicroseconds / Count;
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public int Rentals { get; private set; }

        public int Returns { get; private set; }

        public byte[]? LastRented { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Rentals++;
            LastRented = ArrayPool<byte>.Shared.Rent(minimumLength);
            return LastRented;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Returns++;
            ArrayPool<byte>.Shared.Return(array, clearArray);
        }
    }
}
