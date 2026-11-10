using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;

namespace EmulatorWorkspace.Supervisor;

/// <summary>
/// One named-pipe listener per workspace root. The pipe is restricted to the
/// current Windows user by the OS, and every accepted client is additionally
/// checked for same user and same logon session before a request is read.
/// </summary>
public sealed class SupervisorPipeServer
{
    private readonly string _pipeName;
    private readonly OperationDispatcher _dispatcher;
    private readonly InstanceManager _manager;
    private readonly Action<string> _log;
    private readonly List<Task> _sessions = new();
    private readonly object _sessionGate = new();

    public SupervisorPipeServer(
        string pipeName,
        OperationDispatcher dispatcher,
        InstanceManager manager,
        Action<string> log)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _manager = manager;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log($"Listening on pipe '{_pipeName}'.");
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 64 * 1024,
                    outBufferSize: 256 * 1024);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                PeerInfo peer;
                try
                {
                    peer = PipePeer.Verify(server.SafePipeHandle);
                }
                catch (Exception ex)
                {
                    _log($"Rejected pipe client: {ex.Message}");
                    server.Dispose();
                    continue;
                }

                _log($"Accepted client pid={peer.ProcessId} process={peer.ProcessName} session={peer.SessionId}.");
                var session = new ClientSession(server, peer, _dispatcher, _manager, _log);
                lock (_sessionGate)
                {
                    _sessions.RemoveAll(t => t.IsCompleted);
                    _sessions.Add(session.RunAsync(cancellationToken));
                }

                server = null;
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (IOException ex)
            {
                _log("Pipe listener error: " + ex.Message);
                server?.Dispose();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        Task[] pending;
        lock (_sessionGate)
        {
            pending = _sessions.ToArray();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        _log("Pipe server stopped.");
    }

    private sealed class ClientSession
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly PeerInfo _peer;
        private readonly OperationDispatcher _dispatcher;
        private readonly InstanceManager _manager;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
        private readonly ConcurrentBag<string> _subscriptions = new();

        public ClientSession(
            NamedPipeServerStream pipe,
            PeerInfo peer,
            OperationDispatcher dispatcher,
            InstanceManager manager,
            Action<string> log)
        {
            _pipe = pipe;
            _peer = peer;
            _dispatcher = dispatcher;
            _manager = manager;
            _log = log;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _pipe.IsConnected)
                {
                    JsonObject? message;
                    try
                    {
                        message = await PipeFraming.ReadAsync(
                            _pipe, SupervisorProtocol.MaxRequestBytes, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (message is null)
                    {
                        break;
                    }

                    var type = JsonArgs.String(message, "type") ?? "request";
                    if (type == "cancel")
                    {
                        var cancelId = JsonArgs.String(message, "id");
                        if (cancelId is not null && _inFlight.TryGetValue(cancelId, out var source))
                        {
                            source.Cancel();
                        }

                        continue;
                    }

                    var request = JsonSerializer.Deserialize<RequestEnvelope>(
                        message.ToJsonString(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (request is null || string.IsNullOrWhiteSpace(request.Id))
                    {
                        await WriteAsync(new ResponseEnvelope
                        {
                            Id = "",
                            Ok = false,
                            Code = ErrorCodes.InvalidRequest,
                            Message = "Request envelope was invalid.",
                        }, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _ = HandleAsync(request, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log($"Session ended: {ex.Message}");
            }
            finally
            {
                foreach (var subscription in _subscriptions)
                {
                    try
                    {
                        await _manager.UnsubscribeAsync(subscription, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }

                foreach (var source in _inFlight.Values)
                {
                    source.Cancel();
                    source.Dispose();
                }

                _pipe.Dispose();
                _log($"Client pid={_peer.ProcessId} disconnected.");
            }
        }

        private async Task HandleAsync(RequestEnvelope request, CancellationToken sessionToken)
        {
            if (request.Protocol != SupervisorProtocol.Version)
            {
                await WriteAsync(new ResponseEnvelope
                {
                    Id = request.Id,
                    Ok = false,
                    Code = ErrorCodes.UnsupportedProtocol,
                    Message = $"Protocol {request.Protocol} is not supported; this supervisor speaks {SupervisorProtocol.Version}.",
                }, sessionToken).ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            if (request.DeadlineMs is > 0)
            {
                linked.CancelAfter(TimeSpan.FromMilliseconds(request.DeadlineMs.Value));
            }

            _inFlight[request.Id] = linked;
            try
            {
                var response = await _dispatcher.ExecuteAsync(request, _peer, new Sink(this), linked.Token)
                    .ConfigureAwait(false);
                await WriteAsync(response, sessionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await WriteAsync(new ResponseEnvelope
                    {
                        Id = request.Id,
                        Ok = false,
                        Code = ErrorCodes.Cancelled,
                        Message = "Operation cancelled or deadline exceeded.",
                    }, sessionToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteAsync(new ResponseEnvelope
                    {
                        Id = request.Id,
                        Ok = false,
                        Code = ErrorCodes.Internal,
                        Message = ex.Message,
                    }, sessionToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                _inFlight.TryRemove(request.Id, out _);
            }
        }

        public async Task SendEventAsync(EventEnvelope envelope)
        {
            await WriteAsync(envelope, CancellationToken.None).ConfigureAwait(false);
        }

        public void TrackSubscription(string id) => _subscriptions.Add(id);

        private async Task WriteAsync<T>(T message, CancellationToken cancellationToken) where T : class
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PipeFraming.WriteAsync(_pipe, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private sealed class Sink(ClientSession session) : IEventSink
        {
            public Task SendEventAsync(EventEnvelope envelope) => session.SendEventAsync(envelope);

            public void TrackSubscription(string id) => session.TrackSubscription(id);
        }
    }
}
