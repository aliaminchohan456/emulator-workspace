using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Fan-out for subscription events. Each subscriber is owned by exactly one
/// client session; disconnecting that session removes all of its subscriptions.
/// </summary>
public sealed class SupervisorEventHub
{
    private sealed record Subscriber(
        string Id,
        string Instance,
        string Kind,
        Func<EventEnvelope, Task> Send);

    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new(StringComparer.Ordinal);

    public string Add(string instance, string kind, Func<EventEnvelope, Task> send)
    {
        var id = Guid.NewGuid().ToString("N");
        _subscribers[id] = new Subscriber(id, instance, kind, send);
        return id;
    }

    public bool Remove(string id) => _subscribers.TryRemove(id, out _);

    public int Count(string instance, string kind) =>
        _subscribers.Values.Count(s => s.Instance == instance && s.Kind == kind);

    public IReadOnlyList<string> RemoveInstance(string instance)
    {
        var removed = new List<string>();
        foreach (var subscriber in _subscribers.Values.Where(s => s.Instance == instance).ToList())
        {
            if (_subscribers.TryRemove(subscriber.Id, out _))
            {
                removed.Add(subscriber.Id);
            }
        }

        return removed;
    }

    public async Task PublishAsync(string instance, string kind, string eventName, JsonNode data)
    {
        foreach (var subscriber in _subscribers.Values.Where(s => s.Instance == instance && s.Kind == kind).ToList())
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
                _subscribers.TryRemove(subscriber.Id, out _);
            }
        }
    }

    public async Task CloseAsync(string subscriptionId, string reason)
    {
        if (!_subscribers.TryRemove(subscriptionId, out var subscriber))
        {
            return;
        }

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
}

public sealed class SupervisorOperationException(string code, string message, bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}
