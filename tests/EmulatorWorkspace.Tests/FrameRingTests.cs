using System.Text.Json.Nodes;
using EmulatorWorkspace.Core.Frames;
using EmulatorWorkspace.Core.Supervisor;
using EmulatorWorkspace.Contracts;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-11: the shared-memory frame ring's layout, its seqlock and its slot
/// choice, pinned over a plain byte array - the writer and the reader are
/// separate processes, so the only thing that can be wrong is what this memory
/// says.
/// </summary>
public sealed class FrameRingTests
{
    private const int Capacity = 64 * 64 * 3; // 12,288 bytes: two 64x64 RGB frames

    private static byte[] NewRing(int capacity = Capacity)
    {
        var ring = new byte[FrameRingCore.RequiredBytes(capacity)];
        FrameRingCore.Initialize(ring, capacity, 4242, 638000000000000000L);
        return ring;
    }

    private static byte[] Frame(int width, int height, byte seed = 0)
    {
        var payload = new byte[width * height * 3];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(seed + index);
        }

        return payload;
    }

    /// <summary>
    /// One slot's 64-byte header as words. The field indices are the layout the
    /// ring publishes, so a test that has to stand in for a reader inside a
    /// copy (or find the newest slot) reads it the way both processes do.
    /// </summary>
    private static Span<int> Header(byte[] ring, int slot, int capacity = Capacity) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(
            ring.AsSpan(FrameRingCore.SlotOffset(slot, capacity), FrameRingCore.SlotHeaderBytes));

    private const int SeqIndex = 0;
    private const int WidthIndex = 1;
    private const int ReadyIndex = 5;
    private const int ReadersIndex = 6;
    private const int GenerationIndex = 7;

    [Fact]
    public void Layout_DescribesWhatItInitialized()
    {
        var ring = NewRing();

        var descriptor = FrameRingCore.Describe(ring);

        Assert.NotNull(descriptor);
        Assert.Equal(FrameRingCore.Version, descriptor!.Value.Version);
        Assert.Equal(3, descriptor.Value.SlotCount);
        Assert.Equal(Capacity, descriptor.Value.Capacity);
        Assert.Equal(4242, descriptor.Value.OwnerPid);
        Assert.Equal(FrameRingCore.RequiredBytes(Capacity), ring.Length);
        // Every slot starts after the ring header, 64-byte aligned.
        for (var slot = 0; slot < FrameRingCore.SlotCount; slot++)
        {
            Assert.Equal(0, FrameRingCore.SlotOffset(slot, Capacity) % 64);
        }
    }

    [Fact]
    public void Layout_RejectsAnythingThatIsNotARing()
    {
        Assert.Null(FrameRingCore.Describe(new byte[16]));

        var neverInitialized = new byte[FrameRingCore.RequiredBytes(Capacity)];
        Assert.Null(FrameRingCore.Describe(neverInitialized));

        var truncated = new byte[FrameRingCore.RingHeaderBytes + 10];
        Assert.Null(FrameRingCore.Describe(truncated));
    }

    [Fact]
    public void Layout_RoundsCapacityUpSoEverySlotHeaderStaysAligned()
    {
        Assert.Equal(64, FrameRingCore.AlignCapacity(1));
        Assert.Equal(128, FrameRingCore.AlignCapacity(65));
        Assert.Equal(FrameRingCore.RingHeaderBytes + 3 * (FrameRingCore.SlotHeaderBytes + 128),
            FrameRingCore.RequiredBytes(65));
    }

    [Fact]
    public void Publish_ThenReadReturnsTheSameFrame()
    {
        var ring = NewRing();
        var payload = Frame(64, 64, 7);

        FrameRingCore.Publish(ring, 1, payload, 64, 64, 64 * 3, FrameRingFormat.Rgb24, 99, 3, 1, 123456789L);

        var destination = new byte[payload.Length];
        var status = FrameRingCore.TryRead(ring, 1, destination, 64 * 3, out var info);

        Assert.Equal(FrameRingReadStatus.Ok, status);
        Assert.Equal(payload, destination);
        Assert.Equal(64, info.Width);
        Assert.Equal(64, info.Height);
        Assert.Equal(64 * 3, info.Stride);
        Assert.Equal(FrameRingFormat.Rgb24, info.Format);
        Assert.Equal(payload.Length, info.Bytes);
        Assert.Equal(99u, info.EmulatorSequence);
        Assert.Equal(3, info.Dropped);
        Assert.Equal(123456789L, info.WrittenUtcTicks);
    }

    [Fact]
    public void Read_OfASlotNobodyWroteIsEmpty()
    {
        var ring = NewRing();

        var status = FrameRingCore.TryRead(ring, 2, new byte[Capacity], Capacity, out _);

        Assert.Equal(FrameRingReadStatus.Empty, status);
    }

    [Fact]
    public void Read_WhileTheSequenceIsOddIsTorn()
    {
        var ring = NewRing();
        var payload = Frame(64, 64);
        FrameRingCore.Publish(ring, 0, payload, 64, 64, 64 * 3, FrameRingFormat.Rgb24, 1, 0, 1, 1);

        // The writer's first act on a slot: make the sequence odd.
        var header = Header(ring, 0);
        header[0] = 3;

        var status = FrameRingCore.TryRead(ring, 0, new byte[Capacity], Capacity, out _);

        Assert.Equal(FrameRingReadStatus.Torn, status);
    }

    [Fact]
    public void IsStable_IsTheWholeTornReadRule()
    {
        // Even and unchanged: the frame was complete for the whole copy.
        Assert.True(FrameRingCore.IsStable(2, 2));
        Assert.True(FrameRingCore.IsStable(4096, 4096));

        // The writer started (odd) or finished another frame (even, moved):
        // either way the bytes the reader copied are not one frame.
        Assert.False(FrameRingCore.IsStable(2, 3));
        Assert.False(FrameRingCore.IsStable(3, 3));
        Assert.False(FrameRingCore.IsStable(3, 4));
        Assert.False(FrameRingCore.IsStable(2, 4));
    }


    [Fact]
    public void Read_RejectsAHeaderThatDoesNotDescribeAFrame()
    {
        var ring = NewRing();
        var payload = Frame(64, 64);
        FrameRingCore.Publish(ring, 0, payload, 64, 64, 64 * 3, FrameRingFormat.Rgb24, 1, 0, 1, 1);

        var header = Header(ring, 0);
        header[1] = 0; // width

        var status = FrameRingCore.TryRead(ring, 0, new byte[Capacity], Capacity, out _);

        Assert.Equal(FrameRingReadStatus.Invalid, status);
    }

    [Fact]
    public void Publish_RefusesAFrameLargerThanASlot()
    {
        var ring = NewRing();

        Assert.Throws<ArgumentException>(() =>
            FrameRingCore.Publish(ring, 0, new byte[Capacity + 1], 64, 64, 64 * 3,
                FrameRingFormat.Rgb24, 1, 0, 1, 1));
    }

    [Fact]
    public void SelectSlot_TakesTheOldestSlotInOrder()
    {
        var ring = NewRing();
        var payload = Frame(64, 64);

        Assert.Equal(0, FrameRingCore.SelectSlot(ring));
        FrameRingCore.Publish(ring, 0, payload, 64, 64, 192, FrameRingFormat.Rgb24, 1, 0, 1, 1);
        Assert.Equal(1, FrameRingCore.SelectSlot(ring));
        FrameRingCore.Publish(ring, 1, payload, 64, 64, 192, FrameRingFormat.Rgb24, 2, 0, 2, 2);
        Assert.Equal(2, FrameRingCore.SelectSlot(ring));
        FrameRingCore.Publish(ring, 2, payload, 64, 64, 192, FrameRingFormat.Rgb24, 3, 0, 3, 3);
        Assert.Equal(0, FrameRingCore.SelectSlot(ring));
    }

    [Fact]
    public void SelectSlot_SkipsTheSlotAReaderIsInside()
    {
        var ring = NewRing();
        var payload = Frame(64, 64);
        for (var slot = 0; slot < FrameRingCore.SlotCount; slot++)
        {
            FrameRingCore.Publish(ring, slot, payload, 64, 64, 192, FrameRingFormat.Rgb24,
                (uint)slot, 0, slot + 1, slot);
        }

        // Slot 0 is the oldest, but a reader is copying out of it.
        Header(ring, 0)[ReadersIndex] = 1;

        Assert.Equal(1, FrameRingCore.SelectSlot(ring));
    }

    [Fact]
    public void SelectSlot_NeverBlocksWhenEverySlotIsBeingRead()
    {
        var ring = NewRing();
        var payload = Frame(64, 64);
        for (var slot = 0; slot < FrameRingCore.SlotCount; slot++)
        {
            FrameRingCore.Publish(ring, slot, payload, 64, 64, 192, FrameRingFormat.Rgb24,
                (uint)slot, 0, slot + 1, slot);
        }

        for (var slot = 0; slot < FrameRingCore.SlotCount; slot++)
        {
            // What TryRead's guard does for the length of its copy.
            Header(ring, slot)[ReadersIndex] = 1;
        }

        // The oldest slot still wins: the writer is never made to wait.
        Assert.Equal(0, FrameRingCore.SelectSlot(ring));
    }

    [Fact]
    public async Task ReaderAndWriterInterleaveWithoutEverMixingTwoFrames()
    {
        // A real interleaving: one thread publishes frame A and frame B while
        // this thread reads the newest slot. Every successful read is one whole
        // frame - never a blend - and a read that loses the race is reported as
        // torn instead of being shown.
        var width = 256;
        var height = 256;
        var capacity = width * height * 3;
        var ring = NewRing(capacity);
        var a = Enumerable.Repeat((byte)0x11, capacity).ToArray();
        var b = Enumerable.Repeat((byte)0xE7, capacity).ToArray();

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            long generation = 0;
            var useA = true;
            while (!stop.IsCancellationRequested)
            {
                var payload = useA ? a : b;
                useA = !useA;
                var slot = FrameRingCore.SelectSlot(ring);
                FrameRingCore.Publish(ring, slot, payload, width, height, width * 3,
                    FrameRingFormat.Rgb24, (uint)generation, 0, (int)(++generation), 1);
                // A writer that never yields would own the ring's few
                // microseconds; a real one publishes 30-60 times a second.
                Thread.Sleep(0);
            }
        });

        var destination = new byte[capacity];
        var ok = 0;
        var torn = 0;
        var other = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (ok < 50 && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                // A reader follows the newest published frame - what the event's
                // slot names - not the oldest free slot the writer is about to
                // take.
                var slot = NewestSlot(ring, capacity);
                if (slot < 0)
                {
                    // The writer has not finished its first frame yet.
                    Thread.Yield();
                    continue;
                }
                var status = FrameRingCore.TryRead(ring, slot, destination, width * 3, out var info);
                switch (status)
                {
                    case FrameRingReadStatus.Ok:
                        ok++;
                        var first = destination[0];
                        Assert.True(first is 0x11 or 0xE7, "a read frame must be one published frame");
                        Assert.Equal(first, destination[^1]);
                        Assert.Equal(width, info.Width);
                        Assert.Equal(width * 3, info.Stride);
                        break;
                    case FrameRingReadStatus.Torn:
                        torn++;
                        break;
                    default:
                        other++;
                        break;
                }
            }
        }
        finally
        {
            stop.Cancel();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(50, ok);
        // A slot that has been published into is never empty or malformed while
        // the writer keeps publishing: the only two answers are "a frame" and
        // "the writer got there first".
        Assert.Equal(0, other);
    }

    private static int NewestSlot(byte[] ring, int capacity)
    {
        // The newest slot that is not being written: the slot an event can name.
        var best = -1;
        var highest = int.MinValue;
        for (var slot = 0; slot < FrameRingCore.SlotCount; slot++)
        {
            var header = Header(ring, slot, capacity);
            var sequence = header[SeqIndex];
            if (sequence % 2 != 0 || header[ReadyIndex] == 0)
            {
                continue;
            }

            var generation = header[GenerationIndex];
            if (generation > highest)
            {
                highest = generation;
                best = slot;
            }
        }

        return best;
    }

    [Fact]
    public void HasFrame_IsFalseUntilSomethingIsPublished()
    {
        var ring = NewRing();
        Assert.False(FrameRingCore.HasFrame(ring));

        FrameRingCore.Publish(ring, 0, Frame(64, 64), 64, 64, 192, FrameRingFormat.Rgb24, 1, 0, 1, 1);

        Assert.True(FrameRingCore.HasFrame(ring));
    }

    /// <summary>
    /// The same publish and read through a real named mapping, which is the one
    /// thing the pure tests cannot cover: `CreateNew`, `OpenExisting` and the
    /// pointer both processes use.
    /// </summary>
    [WindowsFact]
    public void WriterAndReaderMeetThroughTheNamedMapping()
    {
        var instance = "ev11-ringtest-" + Guid.NewGuid().ToString("N")[..8];
        var payload = Frame(64, 64, 3);
        using var writer = FrameRingWriter.Create(instance, 4242, payload.Length, out var error);
        Assert.True(writer is not null, error);

        using var reader = FrameRingReader.TryOpen(writer!.Name, out var openError);
        Assert.True(reader is not null, openError);
        Assert.Equal(FrameRingCore.SlotCount, reader!.SlotCount);
        Assert.Equal(FrameRingCore.AlignCapacity(payload.Length), reader.Capacity);

        var slot = writer.Write(payload, 64, 64, 64 * 3, FrameRingFormat.Rgb24, 7, 1);
        Assert.InRange(slot, 0, FrameRingCore.SlotCount - 1);

        var destination = new byte[payload.Length];
        var status = reader.TryRead(slot, destination, 64 * 3, out var info);

        Assert.Equal(FrameRingReadStatus.Ok, status);
        Assert.Equal(payload, destination);
        Assert.Equal(FrameRingFormat.Rgb24, info.Format);
        Assert.Equal(7u, info.EmulatorSequence);
    }

    [WindowsFact]
    public void ReaderReportsAMissingMap()
    {
        var reader = FrameRingReader.TryOpen(
            FrameRingWriter.MapName("ev11-missing-" + Guid.NewGuid().ToString("N")[..8], 1), out var error);

        Assert.Null(reader);
        Assert.NotNull(error);
    }
}

