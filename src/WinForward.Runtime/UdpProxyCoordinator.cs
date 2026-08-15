using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime;

public interface IUdpResponseSink
{
    ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken);
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
    private readonly TimeProvider _timeProvider;
    private readonly Func<ValueTask>? _beforeExpiryRecheck;
    private readonly IRuntimeLogger _logger;
    private Task? _disposeTask;
    private bool _disposed;

    public UdpProxyCoordinator(IUdpProxyTransportFactory transportFactory, IUdpResponseSink responseSink, int capacity = 16_384, IRuntimeLogger? logger = null)
        : this(transportFactory, responseSink, capacity, TimeProvider.System, null, logger)
    {
    }

    internal UdpProxyCoordinator(
        IUdpProxyTransportFactory transportFactory,
        IUdpResponseSink responseSink,
        int capacity,
        TimeProvider timeProvider,
        Func<ValueTask>? beforeExpiryRecheck,
        IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(responseSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _transportFactory = transportFactory;
        _responseSink = responseSink;
        _associations = new UdpAssociationTable(capacity);
        _capacity = capacity;
        _timeProvider = timeProvider;
        _beforeExpiryRecheck = beforeExpiryRecheck;
        _logger = logger ?? NullRuntimeLogger.Instance;
    }

    public async ValueTask<bool> TrySendAsync(FlowKey flow, Socks5Server server, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, long packetSequence = 0, long flowGeneration = 0, byte[]? clientMac = null)
    {
        if (flow.Protocol != TransportProtocol.Udp) throw new ArgumentException("UDP coordinator accepts only UDP flow keys.", nameof(flow));
        Task<UdpProxySession> sessionTask;
        var trackSetupFailure = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(flow, out sessionTask!))
            {
                if (_sessions.Count >= _capacity)
                {
                    LogTrace("udp.session.rejected", flow, new RuntimeLogField("reason", "capacity"));
                    return false;
                }
                var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                sessionTask = CreateSessionAsync(flow, server, flowGeneration, clientMac, _shutdown.Token, registered.Task);
                _sessions.Add(flow, sessionTask);
                registered.TrySetResult();
                trackSetupFailure = true;
            }
        }
        if (trackSetupFailure) TrackFailedSetup(flow, sessionTask);

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
            LogTrace("udp.packet.sent", flow, new RuntimeLogField("packet", packetSequence == 0 ? null : packetSequence), new RuntimeLogField("flow", session.FlowGeneration == 0 ? null : session.FlowGeneration), new RuntimeLogField("bytes", payload.Length), new RuntimeLogField("udpAssociation", session.Association.Generation));
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
        Task disposeTask;
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
            }
            disposeTask = _disposeTask;
        }

        if (completion is not null)
        {
            try
            {
                await DisposeCoreAsync().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        await disposeTask.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
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
                GC.KeepAlive(task);
            }
            catch (Exception) when (task.IsFaulted || task.IsCanceled)
            {
                GC.KeepAlive(task);
            }
        }
        _shutdown.Dispose();
    }

    private async Task<UdpProxySession> CreateSessionAsync(FlowKey flow, Socks5Server server, long flowGeneration, byte[]? clientMac, CancellationToken cancellationToken, Task registered)
    {
        IUdpProxyTransport? transport = null;
        UdpAssociation? association = null;
        var associationCreated = false;
        try
        {
            transport = await _transportFactory.CreateAsync(server, cancellationToken).ConfigureAwait(false);
            var relayAlias = new RelayAlias(FlowKey.Create(
                Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port)),
                Endpoint.From(transport.RelayEndpoint.Address, checked((ushort)transport.RelayEndpoint.Port)),
                TransportProtocol.Udp,
                flow.Origin));
            if (!_associations.TryClaim(flow, relayAlias, _timeProvider.GetUtcNow(), out association, out associationCreated) || association is null)
            {
                throw new IOException("UDP relay alias collision with another flow; blocking the flow.");
            }

            if (!associationCreated || association.RelayAlias != relayAlias)
            {
                throw new IOException("UDP flow association was already owned by another session; blocking the flow.");
            }

            var session = new UdpProxySession(flow, flowGeneration, association, transport, _responseSink, clientMac, cancellationToken, _timeProvider, OnSessionActivity, _logger);
            transport = null;
            session.Start(RemoveReceiveFailedSessionAsync, registered);
            LogDebug("udp.session.created", flow, flowGeneration, association, server.Name);
            return session;
        }
        catch
        {
            if (associationCreated && association is not null) _associations.TryRemove(association);
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

        if (idle.Length > 0 && _beforeExpiryRecheck is not null) await _beforeExpiryRecheck().ConfigureAwait(false);

        var removed = 0;
        foreach (var task in idle)
        {
            try
            {
                var session = await task.ConfigureAwait(false);
                if (!session.TryBeginExpiry(now, idleTimeout)) continue;
                var ownsSession = false;
                lock (_gate)
                {
                    if (_sessions.TryGetValue(session.Flow, out var current) && ReferenceEquals(current, task))
                    {
                        _associations.TryRemove(session.Association);
                        _sessions.Remove(session.Flow);
                        ownsSession = true;
                    }
                }
                if (!ownsSession)
                {
                    session.CancelExpiry();
                    continue;
                }
                await session.DisposeAsync().ConfigureAwait(false);
                LogDebug("udp.session.expired", session.Flow, session.FlowGeneration, session.Association, null);
                removed++;
            }
            catch (Exception) when (task.IsFaulted || task.IsCanceled)
            {
                continue;
            }
        }

        return removed;
    }

    private async ValueTask RemoveFailedSessionAsync(FlowKey flow, Task<UdpProxySession> expected)
    {
        UdpProxySession? completedSession = null;
        if (expected.IsCompletedSuccessfully) completedSession = await expected.ConfigureAwait(false);
        var ownsSession = false;
        lock (_gate)
        {
            if (_sessions.TryGetValue(flow, out var current) && ReferenceEquals(current, expected))
            {
                if (completedSession is not null) _associations.TryRemove(completedSession.Association);
                _sessions.Remove(flow);
                ownsSession = true;
            }
        }
        if (!ownsSession) return;

        try
        {
            var session = completedSession ?? await expected.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) when (expected.IsFaulted || expected.IsCanceled)
        {
            GC.KeepAlive(expected);
        }
    }

    private void TrackFailedSetup(FlowKey flow, Task<UdpProxySession> sessionTask)
    {
        _ = ObserveFailedSetupAsync(flow, sessionTask);
    }

    private async Task ObserveFailedSetupAsync(FlowKey flow, Task<UdpProxySession> sessionTask)
    {
        try
        {
            _ = await sessionTask.ConfigureAwait(false);
        }
        catch (Exception) when (sessionTask.IsFaulted || sessionTask.IsCanceled)
        {
            await RemoveFailedSessionAsync(flow, sessionTask).ConfigureAwait(false);
        }
    }

    private void OnSessionActivity(UdpAssociation association, DateTimeOffset now) => _associations.TryTouch(association, now);

    private async Task RemoveReceiveFailedSessionAsync(UdpProxySession session)
    {
        Task<UdpProxySession>? expected = null;
        lock (_gate)
        {
            _sessions.TryGetValue(session.Flow, out expected);
        }
        if (expected is null) return;

        UdpProxySession currentSession;
        try
        {
            currentSession = await expected.ConfigureAwait(false);
        }
        catch (Exception) when (expected.IsFaulted || expected.IsCanceled)
        {
            return;
        }
        if (!ReferenceEquals(currentSession, session)) return;

        var ownsSession = false;
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.Flow, out var current) && ReferenceEquals(current, expected))
            {
                _associations.TryRemove(session.Association);
                _sessions.Remove(session.Flow);
                ownsSession = true;
            }
        }
        if (!ownsSession) return;
        await session.DisposeAsync().ConfigureAwait(false);
        LogDebug("udp.session.closed", session.Flow, session.FlowGeneration, session.Association, null);
    }

    private void LogDebug(string eventName, FlowKey flow, long flowGeneration, UdpAssociation association, string? serverName)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        _logger.Event(RuntimeLogLevel.Debug, eventName,
            new("flow", flowGeneration == 0 ? null : flowGeneration),
            new("udpAssociation", association.Generation), new("protocol", flow.Protocol),
            new("source", flow.Local), new("destination", flow.Remote), new("proxy", serverName));
    }

    private void LogTrace(string eventName, FlowKey flow, params RuntimeLogField[] additional)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        var fields = new RuntimeLogField[additional.Length + 3];
        fields[0] = new("protocol", flow.Protocol);
        fields[1] = new("source", flow.Local);
        fields[2] = new("destination", flow.Remote);
        additional.CopyTo(fields, 3);
        _logger.Event(RuntimeLogLevel.Trace, eventName, fields);
    }
}

