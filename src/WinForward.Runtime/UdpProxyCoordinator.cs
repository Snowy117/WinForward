using System.Buffers;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime;

public sealed class UdpProxyCoordinator : IAsyncDisposable
{
    private const int MaximumSocks5UdpHeaderSize = 22;
    private const int OversizeSentinelSize = 1;
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
    private readonly ArrayPool<byte> _receiveBufferPool;
    private readonly int _receiveBufferSize;
    private Task? _disposeTask;
    private bool _disposed;

    public UdpProxyCoordinator(
        IUdpProxyTransportFactory transportFactory,
        IUdpResponseSink responseSink,
        int capacity = 16_384,
        IRuntimeLogger? logger = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame)
        : this(transportFactory, responseSink, capacity, TimeProvider.System, null, logger, maximumFrameSize)
    {
    }

    internal UdpProxyCoordinator(
        IUdpProxyTransportFactory transportFactory,
        IUdpResponseSink responseSink,
        int capacity,
        TimeProvider timeProvider,
        Func<ValueTask>? beforeExpiryRecheck,
        IRuntimeLogger? logger = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        ArrayPool<byte>? receiveBufferPool = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(responseSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumFrameSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrameSize));
        _transportFactory = transportFactory;
        _responseSink = responseSink;
        _associations = new UdpAssociationTable(capacity);
        _capacity = capacity;
        _timeProvider = timeProvider;
        _beforeExpiryRecheck = beforeExpiryRecheck;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _receiveBufferPool = receiveBufferPool ?? ArrayPool<byte>.Shared;
        _receiveBufferSize = checked(maximumFrameSize + MaximumSocks5UdpHeaderSize + OversizeSentinelSize);
    }

    public async ValueTask<bool> TrySendAsync(FlowKey flow, Socks5Server server, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, long packetSequence = 0, long flowGeneration = 0, ReadOnlyMemory<byte> clientMac = default)
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
                    if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.session.rejected", flow, new RuntimeLogField("reason", "capacity"));
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
            if (_logger.IsEnabled(RuntimeLogLevel.Trace))
            {
                LogTrace("udp.packet.sent", flow, new RuntimeLogField("packet", packetSequence == 0 ? null : packetSequence), new RuntimeLogField("flow", session.FlowGeneration == 0 ? null : session.FlowGeneration), new RuntimeLogField("bytes", payload.Length), new RuntimeLogField("udpAssociation", session.Association.Generation));
            }
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

    private async Task<UdpProxySession> CreateSessionAsync(FlowKey flow, Socks5Server server, long flowGeneration, ReadOnlyMemory<byte> clientMac, CancellationToken cancellationToken, Task registered)
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

            var session = new UdpProxySession(flow, flowGeneration, association, transport, _responseSink, clientMac.IsEmpty ? null : clientMac.ToArray(), cancellationToken, _timeProvider, OnSessionActivity, _logger, _receiveBufferPool, _receiveBufferSize);
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
                if (!await TryRemoveSessionAsync(session.Flow, task, session).ConfigureAwait(false))
                {
                    session.CancelExpiry();
                    continue;
                }
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
        try
        {
            if (!await TryRemoveSessionAsync(flow, expected, completedSession).ConfigureAwait(false)) return;
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
        if (!await TryRemoveSessionAsync(session.Flow, expected, session).ConfigureAwait(false)) return;
        LogDebug("udp.session.closed", session.Flow, session.FlowGeneration, session.Association, null);
    }

    /// <summary>
    /// Shared ownsSession removal protocol for every teardown path: under the gate, the session
    /// task mapped to <paramref name="flow"/> is removed only when it is still the exact task
    /// instance <paramref name="expected"/> (a newer generation may have replaced it), and the
    /// association of <paramref name="resolvedSession"/> — the session resolved before the gate,
    /// when the task already completed successfully — is released in the same critical section.
    /// Disposal runs outside the gate; when <paramref name="resolvedSession"/> is null the task
    /// is awaited here so a failed setup is still removed and awaited. Returns true when this
    /// caller owned the removal; per-call-site logging and expiry bookkeeping stay with callers.
    /// </summary>
    private async Task<bool> TryRemoveSessionAsync(FlowKey flow, Task<UdpProxySession> expected, UdpProxySession? resolvedSession)
    {
        var ownsSession = false;
        lock (_gate)
        {
            if (_sessions.TryGetValue(flow, out var current) && ReferenceEquals(current, expected))
            {
                if (resolvedSession is not null) _associations.TryRemove(resolvedSession.Association);
                _sessions.Remove(flow);
                ownsSession = true;
            }
        }
        if (!ownsSession) return false;

        var session = resolvedSession ?? await expected.ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        return true;
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