/// <summary>
/// EV-11: the event shape and the file decision, which are what an existing
/// client sees and what the supervisor writes.
/// </summary>
public sealed class FrameEventShapeTests
{
    private static FrameEvent Event(int slot, string? mapName, string path) => new(
        "ev11-a", 42, 540, 1200, 1620, "rgb24", 1080, 2400, 1_944_000, 1234,
        new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 17, 0, 0, 0, 1, TimeSpan.Zero),
        path, 0, slot, mapName);

    [Fact]
    public void SharedFrame_CarriesSlotAndMapAndNoPath()
    {
        var payload = FrameEventPayload.Build(Event(1, @"Local\ew-frames-ev11-a-1234", ""));

        Assert.Equal(1, payload["slot"]!.GetValue<int>());
        Assert.Equal(@"Local\ew-frames-ev11-a-1234", payload["mapName"]!.GetValue<string>());
        Assert.Null(payload["path"]);
    }

    [Fact]
    public void FileFrame_StillCarriesEveryFieldAnOldClientReads()
    {
        // Round-tripped through text, which is how a client on the pipe sees it.
        var payload = JsonNode.Parse(
            FrameEventPayload.Build(Event(-1, null, @"D:\ws\frames\ev11-a-42.bgr")).ToJsonString())!.AsObject();

        // The EV-10 field set, unchanged.
        Assert.Equal(42, payload["seq"]!.GetValue<int>());
        Assert.Equal(540, payload["width"]!.GetValue<int>());
        Assert.Equal(1200, payload["height"]!.GetValue<int>());
        Assert.Equal(1620, payload["stride"]!.GetValue<int>());
        Assert.Equal("rgb24", payload["format"]!.GetValue<string>());
        Assert.Equal(1080, payload["guestWidth"]!.GetValue<int>());
        Assert.Equal(2400, payload["guestHeight"]!.GetValue<int>());
        Assert.Equal(1_944_000, payload["bytes"]!.GetValue<long>());
        Assert.Equal(1234, payload["emulatorTimestampUs"]!.GetValue<int>());
        Assert.Equal(0, payload["dropped"]!.GetValue<int>());
        Assert.NotNull(payload["receivedUtc"]);
        Assert.NotNull(payload["writtenUtc"]);
        Assert.Equal(@"D:\ws\frames\ev11-a-42.bgr", payload["path"]!.GetValue<string>());
    }

    [Fact]
    public void AnOldClientParsesTheNewEventWithTheFieldsItKnows()
    {
        // The exact read an EV-10 client does: peek the type, take the fields
        // by name, ignore what it does not know. A missing path is the only
        // difference it can see, and it only sees that when it did not ask for
        // a file.
        var shared = FrameEventPayload.Build(Event(2, "map", ""));
        var path = shared["path"]?.GetValue<string>();
        Assert.Null(path);
        Assert.Equal(540, shared["width"]!.GetValue<int>());
        Assert.Equal(1200, shared["height"]!.GetValue<int>());
        Assert.Equal("rgb24", shared["format"]!.GetValue<string>());

        // ...and the same client with a file subscriber next to it.
        var withFile = FrameEventPayload.Build(Event(2, "map", "/frames/a-1.bgr"));
        Assert.Equal("/frames/a-1.bgr", withFile["path"]!.GetValue<string>());
        Assert.Equal(2, withFile["slot"]!.GetValue<int>());
        Assert.Equal("map", withFile["mapName"]!.GetValue<string>());
    }

    [Fact]
    public void EventIsValidJsonAndKeepsTheSharedFieldsAsAdditions()
    {
        var payload = FrameEventPayload.Build(Event(0, "map", ""));
        var text = payload.ToJsonString();
        var parsed = JsonNode.Parse(text)!.AsObject();

        Assert.Contains("\"slot\":0", text);
        Assert.Contains("\"mapName\":\"map\"", text);
        Assert.Equal(parsed.Count, payload.Count);
    }

    [Fact]
    public void FilePolicy_WritesOnlyForAFileSubscriberOrAFileOnlyFrame()
    {
        // A frame the ring took, nobody wants files: no file.
        Assert.False(FrameFilePolicy.NeedsFile(slot: 1, filesWanted: false));

        // A frame the ring took, a CLI/MCP subscriber wants files: write it.
        Assert.True(FrameFilePolicy.NeedsFile(slot: 1, filesWanted: true));

        // A frame the ring could not take is always written, with or without
        // a file subscriber, so a desktop can still show it.
        Assert.True(FrameFilePolicy.NeedsFile(slot: -1, filesWanted: false));
        Assert.True(FrameFilePolicy.NeedsFile(slot: -1, filesWanted: true));
    }

    [Fact]
    public void Delivery_DefaultsToFilesSoTheCliAndMcpKeepTheirPath()
    {
        Assert.Equal(FrameDelivery.File, FrameDelivery.Parse(null));
        Assert.Equal(FrameDelivery.File, FrameDelivery.Parse(""));
        Assert.Equal(FrameDelivery.File, FrameDelivery.Parse("file"));
        Assert.Equal(FrameDelivery.File, FrameDelivery.Parse("something-else"));
        Assert.Equal(FrameDelivery.Shared, FrameDelivery.Parse("shared"));
        Assert.Equal(FrameDelivery.Shared, FrameDelivery.Parse("SHARED"));

        Assert.True(FrameDelivery.WantsFiles(null));
        Assert.False(FrameDelivery.WantsFiles("shared"));
    }

    [Fact]
    public void MapName_IsPerInstanceAndPerSupervisorProcess()
    {
        var name = FrameRingWriter.MapName("ev11-a", 1234);

        Assert.Equal(@"Local\ew-frames-ev11-a-1234", name);
        Assert.NotEqual(name, FrameRingWriter.MapName("ev11-a", 1235));
        Assert.NotEqual(name, FrameRingWriter.MapName("ev11-b", 1234));
    }
}

