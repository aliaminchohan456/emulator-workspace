namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>EV-05: where a stream subscription is.</summary>
public enum SubPhase
{
    /// <summary>No subscription, and none in flight.</summary>
    None,

    /// <summary>The subscribe request is out; no id has been granted yet.</summary>
    Subscribing,

    /// <summary>The supervisor granted an id, and that id is the live stream.</summary>
    Live,
}

/// <summary>
/// EV-05: everything that can happen to a subscription. The frame and the log
/// stream see the same events; the reply to a subscribe is
/// <see cref="Granted"/>, the supervisor's close is <see cref="Closed"/> and a
/// lost supervisor is <see cref="PipeLost"/>.
/// </summary>
public abstract record SubEvent
{
    private SubEvent()
    {
    }

    /// <summary>A subscribe is being asked for, on this guest.</summary>
    public sealed record Requested(string Device) : SubEvent;

    /// <summary>The supervisor answered with the id it opened, for that guest.</summary>
    public sealed record Granted(string Id, string Device) : SubEvent;

    /// <summary>The subscribe request failed; nothing was opened.</summary>
    public sealed record Refused : SubEvent;

    /// <summary>The supervisor closed this id.</summary>
    public sealed record Closed(string Id) : SubEvent;

    /// <summary>The pipe the subscription lived on is gone.</summary>
    public sealed record PipeLost : SubEvent;

    /// <summary>The guest this window follows changed (null: none selected).</summary>
    public sealed record DeviceChanged(string? Device) : SubEvent;
}

/// <summary>
/// EV-05: one stream subscription, with one transition function.
///
/// The window used to keep three independent fields for the frame stream (an
/// id, a "subscribe pending" flag and the log stream's id) that every caller
/// wrote directly. A late grant after a device change therefore replaced the
/// id of a stream nobody wanted, a close that arrived while a subscribe was in
/// flight cancelled the wrong thing, and two callers could each open a
/// subscription because neither saw the other's pending flag. The phase, the
/// guest the id belongs to and the id itself are one value now, and they move
/// only through <see cref="Next"/>, which also says which id - if any - has to
/// be handed back to the supervisor with an unsubscribe.
///
/// The rules are pure, so every race that used to need a live guest is a test.
/// </summary>
public sealed record StreamSubscription(SubPhase Phase, string? Device, string? Id)
{
    /// <summary>Nothing held.</summary>
    public static StreamSubscription None { get; } = new(SubPhase.None, null, null);

    /// <summary>The supervisor granted an id that this window is streaming.</summary>
    public bool IsLive => Phase == SubPhase.Live;

    /// <summary>
    /// The one transition function. It returns the next state and, when the
    /// event made an id obsolete, the id that must be unsubscribed.
    ///
    /// <list type="bullet">
    /// <item><see cref="SubEvent.Requested"/> starts a subscription only from
    /// <see cref="SubPhase.None"/>; a second request while one is in flight or
    /// live is ignored, so two callers cannot open two streams.</item>
    /// <item><see cref="SubEvent.Granted"/> is accepted only for the guest this
    /// state is waiting on. Any other grant - a late reply after the device
    /// changed, a reply after the pipe was lost, or a second subscribe that
    /// raced the first - is handed back as an unsubscribe id and changes
    /// nothing.</item>
    /// <item><see cref="SubEvent.Closed"/> ends the stream only when the id is
    /// the one held. A close that arrives while subscribing names an id this
    /// window does not hold, so it is ignored and the in-flight subscribe
    /// still lands.</item>
    /// <item><see cref="SubEvent.PipeLost"/> drops the id without an
    /// unsubscribe - the pipe it would travel on is gone - and leaves the
    /// state ready to attach again.</item>
    /// <item><see cref="SubEvent.DeviceChanged"/> unsubscribes a live id and
    /// returns to <see cref="SubPhase.None"/> for the new guest; changing to
    /// the guest the state already names is a no-op, so a refresh that
    /// re-selects the same guest does not churn the stream.</item>
    /// </list>
    /// </summary>
    public static (StreamSubscription Next, string? UnsubscribeId) Next(StreamSubscription s, SubEvent e)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(e);

