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
    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private int _disposed;

    private SupervisorConnection(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public event Action<EventEnvelope>? EventReceived;

    public Action<string>? Trace { get; set; }

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
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var request = new RequestEnvelope
        {
            Id = id,
            Op = op,
            Args = args,
            DeadlineMs = deadline is null ? null : (int)Math.Min(int.MaxValue, deadline.Value.TotalMilliseconds),
        };

        var completion = new TaskCompletionSource<ResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await PipeFraming.WriteAsync(_pipe, request, cancellationToken).ConfigureAwait(false);

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
                        Trace?.Invoke($"event {envelope.Event} sub={envelope.Subscription}");
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
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new SupervisorUnavailableException("The supervisor connection closed."));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _pipe.Dispose();
        _cts.Dispose();
    }
}
