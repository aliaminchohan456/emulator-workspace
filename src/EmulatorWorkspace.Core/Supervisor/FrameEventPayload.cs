using System.Text.Json.Nodes;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// EV-11: the wire shape of one <c>frame</c> event, in one place so its
/// backwards compatibility is a test and not a convention.
///
/// The event is additive over EV-10: every field it had is still here, and two
/// new ones say where the pixels are - <c>slot</c> in the per-instance ring and
/// the ring's <c>mapName</c>. <c>path</c> is present exactly when a file was
/// written for a subscriber that asked for one, which is what the CLI and the
/// MCP client still do.
/// </summary>
public static class FrameEventPayload
{
    public static JsonObject Build(FrameEvent frame)
    {
        var payload = new JsonObject
        {
            ["seq"] = frame.Sequence,
            ["width"] = frame.Width,
            ["height"] = frame.Height,
            ["stride"] = frame.Stride,
            ["format"] = frame.Format,
            ["guestWidth"] = frame.GuestWidth,
            ["guestHeight"] = frame.GuestHeight,
            ["bytes"] = frame.Bytes,
            ["emulatorTimestampUs"] = frame.EmulatorTimestampUs,
            ["receivedUtc"] = frame.ReceivedUtc.ToString("O"),
            ["writtenUtc"] = frame.WrittenUtc.ToString("O"),
            ["dropped"] = frame.DroppedSincePrevious,
            ["slot"] = frame.Slot,
            ["mapName"] = frame.MapName,
        };

        if (!string.IsNullOrEmpty(frame.Path))
        {
            payload["path"] = frame.Path;
        }

        return payload;
    }
}

/// <summary>
/// EV-11: whether a published frame also has to be written to a file. A frame
/// the ring could not take always falls back to its file; a frame the ring did
/// take is written only while a subscriber asked for files.
/// </summary>
public static class FrameFilePolicy
{
    public static bool NeedsFile(int slot, bool filesWanted) => slot < 0 || filesWanted;
}
