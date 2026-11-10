using System.Collections.Concurrent;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Per-instance input barrier used to orchestrate a deterministic queued-input
/// race: the holder parks agent input for one guest until it is released, and
/// every waiter records when it started queueing so the harness can prove the
/// request was queued before a takeover happened.
/// </summary>
public sealed class InputGate
{
    private readonly ConcurrentDictionary<string, HoldState> _holds = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Waiter(DateTimeOffset QueuedUtc, string RequestId);

    private sealed class HoldState
    {
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset HeldUtc { get; init; } = DateTimeOffset.UtcNow;

        public ConcurrentDictionary<string, Waiter> Waiters { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Parks input for the instance. Returns the hold timestamp.</summary>
    public DateTimeOffset Hold(string instance)
    {
        var hold = _holds.GetOrAdd(instance, _ => new HoldState());
        return hold.HeldUtc;
    }

    /// <summary>Releases parked input. Returns the number of waiters released.</summary>
    public int Release(string instance)
    {
        if (!_holds.TryRemove(instance, out var hold))
        {
            return 0;
        }

        hold.Release.TrySetResult(true);
        return hold.Waiters.Count;
    }

    public bool IsHeld(string instance) => _holds.ContainsKey(instance);

    public int WaitingCount(string instance) =>
        _holds.TryGetValue(instance, out var hold) ? hold.Waiters.Count : 0;

    public DateTimeOffset? HeldSinceUtc(string instance) =>
        _holds.TryGetValue(instance, out var hold) ? hold.HeldUtc : null;

    public IReadOnlyList<Waiter> Waiters(string instance) =>
        _holds.TryGetValue(instance, out var hold)
            ? hold.Waiters.Values.OrderBy(w => w.QueuedUtc).ToList()
            : Array.Empty<Waiter>();

    /// <summary>
    /// Waits until the instance is not held. The waiter is registered first so
    /// the caller can prove the request was queued before it was released.
    /// </summary>
    public async Task<Waiter> WaitAsync(string instance, string requestId, CancellationToken cancellationToken)
    {
        if (!_holds.TryGetValue(instance, out var hold))
        {
            return new Waiter(DateTimeOffset.UtcNow, requestId);
        }

        var waiter = new Waiter(DateTimeOffset.UtcNow, requestId);
        hold.Waiters[requestId] = waiter;
        try
        {
            await hold.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return waiter;
        }
        finally
        {
            hold.Waiters.TryRemove(requestId, out _);
        }
    }
}
