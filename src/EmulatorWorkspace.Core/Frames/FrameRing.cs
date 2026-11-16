using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace EmulatorWorkspace.Core.Frames;

/// <summary>EV-11: the pixel layout one ring slot holds.</summary>
public enum FrameRingFormat
{
    None = 0,
    Rgb24 = 1,
    Bgr24 = 2,
    Bgra32 = 3,
    Png = 4,
}

/// <summary>EV-11: what one slot's header says about the frame it holds.</summary>
public readonly record struct FrameRingFrameInfo(
    int Width,
    int Height,
    int Stride,
    FrameRingFormat Format,
    int Bytes,
    uint EmulatorSequence,
    int Dropped,
    long WrittenUtcTicks);

/// <summary>EV-11: what the ring's own header says.</summary>
public readonly record struct FrameRingDescriptor(
    int Version,
    int SlotCount,
    int Capacity,
    int SlotBytes,
    int OwnerPid,
    long CreatedUtcTicks);

/// <summary>The result of one reader attempt.</summary>
public enum FrameRingReadStatus
{
    /// <summary>The frame was copied and its sequence did not move.</summary>
    Ok,

    /// <summary>The writer was in the middle of the slot (odd or moved sequence).</summary>
    Torn,

    /// <summary>The slot has never been written.</summary>
    Empty,

    /// <summary>The slot's header is not a frame this reader can copy.</summary>
    Invalid,
}

/// <summary>
/// EV-11: the pure layout and the two algorithms of the shared-memory frame
/// ring, over a plain byte span. The writer and the reader are separate
/// processes, so the only shared state is this memory: every field is a
/// fixed-width slot in a 64-byte header, and every publish is a seqlock
/// (odd sequence while writing, even when the frame is complete).
///
/// The writer never blocks on a reader: it takes the oldest slot nobody is
/// reading (a generation counter per slot breaks the tie), and if all three
/// are being read it overwrites the oldest anyway. A reader that loses that
/// race sees the sequence move under it and reports a torn read.
/// </summary>
public static class FrameRingCore
{
    /// <summary>"EWFR" little endian.</summary>
    public const uint Magic = 0x52465745;

    public const int Version = 1;
    public const int SlotCount = 3;

    /// <summary>Bytes of the ring header, before the first slot.</summary>
    public const int RingHeaderBytes = 64;

    /// <summary>Bytes of one slot's header, before its payload.</summary>
    public const int SlotHeaderBytes = 64;

    /// <summary>Header field indices, in 32-bit words from the start of a slot.</summary>
    private const int SeqIndex = 0;
    private const int WidthIndex = 1;
    private const int HeightIndex = 2;
    private const int StrideIndex = 3;
    private const int FormatIndex = 4;
    private const int ReadyIndex = 5;
    private const int ReadersIndex = 6;
    private const int GenerationIndex = 7;
    private const int BytesIndex = 8;      // two words: int64
    private const int EmulatorSeqIndex = 10;
    private const int DroppedIndex = 11;
    private const int WrittenIndex = 12;   // two words: int64

    /// <summary>Header field indices, in 32-bit words from the start of the ring.</summary>
    private const int RingMagicIndex = 0;
    private const int RingVersionIndex = 1;
    private const int RingSlotCountIndex = 2;
    private const int RingSlotBytesIndex = 3;
    private const int RingCapacityIndex = 4;
    private const int RingPidIndex = 5;
    private const int RingCreatedIndex = 6; // two words: int64

