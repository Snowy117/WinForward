using System.Buffers;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.UdpProxy;

internal sealed class UdpProxySession : IAsyncDisposable
{
    /// <summary>Interval between per-session rate-limited summaries (skipped datagrams, injection failures).</summary>
    private static readonly TimeSpan RateLimitedLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum interval between activity propagations to the association table. The table
    /// serves reverse-leg classification and idle-sweep pruning on seconds-scale timeouts, so
    /// coarser propagation granularity is unobservable there, while the per-datagram cost of
    /// the touch drops to one Interlocked exchange. <see cref="LastActivityUtc"/> stays exact
    /// per operation, so idle-expiry semantics are unaffected.
    /// </summary>
    private static readonly TimeSpan ActivityPropagationInterval = TimeSpan.FromMilliseconds(100);

    private readonly FlowKey _flow;
    private readonly long _flowGeneration;
    private readonly UdpAssociation _association;
    private readonly IUdpProxyTransport _transport;
    private readonly IUdpResponseSink _sink;
    private readonly CancellationToken _shutdown;
    private readonly TimeProvider _timeProvider;
    private readonly Action<UdpAssociation, DateTimeOffset> _activityObserver;
    private readonly IRuntimeLogger _logger;
    private readonly ArrayPool<byte> _receiveBufferPool;
    private readonly int _receiveBufferSize;
    private readonly Lock _activityGate = new();
    private readonly Lock _disposeGate = new();
    private Task? _receiveLoop;
    private Task? _disposeTask;
    private Exception? _receiveFailure;
    private Func<UdpProxySession, Task>? _receiveFailureHandler;
    private long _lastActivityTicks;
    private long _lastActivityPropagationTicks;
    private long _lastSkipSummaryTicks;
    private long _lastInjectionFailureLogTicks;
    private long _skippedUnexpectedSource;
    private long _skippedOversized;
    private long _skippedMalformed;
    private long _skippedConnectionReset;
    private long _skippedDomainDestination;
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
        IRuntimeLogger logger,
        ArrayPool<byte> receiveBufferPool,
        int receiveBufferSize)
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
        _receiveBufferPool = receiveBufferPool;
        _receiveBufferSize = receiveBufferSize;
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

    public void Start(Func<UdpProxySession, Task> receiveFailureHandler)
    {
        ArgumentNullException.ThrowIfNull(receiveFailureHandler);
        _receiveFailureHandler = receiveFailureHandler;
        var receiveLoop = ReceiveLoopAsync();
        _receiveLoop = receiveLoop;
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
            // The raw Endpoint flows straight through: the transport encodes it into the SOCKS5
            // header, so the forward warm path allocates nothing for endpoint handling.
            await _transport.SendAsync(destination, payload, cancellationToken).ConfigureAwait(false);
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
        var buffer = _receiveBufferPool.Rent(_receiveBufferSize);
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                Socks5UdpReceiveResult receive;
                try
                {
                    receive = await _transport.ReceiveAsync(buffer.AsMemory(0, _receiveBufferSize), _shutdown).ConfigureAwait(false);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // An ICMP port-unreachable answering one of this session's relay sends (S2),
                    // skip-class even from transports that surface it directly, so never fatal
                    RecordSkippedDatagram(Socks5UdpReceiveSkipReason.ConnectionReset);
                    continue;
                }
                TouchActivity();
                if (!receive.HasDatagram)
                {
                    // One anomalous datagram (unexpected relay source, oversized, malformed) skips
                    // and the loop keeps receiving; only socket-level failures tear the session down.
                    RecordSkippedDatagram(receive.SkipReason);
                    continue;
                }
                var response = receive.Datagram;
                if (response.DestinationAddress is not { } address)
                {
                    // A domain-typed response has no IP source to rebuild the frame from (S6a):
                    // counted with the other skip-class anomalies, then skipped.
                    RecordSkippedDomainDestination();
                    continue;
                }
                await InjectResponseAsync(Endpoint.From(address, response.DestinationPort), response).ConfigureAwait(false);
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
        finally
        {
            _receiveBufferPool.Return(buffer);
        }

        if (Volatile.Read(ref _receiveFailure) is not null)
        {
            // Fire-and-forget on purpose: the handler tears this session down, and awaiting it
            // here would make session disposal (which awaits this loop) re-enter itself.
            _ = _receiveFailureHandler!(this);
        }
    }

    /// <summary>Hands one decoded relay response to the reinjection sink; per-response failures skip.</summary>
    private async Task InjectResponseAsync(Endpoint source, Socks5UdpDatagram response)
    {
        try
        {
            await _sink.InjectAsync(_flow, source, response.Payload, ClientMac, _shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A failed reinjection of one response must not kill the flow; skip it and continue.
            LogInjectionFailureRateLimited(exception);
        }

        if (_logger.IsEnabled(RuntimeLogLevel.Trace))
        {
            _logger.Event(RuntimeLogLevel.Trace, "udp.packet.received",
                new("flow", _flowGeneration == 0 ? null : _flowGeneration),
                new("udpAssociation", _association.Generation), new("source", source),
                new("destination", _flow.Local), new("bytes", response.Payload.Length));
        }
    }

    private void RecordSkippedDatagram(Socks5UdpReceiveSkipReason reason)
    {
        switch (reason)
        {
            case Socks5UdpReceiveSkipReason.UnexpectedSource:
                Interlocked.Increment(ref _skippedUnexpectedSource);
                break;
            case Socks5UdpReceiveSkipReason.Oversized:
                Interlocked.Increment(ref _skippedOversized);
                break;
            case Socks5UdpReceiveSkipReason.Malformed:
                Interlocked.Increment(ref _skippedMalformed);
                break;
            case Socks5UdpReceiveSkipReason.ConnectionReset:
                Interlocked.Increment(ref _skippedConnectionReset);
                break;
            case Socks5UdpReceiveSkipReason.None:
            default:
                return;
        }

        MaybeLogSkipSummary();
    }

    private void RecordSkippedDomainDestination()
    {
        Interlocked.Increment(ref _skippedDomainDestination);
        MaybeLogSkipSummary();
    }

    private void MaybeLogSkipSummary()
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastSkipSummaryTicks);
        if (now - last < RateLimitedLogInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSkipSummaryTicks, now, last) != last) return;
        var unexpected = Interlocked.Exchange(ref _skippedUnexpectedSource, 0);
        var oversized = Interlocked.Exchange(ref _skippedOversized, 0);
        var malformed = Interlocked.Exchange(ref _skippedMalformed, 0);
        var connectionReset = Interlocked.Exchange(ref _skippedConnectionReset, 0);
        var domainDestination = Interlocked.Exchange(ref _skippedDomainDestination, 0);
        if (unexpected + oversized + malformed + connectionReset + domainDestination == 0) return;
        _logger.Debug($"SOCKS5 UDP relay skipped datagrams in the last window: unexpectedSource={unexpected} oversized={oversized} malformed={malformed} connectionReset={connectionReset} domainDestination={domainDestination}.");
    }

    private void LogInjectionFailureRateLimited(Exception exception)
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastInjectionFailureLogTicks);
        if (now - last < RateLimitedLogInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastInjectionFailureLogTicks, now, last) != last) return;
        _logger.Warn($"UDP response reinjection failed; the response was skipped: {exception.GetType().Name}: {exception.Message}");
    }

    private void TouchActivity()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_activityGate)
        {
            if (_expiring) return;
            Interlocked.Exchange(ref _lastActivityTicks, now.UtcTicks);
        }

        // Propagate to the association table at most once per interval. Zero means "never
        // propagated", so a session's first activity — and the first activity after any
        // quieter-than-interval gap — is delivered immediately.
        var nowTicks = now.UtcTicks;
        var lastPropagation = Interlocked.Read(ref _lastActivityPropagationTicks);
        if (nowTicks - lastPropagation < ActivityPropagationInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastActivityPropagationTicks, nowTicks, lastPropagation) != lastPropagation) return;
        _activityObserver(_association, now);
    }
}