internal sealed class UdpProxySession : IAsyncDisposable
{
    private readonly FlowKey _flow;
    private readonly long _flowGeneration;
    private readonly UdpAssociation _association;
    private readonly IUdpProxyTransport _transport;
    private readonly IUdpResponseSink _sink;
    private readonly CancellationToken _shutdown;
    private readonly TimeProvider _timeProvider;
    private readonly Action<UdpAssociation, DateTimeOffset> _activityObserver;
    private readonly IRuntimeLogger _logger;
    private readonly Lock _activityGate = new();
    private readonly Lock _disposeGate = new();
    private Task? _receiveLoop;
    private Task? _disposeTask;
    private Exception? _receiveFailure;
    private long _lastActivityTicks;
    private bool _expiring;
    private int _activeSends;

    public UdpProxySession(
        FlowKey flow,
        long flowGeneration,
        UdpAssociation association,
        IUdpProxyTransport transport,
        IUdpResponseSink sink,
        byte[]? clientMac,
        CancellationToken shutdown,
        TimeProvider timeProvider,
        Action<UdpAssociation, DateTimeOffset> activityObserver,
        IRuntimeLogger logger)
    {
        _flow = flow;
        _flowGeneration = flowGeneration;
        _association = association;
        _transport = transport;
        _sink = sink;
        ClientMac = clientMac;
        _shutdown = shutdown;
        _timeProvider = timeProvider;
        _activityObserver = activityObserver;
        _logger = logger;
        _lastActivityTicks = timeProvider.GetUtcNow().UtcTicks;
    }

