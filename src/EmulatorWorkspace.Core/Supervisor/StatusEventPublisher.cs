using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Instances;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// EV-13: the additive change streams the desktop used to poll for.
///
/// <c>instance.changed</c> is published when a record's lifecycle word really
/// moves (the same word observed twice is one event, not two) and whenever a
/// Magisk lab phase is written. <c>pairing.changed</c> is published when the
/// pairing queue changes. Both carry a monotonic version: the instance version
/// is per instance, the pairing version is one counter for the queue, so a
/// client can ignore an event that arrives after a newer read or a newer event.
///
/// The payload of an instance event is the same evidence the status poll
/// carries for the fields a lifecycle reader needs - state, engine liveness,
/// boot completion, the frame stream and the lab classification - so an event
/// can replace a poll instead of merely prompting one.
/// </summary>
public sealed class StatusEventPublisher
{
    private readonly SupervisorEventHub _hub;
    private readonly InstanceManager _manager;
    private readonly MagiskLabService _lab;
    private readonly object _gate = new();
    private readonly Dictionary<string, InstanceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
    private long _pairingVersion;

    public StatusEventPublisher(SupervisorEventHub hub, InstanceManager manager, MagiskLabService lab)
    {
        _hub = hub;
        _manager = manager;
        _lab = lab;
    }

    /// <summary>The version of the last instance event published for this guest.</summary>
    public long InstanceVersion(string name)
    {
        lock (_gate)
        {
            return _versions.TryGetValue(name, out var version) ? version : 0;
        }
    }

    /// <summary>The version of the last pairing event.</summary>
    public long PairingVersion
    {
        get
        {
            lock (_gate)
            {
                return _pairingVersion;
            }
        }
    }

    /// <summary>
    /// Publishes one instance observation. Returns false - and publishes
    /// nothing - when the lifecycle word is the one this publisher already
    /// announced.
    /// </summary>
    public async Task<bool> PublishInstanceStateAsync(string name, InstanceState state)
    {
        JsonObject data;
        long version;
        lock (_gate)
        {
            if (_states.TryGetValue(name, out var last) && last == state)
            {
                return false;
            }

            _states[name] = state;
            version = _versions.TryGetValue(name, out var current) ? current + 1 : 1;
            _versions[name] = version;
            data = BuildInstance(name);
            // The caller is the transition owner: the record was persisted
            // before the callback, and naming the new word here keeps the
            // payload exact even if the store read races the write.
            data["state"] = state.ToString();
            data["version"] = version;
        }

        await _hub.PublishAsync("", "instance", EventNames.InstanceChanged, data).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A lab phase was written. The lifecycle word may not have moved at all
    /// (an install spends minutes in one state), so this bypasses the state
    /// gate and always publishes the new lab classification.
    /// </summary>
    public async Task<long> PublishLabPhaseAsync(string name)
    {
        JsonObject data;
        long version;
        lock (_gate)
        {
            version = _versions.TryGetValue(name, out var current) ? current + 1 : 1;
            _versions[name] = version;
            data = BuildInstance(name);
            data["version"] = version;
        }

        await _hub.PublishAsync("", "instance", EventNames.InstanceChanged, data).ConfigureAwait(false);
        return version;
    }

    /// <summary>
    /// Publishes the pairing queue's new state. <paramref name="change"/> is
    /// "created", "approved", "denied" or "expired".
    /// </summary>
    public async Task<long> PublishPairingAsync(int pendingCount, string id, string change)
    {
        long version;
        lock (_gate)
        {
            version = ++_pairingVersion;
        }

        await _hub.PublishAsync("", "pairing", EventNames.PairingChanged, new JsonObject
        {
            ["version"] = version,
            ["pendingCount"] = pendingCount,
            ["id"] = id,
            ["change"] = change,
        }).ConfigureAwait(false);
        return version;
    }

    private JsonObject BuildInstance(string name)
    {
        var data = _manager.StatusEventSnapshot(name);
        try
        {
            data["lab"] = _lab.Status(name);
        }
        catch (Exception)
        {
            // A journal that cannot be read is not a reason to lose the
            // lifecycle event: the lab row keeps its previous value and the
            // next event or poll names the real problem.
        }

        return data;
    }
}