        return e switch
        {
            SubEvent.Requested requested => Requested(s, requested.Device),
            SubEvent.Granted granted => Granted(s, granted.Id, granted.Device),
            SubEvent.Refused => Refused(s),
            SubEvent.Closed closed => Closed(s, closed.Id),
            SubEvent.PipeLost => PipeLost(s),
            SubEvent.DeviceChanged changed => DeviceChanged(s, changed.Device),
            _ => throw new ArgumentOutOfRangeException(nameof(e), e, "Unknown subscription event."),
        };
    }

    /// <summary>
    /// Whether this state already names that guest (a null argument means "no
    /// guest selected"). A device change to this same guest is a no-op.
    /// </summary>
    public static bool IsSameGuest(StreamSubscription s, string? device)
    {
        ArgumentNullException.ThrowIfNull(s);
        return string.Equals(s.Device, device, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether this stream should be opened now: nothing is held, the guest's
    /// own state allows the stream (<paramref name="lifecycle"/> is the frame
    /// stream's "can attach" or the log stream's "engine is up"), and the state
    /// is not still pointed at another guest.
    /// </summary>
    public static bool ShouldSubscribe(StreamSubscription s, bool lifecycle, string? device)
    {
        ArgumentNullException.ThrowIfNull(s);

        return s.Phase == SubPhase.None
            && lifecycle
            && !string.IsNullOrEmpty(device)
            && (s.Device is null || string.Equals(s.Device, device, StringComparison.Ordinal));
    }

    private static (StreamSubscription, string?) Requested(StreamSubscription s, string device)
    {
        if (s.Phase != SubPhase.None || string.IsNullOrEmpty(device))
        {
            return (s, null);
        }

        return (new StreamSubscription(SubPhase.Subscribing, device, null), null);
    }

    private static (StreamSubscription, string?) Granted(StreamSubscription s, string id, string device)
    {
        if (string.IsNullOrEmpty(id))
        {
            return (s, null);
        }

        if (s.Phase == SubPhase.Subscribing
            && !string.IsNullOrEmpty(device)
            && string.Equals(s.Device, device, StringComparison.Ordinal))
        {
            return (s with { Phase = SubPhase.Live, Id = id }, null);
        }

        // A grant nobody is waiting for. The id is a real stream in the
        // supervisor's hub, so it is handed back to be unsubscribed instead of
        // being written into a field: that is the double subscribe and the
        // late grant in one rule.
        return (s, id);
    }

    private static (StreamSubscription, string?) Refused(StreamSubscription s)
    {
        if (s.Phase != SubPhase.Subscribing)
        {
            return (s, null);
        }

        return (s with { Phase = SubPhase.None, Id = null }, null);
    }

    private static (StreamSubscription, string?) Closed(StreamSubscription s, string id)
    {
        if (s.Phase != SubPhase.Live
            || string.IsNullOrEmpty(id)
            || !string.Equals(s.Id, id, StringComparison.Ordinal))
        {
            // The id is not the live one: it may have been replaced already, and
            // clearing the live stream on a stale close would open a second one.
            return (s, null);
        }

        return (s with { Phase = SubPhase.None, Id = null }, null);
    }

    private static (StreamSubscription, string?) PipeLost(StreamSubscription s)
    {
        if (s.Phase == SubPhase.None && s.Id is null)
        {
            return (s, null);
        }

        // No unsubscribe: the subscription lives in the hub of the supervisor
        // that just went away, and there is no pipe left to ask.
        return (s with { Phase = SubPhase.None, Id = null }, null);
    }

    private static (StreamSubscription, string?) DeviceChanged(StreamSubscription s, string? device)
    {
        if (string.Equals(s.Device, device, StringComparison.Ordinal))
        {
            return (s, null);
        }

        var unsubscribe = s.Phase == SubPhase.Live ? s.Id : null;
        return (new StreamSubscription(SubPhase.None, device, null), unsubscribe);
    }
}
