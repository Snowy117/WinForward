using System.Globalization;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The construction context of one UDP relay session: the flow identity, the claimed relay
/// association, the transport/sink collaborators, the recorded client MAC, and the activity
/// plumbing the session reports through. Grouping them gives the setup pipeline (and the tests
/// that construct sessions directly) one named construction vocabulary; the positional order
/// mirrors the former constructor parameters. A value type deliberately: the context is copied
/// into the session constructor and the instance is never retained, so as a record class it was
/// one heap allocation per session (240 B/session measured, probe C2a1) with no reader of its
/// identity — record value equality is unchanged by the shape.
/// </summary>
internal readonly record struct UdpProxySessionContext(
    FlowKey Flow,
    long FlowGeneration,
    UdpAssociation Association,
    IUdpProxyTransport Transport,
    IUdpResponseSink Sink,
    MacAddress ClientMac,
    TimeProvider TimeProvider,
    Action<UdpAssociation, DateTimeOffset> ActivityObserver,
    IRuntimeLogger Logger,
    NativeBufferPool ReceiveWindowPool,
    int ReceiveBufferSize,
    CancellationToken Shutdown);

internal sealed class UdpProxySession : IAsyncDisposable
{
    /// <summary>Interval between per-session rate-limited summaries (skipped datagrams, injection failures).</summary>
    private static readonly TimeSpan s_rateLimitedLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum interval between activity propagations to the association table. The table
    /// serves reverse-leg classification and idle-sweep pruning on seconds-scale timeouts, so
    /// coarser propagation granularity is unobservable there, while the per-datagram cost of
    /// the touch drops to one Interlocked exchange. <see cref="LastActivityUtc"/> stays exact
    /// per operation, so idle-expiry semantics are unaffected.
    /// </summary>
    private static readonly TimeSpan s_activityPropagationInterval = TimeSpan.FromMilliseconds(100);
    private readonly IUdpProxyTransport _transport;
    private readonly IUdpResponseSink _sink;
    private readonly QuiescenceScope _scope;
    private readonly TimeProvider _timeProvider;
    private readonly Action<UdpAssociation, DateTimeOffset> _activityObserver;
    private readonly IRuntimeLogger _logger;
    private readonly NativeBufferPool _receiveWindowPool;
    private readonly int _receiveBufferSize;
    private readonly Lock _activityGate = new();
    private Task? _receiveLoop;
    private int _teardownStarted;
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

    public UdpProxySession(UdpProxySessionContext context)
    {
#pragma warning disable MA0015, S3928, CA2208 // The paramName deliberately names the null member (the context parameter itself is never null); these analyzers only accept declared parameter names, which would point diagnosis at a phantom "context".
        ArgumentNullException.ThrowIfNull(context.ReceiveWindowPool);
        if (context.ReceiveWindowPool.BufferSize < context.ReceiveBufferSize)
        {
            throw new ArgumentException("The receive-window pool supplies buffers smaller than the session receive window.", nameof(context.ReceiveWindowPool));
        }
#pragma warning restore MA0015, S3928, CA2208
        Flow = context.Flow;
        FlowGeneration = context.FlowGeneration;
        Association = context.Association;
        _transport = context.Transport;
        _sink = context.Sink;
        ClientMac = context.ClientMac;
        _scope = new QuiescenceScope(context.Shutdown);
        _timeProvider = context.TimeProvider;
        _activityObserver = context.ActivityObserver;
        _logger = context.Logger;
        _receiveWindowPool = context.ReceiveWindowPool;
        _receiveBufferSize = context.ReceiveBufferSize;
        _lastActivityTicks = context.TimeProvider.GetUtcNow().UtcTicks;
    }

    public FlowKey Flow { get; }
    public long FlowGeneration { get; }
    public UdpAssociation Association { get; }
    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>
    /// The session-level lifecycle state, read under the activity gate (the lock that orders the
    /// admission policy <c>_expiring</c> against the scope's closed/faulted facts, so the
    /// fault-vs-send race is exactly the pre-scope one); see <see cref="UdpSessionState"/> for the
    /// transition rules. Slot-level <see cref="UdpSessionState.SettingUp"/> is derived by the
    /// coordinator from the slot before a session exists.
    /// </summary>
    internal UdpSessionState State
    {
        get
        {
            lock (_activityGate)
            {
                if (_scope.IsSealed) return UdpSessionState.Disposed;
                if (_scope.Fault is not null) return UdpSessionState.Faulted;
                return _expiring ? UdpSessionState.Expiring : UdpSessionState.Active;
            }
        }
    }