/// <summary>
/// EV-11: the event hub's fan-out. One slow subscriber used to hold every
/// later one for the duration of its own send.
/// </summary>
public sealed class SupervisorEventHubTests
{
    [Fact]
    public async Task Publish_DoesNotMakeOneSubscriberWaitForAnother()
    {
        var hub = new SupervisorEventHub();
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        hub.Add("ev11-a", "frame", async _ =>
        {
            slowCalled.TrySetResult();
            await slowGate.Task.ConfigureAwait(false);
        });
        hub.Add("ev11-a", "frame", _ =>
        {
            fastCalled.TrySetResult();
            return Task.CompletedTask;
        });

        var publish = hub.PublishAsync("ev11-a", "frame", EventNames.Frame, new JsonObject());
        await slowCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Both sends are already in flight: the fast one completes while the
        // slow one is still parked.
        Assert.True(await Task.WhenAny(fastCalled.Task, Task.Delay(TimeSpan.FromSeconds(5))) == fastCalled.Task);
        Assert.False(publish.IsCompleted);

        slowGate.SetResult();
        await publish.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Publish_ReachesOnlyTheMatchingStream()
    {
        var hub = new SupervisorEventHub();
        var frames = 0;
        var logcat = 0;
        hub.Add("ev11-a", "frame", _ => { frames++; return Task.CompletedTask; });
        hub.Add("ev11-a", "logcat", _ => { logcat++; return Task.CompletedTask; });

        await hub.PublishAsync("ev11-a", "frame", EventNames.Frame, new JsonObject());

        Assert.Equal(1, frames);
        Assert.Equal(0, logcat);
    }

    [Fact]
    public async Task Publish_DropsOnlyTheSubscriberWhoseSendFailed()
    {
        var hub = new SupervisorEventHub();
        var delivered = 0;
        hub.Add("ev11-a", "frame", _ => throw new InvalidOperationException("broken pipe"));
        hub.Add("ev11-a", "frame", _ => { delivered++; return Task.CompletedTask; });

        await hub.PublishAsync("ev11-a", "frame", EventNames.Frame, new JsonObject());

        Assert.Equal(1, delivered);
        Assert.Equal(1, hub.Count("ev11-a", "frame"));
    }

    [Fact]
    public void Count_AnswersHowManySubscribersWantFiles()
    {
        var hub = new SupervisorEventHub();
        hub.Add("ev11-a", "frame", _ => Task.CompletedTask, wantsFiles: false);
        hub.Add("ev11-a", "frame", _ => Task.CompletedTask, wantsFiles: true);

        Assert.Equal(2, hub.Count("ev11-a", "frame"));
        Assert.Equal(1, hub.Count("ev11-a", "frame", wantsFiles: true));
        Assert.Equal(1, hub.Count("ev11-a", "frame", wantsFiles: false));
    }

    [Fact]
    public async Task Publish_WithNoSubscriberIsAFreeNoOp()
    {
        var hub = new SupervisorEventHub();

        await hub.PublishAsync("ev11-a", "frame", EventNames.Frame, new JsonObject());
    }
}

