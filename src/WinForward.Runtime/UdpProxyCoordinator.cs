using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime;

public interface IUdpResponseSink
{
    ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

public sealed class UdpProxyCoordinator : IAsyncDisposable
{
    private readonly IUdpProxyTransportFactory _transportFactory;
    private readonly IUdpResponseSink _responseSink;
    private readonly UdpAssociationTable _associations;
    private readonly Dictionary<FlowKey, Task<UdpProxySession>> _sessions = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _capacity;

    public UdpProxyCoordinator(IUdpProxyTransportFactory transportFactory, IUdpResponseSink responseSink, int capacity = 16_384)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(responseSink);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _transportFactory = transportFactory;
        _responseSink = responseSink;
        _associations = new UdpAssociationTable(capacity);
        _capacity = capacity;
    }

    public async ValueTask<bool> TrySendAsync(FlowKey flow, Socks5Server server, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (flow.Protocol != TransportProtocol.Udp) throw new ArgumentException("UDP coordinator accepts only UDP flow keys.", nameof(flow));
        Task<UdpProxySession> sessionTask;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(flow, out sessionTask!))
            {
                if (_sessions.Count >= _capacity) return false;
                sessionTask = CreateSessionAsync(flow, server, _shutdown.Token);
                _sessions.Add(flow, sessionTask);
            }
        }

        UdpProxySession session;
        try
        {
            session = await sessionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A caller cancelling while setup is still in progress must not tear
            // down a session that another datagram is already using. Only a
            // failed setup task is removed here.
            if (sessionTask.IsFaulted || sessionTask.IsCanceled) await RemoveFailedSessionAsync(flow, sessionTask).ConfigureAwait(false);
            throw;
        }

        try
        {
            await session.SendAsync(flow.Remote, payload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            // Cancellation from this individual caller is not evidence that the
            // shared UDP transport failed.
            throw;
        }
        catch
        {
            await RemoveFailedSessionAsync(flow, sessionTask).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task<UdpProxySession>[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var task in sessions)
        {
            try
            {
                var session = await task.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Cancellation is the expected shutdown path.
            }
        }
        _shutdown.Dispose();
    }

    private async Task<UdpProxySession> CreateSessionAsync(FlowKey flow, Socks5Server server, CancellationToken cancellationToken)
    {
        var addressFamily = flow.Local.AddressFamily == AddressFamilyKind.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        var transport = await _transportFactory.CreateAsync(server, addressFamily, cancellationToken).ConfigureAwait(false);
        var relayAlias = new RelayAlias(FlowKey.Create(
            Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port)),
            Endpoint.From(transport.RelayEndpoint.Address, checked((ushort)transport.RelayEndpoint.Port)),
            TransportProtocol.Udp,
            flow.Origin));
        if (!_associations.TryClaim(flow, relayAlias, DateTimeOffset.UtcNow, out _))
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw new IOException("UDP relay alias collision with another flow; blocking the flow.");
        }

        var session = new UdpProxySession(flow, transport, _responseSink, cancellationToken);
        session.Start();
        return session;
    }

    /// <summary>
    /// Removes sessions whose last send or receive is older than <paramref name="idleTimeout"/>
    /// and releases their associations, so short-lived DNS/QUIC-style flows do not accumulate to
    /// the bounded capacity. Idle expiry (design §7/§8) runs on a periodic sweep in the runtime.
    /// </summary>
    public async ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout)
    {
        Task<UdpProxySession>[] idle;
        lock (_gate)
        {
            idle = _sessions.Values.Where(task => task.IsCompletedSuccessfully && now - task.Result.LastActivityUtc >= idleTimeout).ToArray();
        }

        var removed = 0;
        foreach (var task in idle)
        {
            try
            {
                var session = await task.ConfigureAwait(false);
                lock (_gate)
                {
                    if (_sessions.TryGetValue(session.Flow, out var current) && ReferenceEquals(current, task)) _sessions.Remove(session.Flow);
                }
                await session.DisposeAsync().ConfigureAwait(false);
                removed++;
            }
            catch (Exception) when (task.IsFaulted || task.IsCanceled)
            {
                // A failed session has no transport to dispose.
            }
        }

        _associations.RemoveExpired(now, idleTimeout);
        return removed;
    }

    private async ValueTask RemoveFailedSessionAsync(FlowKey flow, Task<UdpProxySession> expected)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(flow, out var current) && ReferenceEquals(current, expected)) _sessions.Remove(flow);
        }
        _associations.TryRemoveOriginal(flow);
        try
        {
            var session = await expected.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) when (expected.IsFaulted || expected.IsCanceled)
        {
            // A failed session has no transport to dispose.
        }
    }
}

internal sealed class UdpProxySession : IAsyncDisposable
{
    private readonly FlowKey _flow;
    private readonly IUdpProxyTransport _transport;
    private readonly IUdpResponseSink _sink;
    private readonly CancellationToken _shutdown;
    private Task? _receiveLoop;
    private Exception? _receiveFailure;
    private long _lastActivityTicks;

    public UdpProxySession(FlowKey flow, IUdpProxyTransport transport, IUdpResponseSink sink, CancellationToken shutdown)
    {
        _flow = flow;
        _transport = transport;
        _sink = sink;
        _shutdown = shutdown;
        _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    public FlowKey Flow => _flow;
    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public void Start() => _receiveLoop = ReceiveLoopAsync();

    public ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var failure = Volatile.Read(ref _receiveFailure);
        if (failure is not null) throw new IOException("SOCKS5 UDP relay session is no longer usable.", failure);
        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
        return _transport.SendAsync(new IPEndPoint(destination.Address, destination.Port), payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Cancellation is the expected shutdown path.
            }
            catch (ObjectDisposedException)
            {
                // Disposal closes the receive socket and ends the loop.
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65_535];
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var response = await _transport.ReceiveAsync(buffer, _shutdown).ConfigureAwait(false);
                if (response.DestinationAddress is null) continue;
                Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
                var source = Endpoint.From(response.DestinationAddress, response.DestinationPort);
                await _sink.InjectAsync(_flow, source, response.Payload, _shutdown).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
            // Disposal closes the receive socket during shutdown.
        }
        catch (Exception exception)
        {
            _receiveFailure = exception;
        }
    }
}
