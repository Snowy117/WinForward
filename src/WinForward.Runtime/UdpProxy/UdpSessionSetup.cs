using System.Buffers;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The SOCKS5 dial/claim/construct/flush pipeline behind every UDP flow's first datagram: the
/// 8-wide setup limiter (patient admission), the dial-start queue re-stamp, the association
/// claim, session construction, and the FIFO flush of datagrams buffered while setup was in
/// flight. Runs entirely off the coordinator gate — coordinator slot state is touched only via
/// ctor-injected delegates (attach, re-stamp, flush dequeue, slot removal), each of which takes
/// the coordinator gate internally, so the gate stays the single arbiter of slot state. The
/// analog of the TCP redirect setup/acceptor pair. Owns the 2026-09-06 dial-start re-stamp fix
/// (limiter queue-wait is not client staleness) as one cohesive unit.
/// </summary>
internal sealed class UdpSessionSetup
{
    private const int MaximumConcurrentSetups = 8;

    /// <summary>
    /// How long a buffered setup datagram stays deliverable, measured from its flow's dial
    /// start (the re-stamp applied when the setup leaves the limiter queue). A normal SOCKS5
    /// dial completes in well under a second; a datagram still queued after this window has
    /// already been retransmitted or abandoned by the application, so the flush delivers only
    /// fresh state.
    /// </summary>
    private static readonly TimeSpan SetupQueueDatagramTtl = TimeSpan.FromSeconds(5);

    private readonly IUdpProxyTransportFactory _transportFactory;
    private readonly UdpAssociationTable _associations;
    private readonly IUdpResponseSink _responseSink;
    private readonly TimeProvider _timeProvider;
    private readonly IRuntimeLogger _logger;
    private readonly ArrayPool<byte> _receiveBufferPool;
    private readonly int _receiveBufferSize;
    private readonly Func<FlowKey, UdpProxyCoordinator.UdpSessionSlot, int> _refreshSetupStamps;
    private readonly Action<UdpProxyCoordinator.UdpSessionSlot, UdpProxySession> _attachSession;
    private readonly Func<FlowKey, UdpProxyCoordinator.UdpSessionSlot, (FlushStep Step, ReadOnlyMemory<byte> Pending, DateTimeOffset EnqueuedAt)> _dequeueForFlush;
    private readonly Func<FlowKey, UdpProxyCoordinator.UdpSessionSlot, bool, Task> _removeSlot;
    private readonly Func<UdpProxySession, Task> _removeReceiveFailedSession;
    private readonly SemaphoreSlim _setupLimiter = new(MaximumConcurrentSetups, MaximumConcurrentSetups);
    private long _ttlExpiredCount;
    private long _stampsRefreshedCount;

    public UdpSessionSetup(
        IUdpProxyTransportFactory transportFactory,
        UdpAssociationTable associations,
        IUdpResponseSink responseSink,
        TimeProvider timeProvider,
        IRuntimeLogger logger,
        ArrayPool<byte> receiveBufferPool,
        int receiveBufferSize,
        Func<FlowKey, UdpProxyCoordinator.UdpSessionSlot, int> refreshSetupStamps,
        Action<UdpProxyCoordinator.UdpSessionSlot, UdpProxySession> attachSession,
        Func<FlowKey, UdpProxyCoordinator.UdpSessionSlot, (FlushStep Step, ReadOnlyMemory<byte> Pending, DateTimeOffset EnqueuedAt)> dequeueForFlush,
        Func<FlowKey, UdpProxyCoordinator.UdpSessionSlot, bool, Task> removeSlot,
        Func<UdpProxySession, Task> removeReceiveFailedSession)
    {
        _transportFactory = transportFactory;
        _associations = associations;
        _responseSink = responseSink;
        _timeProvider = timeProvider;
        _logger = logger;
        _receiveBufferPool = receiveBufferPool;
        _receiveBufferSize = receiveBufferSize;
        _refreshSetupStamps = refreshSetupStamps;
        _attachSession = attachSession;
        _dequeueForFlush = dequeueForFlush;
        _removeSlot = removeSlot;
        _removeReceiveFailedSession = removeReceiveFailedSession;
    }

    /// <summary>The total buffered datagrams dropped at flush for exceeding the setup TTL; for tests and diagnostics.</summary>
    internal long TtlExpiredCount => Interlocked.Read(ref _ttlExpiredCount);

    /// <summary>The total queue entries re-stamped at setup dial start (limiter queue-wait does not age a datagram); for tests and diagnostics.</summary>
    internal long StampsRefreshedCount => Interlocked.Read(ref _stampsRefreshedCount);

    /// <summary>The outcome of one flush-dequeue step under the coordinator gate; the flush loop continues only on <see cref="FlushStep.Dequeued"/>.</summary>
    internal enum FlushStep
    {
        /// <summary>Another owner took the slot's teardown; the flush must stop without touching the slot.</summary>
        NotOwner,