    /// <summary>Round a payload capacity up so every slot header stays 64-byte aligned.</summary>
    public static int AlignCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        return (capacity + 63) & ~63;
    }

    /// <summary>Bytes one mapped ring needs for that payload capacity.</summary>
    public static int RequiredBytes(int capacity)
    {
        var aligned = AlignCapacity(capacity);
        return RingHeaderBytes + SlotCount * (SlotHeaderBytes + aligned);
    }

    public static int SlotOffset(int slot, int capacity)
    {
        if (slot is < 0 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "The ring has three slots.");
        }

        return RingHeaderBytes + slot * (SlotHeaderBytes + AlignCapacity(capacity));
    }

    /// <summary>Writes the ring header. The caller has already zeroed the mapping.</summary>
    public static void Initialize(Span<byte> ring, int capacity, int ownerPid, long createdUtcTicks)
    {
        var aligned = AlignCapacity(capacity);
        if (ring.Length < RequiredBytes(aligned))
        {
            throw new ArgumentException("The mapping is smaller than the ring it must hold.", nameof(ring));
        }

        var words = MemoryMarshal.Cast<byte, int>(ring[..RingHeaderBytes]);
        words[RingMagicIndex] = unchecked((int)Magic);
        words[RingVersionIndex] = Version;
        words[RingSlotCountIndex] = SlotCount;
        words[RingSlotBytesIndex] = SlotHeaderBytes + aligned;
        words[RingCapacityIndex] = aligned;
        words[RingPidIndex] = ownerPid;
        BinaryPrimitives.WriteInt64LittleEndian(
            MemoryMarshal.AsBytes(words[RingCreatedIndex..]),
            createdUtcTicks);

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var header = SlotHeader(ring, slot, aligned);
            header[SeqIndex] = 0;
            header[ReadyIndex] = 0;
            header[ReadersIndex] = 0;
            header[GenerationIndex] = 0;
        }

        // Touch the payload pages once. The mapping is lazily committed, so the
        // first frames through a cold ring pay the faults (about 2,000 pages for
        // three 540x1200 slots); doing it here moves that cost to the
        // subscription instead of into the measured per-frame write.
        ring[RingHeaderBytes..].Clear();
    }

    /// <summary>Reads the ring header, or null when this is not one of our rings.</summary>
    public static FrameRingDescriptor? Describe(ReadOnlySpan<byte> ring)
    {
        if (ring.Length < RingHeaderBytes)
        {
            return null;
        }

        var words = MemoryMarshal.Cast<byte, int>(ring[..RingHeaderBytes]);
        if (unchecked((uint)words[RingMagicIndex]) != Magic)
        {
            return null;
        }

        var version = words[RingVersionIndex];
        var slotCount = words[RingSlotCountIndex];
        var slotBytes = words[RingSlotBytesIndex];
        var capacity = words[RingCapacityIndex];
        if (version != Version || slotCount != SlotCount || slotBytes <= SlotHeaderBytes || capacity <= 0)
        {
            return null;
        }

        if (ring.Length < RingHeaderBytes + slotCount * slotBytes)
        {
            return null;
        }

        var created = BinaryPrimitives.ReadInt64LittleEndian(MemoryMarshal.AsBytes(words[RingCreatedIndex..]));
        return new FrameRingDescriptor(version, slotCount, capacity, slotBytes, words[RingPidIndex], created);
    }

    /// <summary>
    /// The writer's slot choice: the oldest slot nobody is currently reading.
    /// A slot with readers is only taken when every slot has one, and even then
    /// the oldest wins - the writer is never made to wait.
    /// </summary>
    public static int SelectSlot(Span<byte> ring)
    {
        var capacity = CapacityOf(ring);
        var chosen = 0;
        var chosenGeneration = int.MaxValue;
        var chosenBusy = true;
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var header = SlotHeader(ring, slot, capacity);
            var generation = Volatile.Read(ref header[GenerationIndex]);
            var busy = Volatile.Read(ref header[ReadersIndex]) != 0;
            // A free slot always beats a slot a reader is inside; within the
            // same class the least recently written slot wins.
            var better = chosenBusy && !busy || chosenBusy == busy && generation < chosenGeneration;
            if (slot == 0 || better)
            {
                chosen = slot;
                chosenGeneration = generation;
                chosenBusy = busy;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Publishes one frame into <paramref name="slot"/>. The sequence is odd
    /// for the whole write and even again when the payload is complete, which
    /// is what a reader checks before and after its copy.
    /// </summary>
    public static void Publish(
        Span<byte> ring,
        int slot,
        ReadOnlySpan<byte> payload,
        int width,
        int height,
        int stride,
        FrameRingFormat format,
        uint emulatorSequence,
        int dropped,
        int generation,
        long writtenUtcTicks)
    {
        var capacity = CapacityOf(ring);
        if (payload.Length > capacity)
        {
            throw new ArgumentException("The frame does not fit the ring slot.", nameof(payload));
        }

        var offset = SlotOffset(slot, capacity);
        var header = MemoryMarshal.Cast<byte, int>(ring.Slice(offset, SlotHeaderBytes));
        var sequence = Volatile.Read(ref header[SeqIndex]);
        // Odd: a reader that starts now knows the slot is being written.
        Interlocked.Exchange(ref header[SeqIndex], unchecked(sequence + 1));

        header[WidthIndex] = width;
        header[HeightIndex] = height;
        header[StrideIndex] = stride;
        header[FormatIndex] = (int)format;
        BinaryPrimitives.WriteInt64LittleEndian(
            MemoryMarshal.AsBytes(header[BytesIndex..]), payload.Length);
        header[EmulatorSeqIndex] = unchecked((int)emulatorSequence);
        header[DroppedIndex] = dropped;
        BinaryPrimitives.WriteInt64LittleEndian(
            MemoryMarshal.AsBytes(header[WrittenIndex..]), writtenUtcTicks);
        header[GenerationIndex] = generation;

        payload.CopyTo(ring.Slice(offset + SlotHeaderBytes, payload.Length));

        Volatile.Write(ref header[ReadyIndex], 1);
        // Even: the frame is complete.
        Interlocked.Exchange(ref header[SeqIndex], unchecked(sequence + 2));
    }

    /// <summary>
    /// Copies one frame out of a slot into the reader's buffer, verifying the
    /// sequence before and after the copy. The reader marks the slot as being
    /// read for the duration, so a writer prefers another slot.
    /// </summary>
    public static FrameRingReadStatus TryRead(
        Span<byte> ring,
        int slot,
        Span<byte> destination,
        int destinationStride,
        out FrameRingFrameInfo info)
    {
        info = default;
        var capacity = CapacityOf(ring);
        var offset = SlotOffset(slot, capacity);
        var header = MemoryMarshal.Cast<byte, int>(ring.Slice(offset, SlotHeaderBytes));

        var before = Volatile.Read(ref header[SeqIndex]);
        if ((before & 1) != 0)
        {
            return FrameRingReadStatus.Torn;
        }

        if (Volatile.Read(ref header[ReadyIndex]) == 0)
        {
            return FrameRingReadStatus.Empty;
        }

        var width = header[WidthIndex];
        var height = header[HeightIndex];
        var stride = header[StrideIndex];
        var format = (FrameRingFormat)header[FormatIndex];
        var bytes = (int)BinaryPrimitives.ReadInt64LittleEndian(MemoryMarshal.AsBytes(header[BytesIndex..]));
        if (width <= 0 || height <= 0 || stride <= 0 || bytes <= 0 || bytes > capacity
            || (long)stride * height > bytes)
        {
            return FrameRingReadStatus.Invalid;
        }

        if (destination.Length < bytes || destinationStride < stride)
        {
            return FrameRingReadStatus.Invalid;
        }

        var payload = ring.Slice(offset + SlotHeaderBytes, bytes);
        Interlocked.Increment(ref header[ReadersIndex]);
        try
        {
            if (destinationStride == stride)
            {
                payload.CopyTo(destination);
            }
            else
            {
                for (var row = 0; row < height; row++)
                {
                    var rowBytes = Math.Min(stride, bytes - row * stride);
                    if (rowBytes <= 0)
                    {
                        break;
                    }

                    payload.Slice(row * stride, rowBytes)
                        .CopyTo(destination.Slice(row * destinationStride, rowBytes));
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref header[ReadersIndex]);
        }

        var after = Volatile.Read(ref header[SeqIndex]);
        if (!IsStable(unchecked((uint)before), unchecked((uint)after)))
        {
            return FrameRingReadStatus.Torn;
        }

        info = new FrameRingFrameInfo(
            width,
            height,
            stride,
            format,
            bytes,
            unchecked((uint)header[EmulatorSeqIndex]),
            header[DroppedIndex],
            BinaryPrimitives.ReadInt64LittleEndian(MemoryMarshal.AsBytes(header[WrittenIndex..])));
        return FrameRingReadStatus.Ok;
    }

    /// <summary>
    /// Whether a slot's sequence proved that one whole frame was copied: even
    /// before and after, and unchanged across the copy.
    /// </summary>
    public static bool IsStable(uint before, uint after) =>
        before == after && (after & 1) == 0;

    /// <summary>Whether any slot has been published into.</summary>
    public static bool HasFrame(Span<byte> ring)
    {
        var capacity = CapacityOf(ring);
        for (var slot = 0; slot < SlotCount; slot++)
        {
            if (Volatile.Read(ref SlotHeader(ring, slot, capacity)[ReadyIndex]) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int CapacityOf(ReadOnlySpan<byte> ring)
    {
        if (ring.Length < RingHeaderBytes)
        {
            throw new ArgumentException("The mapping is smaller than a ring header.", nameof(ring));
        }

        var words = MemoryMarshal.Cast<byte, int>(ring[..RingHeaderBytes]);
        var capacity = words[RingCapacityIndex];
        if (capacity <= 0 || RingHeaderBytes + SlotCount * (SlotHeaderBytes + capacity) > ring.Length)
        {
            throw new ArgumentException("The mapping is not an initialized frame ring.", nameof(ring));
        }

        return capacity;
    }

    private static Span<int> SlotHeader(Span<byte> ring, int slot, int capacity) =>
        MemoryMarshal.Cast<byte, int>(ring.Slice(SlotOffset(slot, capacity), SlotHeaderBytes));
}

/// <summary>EV-11: the owning side of one shared-memory frame ring.</summary>
public sealed unsafe class FrameRingWriter : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private int _generation;
    private bool _disposed;

    private FrameRingWriter(string name, MemoryMappedFile file, MemoryMappedViewAccessor view, byte* pointer, int capacity)
    {
        Name = name;
        _file = file;
        _view = view;
        _pointer = pointer;
        Capacity = capacity;
    }

    public string Name { get; }

    public int Capacity { get; }

    /// <summary>
    /// Creates the per-instance mapping. The name carries the supervisor's pid
    /// so a restarted supervisor cannot collide with the ring a reader still
    /// holds from the previous one.
    /// </summary>
    public static FrameRingWriter? Create(string instance, int supervisorPid, int capacity, out string? error)
    {
        error = null;
        var aligned = FrameRingCore.AlignCapacity(capacity);
        var name = MapName(instance, supervisorPid);
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = CreateOrOpen(name, FrameRingCore.RequiredBytes(aligned));
            // A ring left behind by an earlier pump of this same supervisor is
            // reused only when its slots hold our capacity; the slots are then
            // re-initialized so nobody is served a frame from the old stream.
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            var available = checked((int)view.Capacity);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            var ring = new Span<byte>(pointer + view.PointerOffset, available);
            var existing = FrameRingCore.Describe(ring);
            if (existing is { } descriptor && descriptor.Capacity != aligned)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                error = $"the existing ring holds {descriptor.Capacity} bytes per slot, not {aligned}";
                view.Dispose();
                view = null;
                file.Dispose();
                file = null;
                return null;
            }

            FrameRingCore.Initialize(ring, aligned, supervisorPid, DateTimeOffset.UtcNow.UtcTicks);
            return new FrameRingWriter(name, file, view, pointer, aligned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            if (view is not null)
            {
                view.Dispose();
            }

            file?.Dispose();
            return null;
        }
    }

    private static MemoryMappedFile CreateOrOpen(string name, int bytes)
    {
        try
        {
            return MemoryMappedFile.CreateNew(name, bytes);
        }
        catch (IOException)
        {
            // A reader still holds the previous pump's ring of this instance.
            // The section outlives its writer, so the name is taken; the caller
            // initializes the slots it finds.
            return OpenExisting(name);
        }
    }

    // The whole product targets Windows; Core carries no platform attribute, so
    // the analyzer cannot see it. The registry reads carry the same (baseline)
    // warning.
#pragma warning disable CA1416
    private static MemoryMappedFile OpenExisting(string name) =>
        MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
#pragma warning restore CA1416

    public static string MapName(string instance, int supervisorPid) =>
        string.Create(CultureInfo.InvariantCulture, $@"Local\ew-frames-{instance}-{supervisorPid}");

    /// <summary>Publishes one frame and returns the slot it landed in, or -1.</summary>
    public int Write(
        ReadOnlySpan<byte> payload,
        int width,
        int height,
        int stride,
        FrameRingFormat format,
        uint emulatorSequence,
        int dropped)
    {
        if (_disposed || payload.Length > Capacity)
        {
            return -1;
        }

        var ring = new Span<byte>(_pointer + _view.PointerOffset, FrameRingCore.RequiredBytes(Capacity));
        // The generation is the ring's own clock: it is only used to pick the
        // oldest slot, so wrapping is harmless.
        var generation = unchecked(_generation + 1);
        var slot = FrameRingCore.SelectSlot(ring);
        FrameRingCore.Publish(ring, slot, payload, width, height, stride, format,
            emulatorSequence, dropped, generation, DateTimeOffset.UtcNow.UtcTicks);
        _generation = generation;
        return slot;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>EV-11: the watching side of one shared-memory frame ring.</summary>
public sealed unsafe class FrameRingReader : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _slotBytes;
    private byte* _pointer;
    private bool _disposed;

    private FrameRingReader(
        MemoryMappedFile file,
        MemoryMappedViewAccessor view,
        byte* pointer,
        in FrameRingDescriptor descriptor)
    {
        _file = file;
        _view = view;
        _pointer = pointer;
        Descriptor = descriptor;
        _slotBytes = descriptor.SlotBytes;
    }

    public FrameRingDescriptor Descriptor { get; }

    public int SlotCount => Descriptor.SlotCount;

    public int Capacity => Descriptor.Capacity;

    public static FrameRingReader? TryOpen(string name, out string? error)
    {
        error = null;
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            // The whole product targets Windows; Core has no platform attribute
            // so the analyzer cannot see it. The pipe peer and the registry
            // reads carry the same (baseline) warning.
#pragma warning disable CA1416
            file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
#pragma warning restore CA1416
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            var available = checked((int)view.Capacity);
            var ring = new Span<byte>(pointer + view.PointerOffset, available);
            var descriptor = FrameRingCore.Describe(ring);
            if (descriptor is null)
            {
                error = "the mapping is not an EV-11 frame ring";
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
                file.Dispose();
                return null;
            }

            return new FrameRingReader(file, view, pointer, descriptor.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            view?.Dispose();
            file?.Dispose();
            return null;
        }
    }

    /// <summary>Copies one slot out of the ring, verifying its sequence.</summary>
    public FrameRingReadStatus TryRead(int slot, Span<byte> destination, int destinationStride, out FrameRingFrameInfo info)
    {
        info = default;
        if (_disposed || slot < 0 || slot >= Descriptor.SlotCount)
        {
            return FrameRingReadStatus.Invalid;
        }

        var ring = new Span<byte>(_pointer + _view.PointerOffset,
            FrameRingCore.RingHeaderBytes + Descriptor.SlotCount * _slotBytes);
        return FrameRingCore.TryRead(ring, slot, destination, destinationStride, out info);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _view.Dispose();
        _file.Dispose();
    }
}
