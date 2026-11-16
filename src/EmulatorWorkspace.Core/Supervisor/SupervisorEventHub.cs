using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Fan-out for subscription events. Each subscriber is owned by exactly one
/// client session; disconnecting that session removes all of its subscriptions.
///
/// EV-11: the subscribers are held in a copy-on-write snapshot and a publish
/// fans out concurrently, so one slow subscriber no longer makes the others
/// wait for it frame after frame. The snapshot is rebuilt only when a
/// subscription is added or removed, so the frame path reads one array and
/// allocates only the send tasks.
/// </summary>
public sealed class SupervisorEventHub
{
    private sealed record Subscriber(
        string Id,
        string Instance,
        string Kind,
        bool WantsFiles,
        Func<EventEnvelope, Task> Send);

    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new(StringComparer.Ordinal);
    private Subscriber[] _snapshot = Array.Empty<Subscriber>();

    public string Add(string instance, string kind, Func<EventEnvelope, Task> send, bool wantsFiles = false)
    {
        var id = Guid.NewGuid().ToString("N");
        _subscribers[id] = new Subscriber(id, instance, kind, wantsFiles, send);
        RebuildSnapshot();
        return id;
    }

    public bool Remove(string id)
    {
        if (!_subscribers.TryRemove(id, out _))
        {
            return false;
        }

        RebuildSnapshot();
        return true;
    }

    public int Count(string instance, string kind) => Count(instance, kind, null);

    /// <summary>
    /// EV-11: how many live subscribers of one stream want frame files. The
    /// frame pump asks this per frame, so a desktop that reads shared memory
    /// keeps frames/ quiet while a CLI or MCP client still gets its file.
    /// </summary>
    public int Count(string instance, string kind, bool? wantsFiles)
    {
        var count = 0;
        foreach (var subscriber in _snapshot)
        {
            if (subscriber.Instance != instance || subscriber.Kind != kind)
            {
                continue;
            }

            if (wantsFiles is { } wanted && subscriber.WantsFiles != wanted)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// The ids of the live subscribers of one stream. A pump that dies uses
    /// this to close exactly the subscriptions it owned, so a closed stream
    /// leaves nothing behind in the hub.
    /// </summary>
    public IReadOnlyList<string> Ids(string instance, string kind)
    {
        var ids = new List<string>();
        foreach (var subscriber in _snapshot)
        {
            if (subscriber.Instance == instance && subscriber.Kind == kind)
            {
                ids.Add(subscriber.Id);
            }
        }

        return ids;
    }

    public IReadOnlyList<string> RemoveInstance(string instance)
    {
        var removed = new List<string>();
        foreach (var subscriber in _snapshot)
        {
            if (subscriber.Instance != instance)
            {
                continue;
            }

            if (_subscribers.TryRemove(subscriber.Id, out _))
            {
                removed.Add(subscriber.Id);
            }
        }

        if (removed.Count > 0)
        {
            RebuildSnapshot();
        }

        return removed;
    }

    public async Task PublishAsync(string instance, string kind, string eventName, JsonNode data)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.Length == 0)
        {
            return;
        }

        List<Task>? sends = null;
        foreach (var subscriber in snapshot)
        {
            if (subscriber.Instance != instance || subscriber.Kind != kind)
            {
                continue;
            }

            (sends ??= new List<Task>(1)).Add(SendAsync(subscriber, eventName, data));
        }

        if (sends is null)
        {
            return;
        }

        // Every send is already in flight; one pipe's backlog cannot delay the
        // others. A failed send drops only its own subscriber.
        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task SendAsync(Subscriber subscriber, string eventName, JsonNode data)
    {
        try
        {
            await subscriber.Send(new EventEnvelope
            {
                Event = eventName,
                Subscription = subscriber.Id,
                Data = data,
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Remove(subscriber.Id);
        }
    }

    public async Task CloseAsync(string subscriptionId, string reason)
    {
        if (!_subscribers.TryRemove(subscriptionId, out var subscriber))
        {
            return;
        }

        RebuildSnapshot();
        try
        {
            await subscriber.Send(new EventEnvelope
            {
                Event = EventNames.SubscriptionClosed,
                Subscription = subscriptionId,
                Data = new JsonObject { ["reason"] = reason },
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void RebuildSnapshot() => Volatile.Write(ref _snapshot, _subscribers.Values.ToArray());
}

public sealed class SupervisorOperationException(string code, string message, bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}