    public FlowKey Flow => _flow;
    public long FlowGeneration => _flowGeneration;
    public UdpAssociation Association => _association;
    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>
    /// The client's Ethernet source MAC recorded from the first datagram of the flow. Forwarded
    /// (VM-originated) flows use it as the destination MAC of rebuilt responses so the vSwitch
    /// delivers them to the client instead of the host stack.
    /// </summary>
    public byte[]? ClientMac { get; }

    public void Start(Func<UdpProxySession, Task> receiveFailureHandler, Task registered)
    {
        ArgumentNullException.ThrowIfNull(receiveFailureHandler);
        ArgumentNullException.ThrowIfNull(registered);
        var receiveLoop = ReceiveLoopAsync();
        _receiveLoop = receiveLoop;
        _ = ObserveReceiveLoopAsync(receiveLoop, receiveFailureHandler, registered);
    }

    public async ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        lock (_activityGate)
        {
            var failure = Volatile.Read(ref _receiveFailure);
            if (failure is not null) throw new IOException("SOCKS5 UDP relay session is no longer usable.", failure);
            if (_expiring) throw new IOException("SOCKS5 UDP relay session is expiring.");
            _activeSends++;
        }

        try
        {
            await _transport.SendAsync(new IPEndPoint(destination.Address, destination.Port), payload, cancellationToken).ConfigureAwait(false);
            TouchActivity();
        }
        finally
        {
            lock (_activityGate) _activeSends--;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    public bool TryBeginExpiry(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_activityGate)
        {
            if (_expiring || _activeSends != 0 || now - LastActivityUtc < idleTimeout) return false;
            _expiring = true;
            return true;
        }
    }

    public void CancelExpiry()
    {
        lock (_activityGate) _expiring = false;
    }

    private async Task DisposeCoreAsync()
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
                return;
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
                TouchActivity();
                if (response.DestinationAddress is null) continue;
                var source = Endpoint.From(response.DestinationAddress, response.DestinationPort);
                await _sink.InjectAsync(_flow, source, response.Payload, ClientMac, _shutdown).ConfigureAwait(false);
                if (_logger.IsEnabled(RuntimeLogLevel.Trace))
                {
                    _logger.Event(RuntimeLogLevel.Trace, "udp.packet.received",
                        new("flow", _flowGeneration == 0 ? null : _flowGeneration),
                        new("udpAssociation", _association.Generation), new("source", source),
                        new("destination", _flow.Local), new("bytes", response.Payload.Length));
                }
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
            Volatile.Write(ref _receiveFailure, exception);
        }
    }

    private void TouchActivity()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_activityGate)
        {
            if (_expiring) return;
            Interlocked.Exchange(ref _lastActivityTicks, now.UtcTicks);
        }
        _activityObserver(_association, now);
    }

    private async Task ObserveReceiveLoopAsync(Task receiveLoop, Func<UdpProxySession, Task> receiveFailureHandler, Task registered)
    {
        await registered.ConfigureAwait(false);
        await receiveLoop.ConfigureAwait(false);
        if (Volatile.Read(ref _receiveFailure) is not null)
        {
            await receiveFailureHandler(this).ConfigureAwait(false);
        }
    }
}