    /// <summary>
    /// The client's Ethernet source MAC recorded from the first datagram of the flow. Forwarded
    /// (VM-originated) flows use it as the destination MAC of rebuilt responses so the vSwitch
    /// delivers them to the client instead of the host stack.
    /// </summary>
    private MacAddress ClientMac { get; }
    public void Start(Action<UdpProxySession> receiveFailureHandler)
    {
        ArgumentNullException.ThrowIfNull(receiveFailureHandler);
        _receiveLoop = ReceiveLoopAsync(receiveFailureHandler);
    }

    /// <summary>
    /// Sends one datagram through the shared transport, returning whether it was handed off:
    /// <see langword="false"/> means the session refused it because it is expiring or already
    /// faulted (the owner of that state — the sweeper for expiry, the failure handler for a fault
    /// — owns the slot removal, so the caller just counts the drop). The entry is non-async
    /// because the payload span must not cross an await — the transport consumes it synchronously
    /// (SOCKS5 encode into its reusable send buffer) before any asynchronous socket operation, and
    /// only the send tail continues asynchronously without the span.
    /// </summary>
    public ValueTask<bool> SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        WorkLease workLease;
        lock (_activityGate)
        {
            if (_scope.Fault is not null || _expiring || !_scope.TryEnter(out workLease))
            {
                return ValueTask.FromResult(false);
            }
        }

        ValueTask send;
        try
        {
            send = _transport.SendSpanAsync(destination, payload, cancellationToken);
        }
        catch
        {
            workLease.Dispose();
            throw;
        }