        /// <summary>A datagram left the queue (its budget charge was released); deliver it below the gate.</summary>
        Dequeued,

        /// <summary>The queue drained empty; the slot was flipped ready in the same critical section.</summary>
        QueueEmpty,
    }

    internal async Task CreateSessionAsync(FlowKey flow, Socks5Server server, long flowGeneration, byte[]? clientMac, CancellationToken cancellationToken, UdpProxyCoordinator.UdpSessionSlot slot)
    {
        // Patient admission: a flash crowd of new flows must queue behind the 8-wide setup
        // gate rather than fail into the setup cooldown, because a failed setup's teardown
        // drains the setup queue and drops the already-accepted triggering datagram (that
        // drop, not the steady-state relay, was the entire measured in-window soak loss).
        // Queued setups observe shutdown cancellation here (no cooldown is armed); genuine
        // setup failures below still fail closed with the setup cooldown.
        await _setupLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        IUdpProxyTransport? transport = null;
        UdpAssociation? association = null;
        var associationCreated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Dial start: the datagram waited on the setup limiter, not on the network, so its
            // queue is re-stamped before the dial and the TTL below measures dial age.
            RefreshSetupStampsAtDialStart(flow, slot);
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

            var session = new UdpProxySession(flow, flowGeneration, association, transport, _responseSink, clientMac, cancellationToken, _timeProvider, OnSessionActivity, _logger, _receiveBufferPool, _receiveBufferSize);
            transport = null;
            _attachSession(slot, session);
            session.Start(_removeReceiveFailedSession);
            UdpProxyLogging.LogDebug(_logger, "udp.session.created", flow, flowGeneration, association, server.Name);
            await FlushSetupQueueAsync(flow, slot, session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (associationCreated && association is not null) _associations.TryRemove(association);
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            // Handle the failure here instead of rethrowing through a second observer task:
            // the setup state machine is already boxed at its first await, so the log, the
            // setup cooldown, and the slot removal ride this frame at no extra cost.
            // Shutdown cancellation keeps the no-cooldown semantics the observer had.
            UdpProxyLogging.LogSetupFailure(_logger, flow, exception);
            await _removeSlot(flow, slot, exception is not OperationCanceledException).ConfigureAwait(false);
        }
        finally
        {
            _setupLimiter.Release();
        }
    }

    /// <summary>
    /// Re-stamps the slot's queued datagrams at dial start: the setup just left the setup
    /// limiter, and the queue-wait so far was admission delay on WinForward's side rather than
    /// client-side staleness, so the flush TTL measures age from this boundary instead of from
    /// the enqueue. A lost slot owner (teardown raced the limiter wait) skips the refresh; its
    /// flush returns at the ownership check anyway.
    /// </summary>
    private void RefreshSetupStampsAtDialStart(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot)
    {
        var refreshed = _refreshSetupStamps(flow, slot);
        Interlocked.Add(ref _stampsRefreshedCount, refreshed);
    }

    /// <summary>
    /// Drains the flow's setup queue in FIFO order through the live session, then flips the slot
    /// to ready. The queue-empty check and the ready transition share one critical section (the
    /// coordinator-side dequeue step), so a concurrent send either enqueued before that step
    /// finished (and is drained here) or sees the ready slot and sends inline: no datagram can
    /// bypass the queue and then be followed by an older queued one. A send failure propagates
    /// to the setup task's failure observer. Datagrams whose age exceeds the setup TTL are
    /// dropped at this boundary — the flush is the single point where age is observable against
    /// the dequeue time, and delivering them would replay state the application has already
    /// retransmitted or abandoned. Age runs from the dial-start re-stamp (see
    /// <see cref="RefreshSetupStampsAtDialStart"/>): enqueue stamps older than that reflect
    /// admission queue-wait, which is not client staleness.
    /// </summary>
    private async Task FlushSetupQueueAsync(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot, UdpProxySession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            var step = _dequeueForFlush(flow, slot);
            if (step.Step != FlushStep.Dequeued) return;

            if (_timeProvider.GetUtcNow() - step.EnqueuedAt > SetupQueueDatagramTtl)
            {
                Interlocked.Increment(ref _ttlExpiredCount);
                continue;
            }

            await session.SendAsync(flow.Remote, step.Pending, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the setup limiter. The coordinator's dispose awaits every started setup task
    /// (each releases the limiter in its finally) before calling this, and a setup starting
    /// later observes the cancelled shutdown token before acquiring the limiter.
    /// </summary>
    internal void DisposeLimiter() => _setupLimiter.Dispose();

    private void OnSessionActivity(UdpAssociation association, DateTimeOffset now) => _associations.TryTouch(association, now);
}
