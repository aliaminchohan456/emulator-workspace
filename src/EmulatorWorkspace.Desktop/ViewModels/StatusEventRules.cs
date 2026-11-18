namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-13: the versions a desktop keeps for the additive change streams.
///
/// The supervisor's version is monotonic per instance and one counter for the
/// pairing queue. An event is applied only when it is newer than the version a
/// read already put on screen, so a delayed event cannot undo a fresher poll,
/// and a repeated event cannot repaint the same state twice. Seeding from a
/// read only ever moves the version forward for exactly that reason.
/// </summary>
public sealed class StatusEventVersions
{
    private readonly Dictionary<string, long> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private long _pairing;

    /// <summary>
    /// A new supervisor starts its counters at zero, so a reconnect must start
    /// the client's comparison from zero too. Without this, a restarted
    /// supervisor's first events would look older than the previous one's.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _instances.Clear();
            _pairing = 0;
        }
    }

    public long InstanceVersion(string id)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(id, out var version) ? version : 0;
        }
    }

    /// <summary>True when the event is newer and was recorded; false means stale or repeated.</summary>
    public bool AcceptInstance(string id, long version)
    {
        lock (_gate)
        {
            if (version <= InstanceVersionUnlocked(id))
            {
                return false;
            }

            _instances[id] = version;
            return true;
        }
    }

    /// <summary>Records a read's version without ever moving backwards.</summary>
    public void SeedInstance(string id, long version)
    {
        lock (_gate)
        {
            if (version > InstanceVersionUnlocked(id))
            {
                _instances[id] = version;
            }
        }
    }

    public long PairingVersion
    {
        get
        {
            lock (_gate)
            {
                return _pairing;
            }
        }
    }

    public bool AcceptPairing(long version)
    {
        lock (_gate)
        {
            if (version <= _pairing)
            {
                return false;
            }

            _pairing = version;
            return true;
        }
    }

    public void SeedPairing(long version)
    {
        lock (_gate)
        {
            if (version > _pairing)
            {
                _pairing = version;
            }
        }
    }

    private long InstanceVersionUnlocked(string id) =>
        _instances.TryGetValue(id, out var version) ? version : 0;
}

/// <summary>
/// EV-13: the two cadences each poll has. While the change subscription is
/// live the timer is only a safety net; when the subscription was refused or
/// the pipe dropped, the old interval comes back until it is re-established.
/// </summary>
public static class StatusEventCadence
{
    /// <summary>The Studio status read, while events carry changes.</summary>
    public static readonly TimeSpan StatusSafetyNet = TimeSpan.FromSeconds(30);

    /// <summary>The Studio status read before (and while) events are unavailable.</summary>
    public static readonly TimeSpan StatusFallback = TimeSpan.FromSeconds(2);

    /// <summary>The device library's state read, while events carry changes.</summary>
    public static readonly TimeSpan LibrarySafetyNet = TimeSpan.FromSeconds(30);

    /// <summary>The device library's state read before (and while) events are unavailable.</summary>
    public static readonly TimeSpan LibraryFallback = TimeSpan.FromSeconds(2);

    /// <summary>The Agents rail badge's light read, while events carry changes.</summary>
    public static readonly TimeSpan BadgeSafetyNet = TimeSpan.FromSeconds(60);

    /// <summary>The Agents rail badge's light read before (and while) events are unavailable.</summary>
    public static readonly TimeSpan BadgeFallback = TimeSpan.FromSeconds(10);

    public static TimeSpan Poll(bool eventsLive, TimeSpan safetyNet, TimeSpan fallback) =>
        eventsLive ? safetyNet : fallback;
}