        if (send.IsCompletedSuccessfully)
        {
            TouchActivity();
            workLease.Dispose();
            return ValueTask.FromResult(true);
        }
        return FinishSpanSendAsync(send, workLease);
    }

    private async ValueTask<bool> FinishSpanSendAsync(ValueTask send, WorkLease workLease)
    {
        try
        {
            await send.ConfigureAwait(false);
            TouchActivity();
            return true;
        }
        finally
        {
            workLease.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        // D11: the scope's single-flight covers only the drain, and sealing happens inside it, so a
        // precheck on IsSealed would be TOCTOU. The one-shot claim owns the transport/loop teardown
        // every caller joins the drain, which the teardown body reaches last.
        return Interlocked.Exchange(ref _teardownStarted, 1) != 0
            ? new ValueTask(_scope.DrainAsync())
            : new ValueTask(DisposeCoreAsync());
    }

    internal bool TryBeginExpiry(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_activityGate)
        {
            if (_scope.IsSealed || _expiring || !_scope.IsIdle || now - LastActivityUtc < idleTimeout) return false;
            _expiring = true;
        }

        // Cancelling the per-session scope (not the coordinator shutdown) ends the receive loop as a
        // normal teardown: an idle-expired session must not record a receive failure for the
        // cancellation it asked for.
        _scope.Cancel();
        return true;
    }

    internal void CancelExpiry()
    {
        lock (_activityGate) _expiring = false;
    }

    private async Task DisposeCoreAsync()
    {
        var token = _scope.Token;
        _scope.Cancel();
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            if (_receiveLoop is not null)
            {
                try { await _receiveLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Cancellation is the expected teardown path (idle expiry or a coordinator shutdown).
                }
                catch (ObjectDisposedException)
                {
                    // Disposal already tore the receive loop down; nothing left to observe here.
                }
            }
        }
        finally
        {
            await _scope.DrainAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The session's receive loop. One async method on purpose: the former
    /// <c>ReceiveLoopAsync</c> + <c>ReceiveDatagramsAsync</c> pair boxed two state machines (and two
    /// Task objects) per session for a single loop that the outer method called exactly once, so the
    /// split bought no seam. The loop reads the scope token and rents the receive window once up
    /// front, and signals a genuine receive fault after the lease is released.
    /// </summary>
    private async Task ReceiveLoopAsync(Action<UdpProxySession> receiveFailureHandler)
    {
        var token = _scope.Token;
        var lease = _receiveWindowPool.Rent();
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socks5UdpReceiveResult receive;
                try
                {
                    receive = await _transport.ReceiveAsync(lease.Memory[.._receiveBufferSize], token).ConfigureAwait(false);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // An ICMP port-unreachable answering one of this session's relay sends (S2),
                    // skip-class even from transports that surface it directly, so never fatal
                    RecordSkippedDatagram(Socks5UdpReceiveSkipReason.ConnectionReset);
                    continue;
                }
                TouchActivity();
                if (!TryGetReceiveSource(receive, out var source))
                {
                    continue;
                }

                await InjectResponseAsync(source, receive.Datagram, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal teardown path: idle expiry or a coordinator shutdown.
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
            // Disposal closes the receive socket during teardown.
        }
        catch (Exception exception)
        {
            _scope.RecordFault(exception, "udp.receive");
        }
        finally
        {
            lease.Dispose();
        }

        if (_scope.Fault is not null)
        {
            // A synchronous signal, deliberately not awaited (F1): the handler starts this session's
            // teardown on the coordinator's scope, and awaiting it here would re-enter session
            // disposal, which joins this loop.
            receiveFailureHandler(this);
        }
    }

    /// <summary>
    /// Classifies one receive result: one anomalous datagram (unexpected relay source, oversized,
    /// malformed) skips and the loop keeps receiving, a domain-typed response has no IP source to
    /// rebuild the frame from (S6a) and is counted with the other skip-class anomalies, and only a
    /// response with a decodable source yields its endpoint. Both skips are counted here; only
    /// socket-level failures tear the session down.
    /// </summary>
    private bool TryGetReceiveSource(Socks5UdpReceiveResult receive, out Endpoint source)
    {
        if (!receive.HasDatagram)
        {
            RecordSkippedDatagram(receive.SkipReason);
            source = default;
            return false;
        }

        var response = receive.Datagram;
        if (response.DestinationAddress is not { } address)
        {
            RecordSkippedDomainDestination();
            source = default;
            return false;
        }

        source = Endpoint.From(address, response.DestinationPort);
        return true;
    }

    /// <summary>Hands one decoded relay response to the reinjection sink; per-response failures skip.</summary>
    private async Task InjectResponseAsync(Endpoint source, Socks5UdpDatagram response, CancellationToken token)
    {
        try
        {
            await _sink.InjectAsync(Flow, source, response.Payload, ClientMac, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
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
                new("flow", FlowGeneration == 0 ? null : FlowGeneration),
                new("udpAssociation", Association.Generation), new("source", source),
                new("destination", Flow.Local), new("bytes", response.Payload.Length));
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
        if (now - last < s_rateLimitedLogInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSkipSummaryTicks, now, last) != last) return;
        var unexpected = Interlocked.Exchange(ref _skippedUnexpectedSource, 0);
        var oversized = Interlocked.Exchange(ref _skippedOversized, 0);
        var malformed = Interlocked.Exchange(ref _skippedMalformed, 0);
        var connectionReset = Interlocked.Exchange(ref _skippedConnectionReset, 0);
        var domainDestination = Interlocked.Exchange(ref _skippedDomainDestination, 0);
        if (unexpected + oversized + malformed + connectionReset + domainDestination == 0) return;
        _logger.Debug(string.Create(CultureInfo.InvariantCulture, $"SOCKS5 UDP relay skipped datagrams in the last window: unexpectedSource={unexpected} oversized={oversized} malformed={malformed} connectionReset={connectionReset} domainDestination={domainDestination}."));
    }

    private void LogInjectionFailureRateLimited(Exception exception)
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastInjectionFailureLogTicks);
        if (now - last < s_rateLimitedLogInterval.Ticks) return;
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
        if (nowTicks - lastPropagation < s_activityPropagationInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastActivityPropagationTicks, nowTicks, lastPropagation) != lastPropagation) return;
        _activityObserver(Association, now);
    }
}
