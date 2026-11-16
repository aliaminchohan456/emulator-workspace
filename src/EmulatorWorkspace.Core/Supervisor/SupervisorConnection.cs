using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// Thread-safe client for the local supervisor. One connection carries both
/// correlated responses and asynchronous subscription events.
/// </summary>
public sealed class SupervisorConnection : IAsyncDisposable
{
    private const int StateOpen = 0;
    private const int StateClosed = 1;

    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private int _state = StateOpen;
    private int _disposed;

    private SupervisorConnection(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public event Action<EventEnvelope>? EventReceived;

    /// <summary>
    /// UX-16 item C: raised once when the reader stops, which happens when the
    /// supervisor process ends or closes the pipe. Pending requests still fail
    /// on their own; this lets the desktop notice a dead pipe before it sends
    /// the next one. Runs on the reader's thread.
    /// </summary>
    public event Action? Closed;

    public Action<string>? Trace { get; set; }

    public string? DefaultAuth { get; set; }

    /// <summary>
    /// True until the reader stops or a write proves the pipe is gone. Callers
    /// can use this to tell "the supervisor ended" from "the request was bad".
    /// </summary>
    public bool IsOpen => Volatile.Read(ref _state) == StateOpen;

    /// <summary>
    /// Wraps a pipe that is already connected, for in-process hosts and tests.
    /// Production connects through <see cref="ConnectAsync"/>.
    /// </summary>
    public static SupervisorConnection FromConnectedPipe(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected)
        {
            throw new ArgumentException("The pipe must already be connected.", nameof(pipe));
        }

        return new SupervisorConnection(pipe);
    }

    public static async Task<SupervisorConnection> ConnectAsync(
        string root,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken = default)
    {
        var pipe = await SupervisorLocator.TryConnectAsync(root, TimeSpan.FromMilliseconds(300), cancellationToken)
            .ConfigureAwait(false);

        if (pipe is null)
        {
            await SupervisorLocator.EnsureRunningAsync(root, startupTimeout, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            pipe = await SupervisorLocator.TryConnectAsync(root, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }

        if (pipe is null)
        {
            throw new SupervisorUnavailableException(
                $"Supervisor for '{root}' is not reachable and could not be started.");
        }

        return new SupervisorConnection(pipe);
    }

    public async Task<ResponseEnvelope> RequestAsync(
        string op,
        JsonObject? args,
        TimeSpan? deadline = null,
        CancellationToken cancellationToken = default,
        string? auth = null,
        string? idempotencyKey = null)
    {
        // A closed connection can never answer; waiting is a ten-minute spinner.
        if (!IsOpen)
        {
            throw new SupervisorUnavailableException("The supervisor connection closed.");
        }

        var id = Guid.NewGuid().ToString("N");
        var request = new RequestEnvelope
        {
            Id = id,
            Op = op,
            Args = args,
            DeadlineMs = deadline is null ? null : (int)Math.Min(int.MaxValue, deadline.Value.TotalMilliseconds),
            Auth = auth ?? DefaultAuth,
            IdempotencyKey = idempotencyKey,
        };

        var completion = new TaskCompletionSource<ResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        // The reader closes the state before it fails the pending table, so a
        // request that registered after that loop would never be failed unless
        // it checks again here.
        if (!IsOpen)
        {
            _pending.TryRemove(id, out _);
            throw new SupervisorUnavailableException("The supervisor connection closed.");
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PipeFraming.WriteAsync(_pipe, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Interlocked.Exchange(ref _state, StateClosed);
                throw new SupervisorUnavailableException("The supervisor connection closed.", ex);
            }
            finally
            {
                try
                {
                    _writeLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // DisposeAsync won the race; the caller already has the write error.
                }
            }

            var effective = deadline ?? TimeSpan.FromMinutes(10);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(effective + TimeSpan.FromSeconds(5));
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await PipeFraming.ReadAsync(_pipe, SupervisorProtocol.MaxResponseBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                var type = JsonArgs.String(message, "type") ?? "response";
                if (type == "response")
                {
                    var response = JsonSerializer.Deserialize<ResponseEnvelope>(
                        message.ToJsonString(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (response is not null && _pending.TryRemove(response.Id, out var completion))
                    {
                        completion.TrySetResult(response);
                    }

                    continue;
                }

                if (type == "event")
                {
                    var envelope = JsonSerializer.Deserialize<EventEnvelope>(
                        message.ToJsonString(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (envelope is not null)
                    {
                        if (envelope.Event is not (EventNames.Frame or EventNames.Logcat))
                        {
                            Trace?.Invoke($"event {envelope.Event} sub={envelope.Subscription}");
                        }
                        EventReceived?.Invoke(envelope);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace?.Invoke("reader error: " + ex.Message);
            // Connection loss is reported through pending requests.
        }
        finally
        {
            // State first: a request that registers from now on must fail at
            // once instead of waiting for a response this loop can't deliver.
            Interlocked.Exchange(ref _state, StateClosed);

            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new SupervisorUnavailableException("The supervisor connection closed."));
            }

            Closed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _state, StateClosed);
        _cts.Cancel();
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _pipe.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
