using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Logging;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.TcpRedirect;

[SupportedOSPlatform("windows")]
public sealed class TcpProxyRelayFactory(SelfTrafficRegistry selfTraffic, ILogger? logger = null, NativeBufferPool? pumpBufferPool = null, Socks5AddressCache? addressCache = null) : ITcpProxyRelayFactory
{
    // The redirect leg completes the client's TCP handshake in tens of milliseconds, so the relay's
    // upstream connect budget bounds how long an unreachable/black-holed SOCKS5 server delays the
    // client's reset: worst case DNS + two ten-second attempts instead of the per-attempt 30s
    // defaults (worst case ~150s). Refused/unreachable failures still surface in sub-second time
    // because a rejected connect fails the attempt immediately.
    internal const int RelayConnectMaxAttempts = 2;
    internal static readonly TimeSpan s_relayConnectAttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The per-direction relay pump window; the bundle-owned relay pool uses this size.</summary>
    public const int PumpBufferSize = 64 * 1024;

    /// <summary>
    /// Establishes the relay for a connection this assembly did not accept: a backend that owns its
    /// own listener hands over the already-connected client socket plus the destination the client
    /// asked for, and the socket is wrapped in the same accepted connection the redirect listener
    /// produces so the SOCKS5 control/connect path is identical to the packet path.
    /// <para>
    /// Ownership transfers on success — the relay closes the socket with its pumps — so the caller
    /// must not dispose it afterwards. A failed call leaves the socket with the caller, which still
    /// owns it and is expected to close it abortively rather than reuse it.
    /// </para>
    /// <para>
    /// The relay from this entry closes the client leg abortively for a non-clean end: a
    /// <see cref="RelayEndKind.Stalled"/> or <see cref="RelayEndKind.Faulted"/> end arms linger 0 and
    /// closes the socket from inside the pumps (the peer sees a reset where the plain close would send
    /// a FIN), while a clean end keeps the normal FIN. The close cannot be left to the consumer: once
    /// <see cref="ITcpRelay.Completion"/> completes the socket is already gone. This local abort carries
    /// the reset the packet path gets from the acceptor's crafted RST|ACK.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="acceptedSocket"/> is not connected to an
    /// IP peer; an unconnected socket cannot identify the accepted connection.</exception>
    public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, Socket acceptedSocket, Socks5Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptedSocket);
        if (acceptedSocket.RemoteEndPoint is not IPEndPoint remote)
        {
            throw new ArgumentException("The accepted socket must be connected to an IP peer.", nameof(acceptedSocket));
        }

        // The client leg is a byte pipe, and both relay legs carry the NoDelay invariant: the
        // listener's accept and the upstream connect set it on their sockets. This entry replaces
        // the listener, so it must reproduce the client half — otherwise small proxied writes
        // stall 40-200 ms on Nagle x delayed ACK.
        acceptedSocket.NoDelay = true;
        var acceptedConnection = new TcpAcceptedConnection(
            acceptedSocket,
            Endpoint.From(remote.Address, checked((ushort)remote.Port)));
        return EstablishCoreAsync(originalDestination, acceptedConnection, server, abortiveCloseOnFault: true, cancellationToken);
    }

    public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptedConnection);
        ArgumentNullException.ThrowIfNull(server);

        if (acceptedConnection is not TcpAcceptedConnection concrete)
        {
            throw new ArgumentException("The accepted connection must be a TcpAcceptedConnection.", nameof(acceptedConnection));
        }

        // The packet path keeps the plain local close: the acceptor's crafted RST|ACK is the
        // client-visible abort for a non-clean end, and a relay-side abortive close would add a
        // second, uncontrolled reset behind it.
        return EstablishCoreAsync(originalDestination, concrete, server, abortiveCloseOnFault: false, cancellationToken);
    }

    private async ValueTask<ITcpRelay> EstablishCoreAsync(Endpoint originalDestination, TcpAcceptedConnection acceptedConnection, Socks5Server server, bool abortiveCloseOnFault, CancellationToken cancellationToken)
    {
        // Register the upstream control connection's exact tuple before its SYN leaves the host.
        // The socket is bound to a wildcard local endpoint, so the registration uses Any:port and
        // the wildcard matcher in SelfTrafficRegistry covers the routing-chosen source IP. This
        // mirrors the UDP relay transport and prevents a catch-all proxy rule from recursively
        // intercepting WinForward's own SOCKS5 control traffic.
        var control = await Socks5ControlConnection.ConnectAsync(server, cancellationToken, (local, remote) =>
            selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Tcp,
                Endpoint.From(local.Address, checked((ushort)local.Port)),
                Endpoint.From(remote.Address, checked((ushort)remote.Port)))),
            maxAttempts: RelayConnectMaxAttempts,
            perAttemptTimeout: s_relayConnectAttemptTimeout,
            addressCache: addressCache).ConfigureAwait(false);
        try
        {
            var destinationAddress = originalDestination.Address;
            await control.ConnectDestinationAsync(new IPEndPoint(destinationAddress.ToIPAddress(), originalDestination.Port), cancellationToken).ConfigureAwait(false);

            var upstream = control.GetUpstreamStream();
            return new TcpProxyRelay(acceptedConnection.Socket, upstream, control, logger, pumpBufferPool, abortiveCloseOnFault);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Why a relay ended: both pumps completed with FINs propagated, a pump stalled past the stall
/// window (the relay then completes without faulting), or a pump faulted. Meaningful only once
/// <see cref="ITcpRelay.Completion"/> completes.
/// </summary>
public enum RelayEndKind
{
    CleanEnded,
    Stalled,
    Faulted,
}

/// <summary>
/// The relay surface that reports <see cref="EndKind"/>. A capability rather than a member of
/// <see cref="ITcpRelay"/>, because not every relay can classify its end; a relay that does not
/// implement it is treated as a clean end (no client reset).
/// <para>
/// The client-visible close mode is pinned by the establishment entry, not by the consumer: the
/// raw-socket entry arms the relay's abortive close for a <see cref="RelayEndKind.Stalled"/> or
/// <see cref="RelayEndKind.Faulted"/> end, and the packet path leaves the close to the acceptor's
/// crafted RST|ACK. A consumer outside this assembly — one that owns the accepted socket instead of
/// borrowing the redirect listener — reads the kind after <see cref="ITcpRelay.Completion"/> to count
/// and log the outcome; it cannot set the linger then, because the relay's pumps have already closed
/// the socket.
/// </para>
/// </summary>
public interface ITcpRelayEndInfo
{
    /// <summary>Why the relay ended; meaningful only after <see cref="ITcpRelay.Completion"/> completes.</summary>
    RelayEndKind EndKind { get; }

    /// <summary>Server-to-client stream bytes written to the client-facing socket; meaningful only
    /// after <see cref="ITcpRelay.Completion"/> completes.</summary>
    long ServerStreamBytes { get; }
}

[SupportedOSPlatform("windows")]
internal sealed class TcpProxyRelay : ITcpRelay, ITcpRelayEndInfo
{
    private const int PumpBufferSize = TcpProxyRelayFactory.PumpBufferSize;
    /// <summary>
    /// The pump-window pool used when composition does not inject one (direct constructions in
    /// tests and benchmarks). Production always injects the bundle-owned, counter-registered pool.
    /// </summary>
    private static readonly NativeBufferPool s_sharedPumpBufferPool = new(PumpBufferSize, capacity: 64);
    // A relay that makes no progress in one direction for this long is considered stalled and the
    // whole relay is reclaimed. Established connections that are merely idle at the packet
    // level (e.g. SSH with keepalives) keep traffic flowing in both directions (data + ACKs), so
    // this generous stall window only fires for a genuinely dead peer and cannot be held forever
    // by <see cref="TcpProxyRelay"/>. Teardown is otherwise tied to the relay ending, not to a
    // per-flow wall-clock idle timeout.
    private static readonly TimeSpan s_stallTimeout = TimeSpan.FromMinutes(30);    // The re-arm throttle: one per second is enough for a 30-minute window, during which the
    // window drifts by at most one second. Skipping the per-chunk TryReset + CancelAfter
    // timer-queue updates saves ~100-200 ns per operation at 10 Gbps chunk rates.
    internal static readonly long s_armThrottleTicks = Stopwatch.Frequency;

    // Only reachable if a pump reports a fault it did not record, which the pump body cannot do.
    private const string PumpFaultMessage = "the relay pump failed";

    private readonly Socket _localSocket;
    private readonly IAsyncDisposable _control;
    private readonly ILogger _logger;
    private readonly NativeBufferPool _pumpBufferPool;
    // Whether a non-clean end closes the client-facing socket abortively (linger 0). Opt-in for the
    // socket-owning entry, which has no acceptor to inject the packet path's crafted RST|ACK; the
    // packet path leaves the close plain so its acceptor's abort stays the single client-visible
    // reset. The abort runs inside the pumps, the only place that executes while the socket is still
    // open — a consumer setting the linger after Completion would write to a closed socket.
    private readonly bool _abortiveCloseOnFault;
    // Owns the pumps' lifetime token and joins the pumps when the relay is disposed. It is sealed
    // and drained only after the local socket is closed, which is what forces a pump the stall
    // fast-exit abandoned to return.
    private readonly QuiescenceScope _scope = new();
    private int _teardownStarted;
    private long _serverStreamBytes;

    public TcpProxyRelay(Socket localSocket, Stream upstream, IAsyncDisposable control, ILogger? logger = null, NativeBufferPool? pumpBufferPool = null, bool abortiveCloseOnFault = false)
    {
        ArgumentNullException.ThrowIfNull(localSocket);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(control);
        _localSocket = localSocket;
        _control = control;
        _logger = logger ?? NullLogger.Instance;
        _pumpBufferPool = pumpBufferPool ?? s_sharedPumpBufferPool;
        _abortiveCloseOnFault = abortiveCloseOnFault;
        Completion = RunPumpAsync(upstream);
    }

    public Task Completion { get; }

    // Defaults to the fail-visible kind: a relay whose pumps never started (a construction-time
    // throw before the run body) completes faulted without ever classifying itself, and that end
    // must still surface as a client reset. CleanEnded is only ever assigned explicitly, after
    // both pumps verifiably completed.
    public RelayEndKind EndKind { get; private set; } = RelayEndKind.Faulted;

    public long ServerStreamBytes => Volatile.Read(ref _serverStreamBytes);

    private async Task RunPumpAsync(Stream upstream)
    {
        await using var localStream = new NetworkStream(_localSocket, ownsSocket: true);
        var token = _scope.Token;
        var localToUpstream = PumpAsync(localStream, upstream, _pumpBufferPool, countsServerStream: false, token);
        var upstreamToLocal = PumpAsync(upstream, localStream, _pumpBufferPool, countsServerStream: true, token);

        var first = await Task.WhenAny(localToUpstream, upstreamToLocal).ConfigureAwait(false);
        var firstResult = await first.ConfigureAwait(false);

        // A pump returns Ended only when its admission was refused, meaning the scope is already sealed
        // and disposal has begun. That is not a relay end: the join below observes the sibling and the
        // relay ends cleanly rather than as a stall.
        if (firstResult != PumpResult.Ended)
        {
            _scope.Cancel();
            // Both remaining first results are non-clean ends, so the client leg is closed abortively
            // before this method's stream scope would close it gracefully.
            AbortLocalSocket();
            if (firstResult == PumpResult.Stalled)
            {
                // Stall fast-exit: the relay is reclaimed without awaiting the sibling, so Completion
                // still completes on a stall instead of waiting for both pumps. The sibling is a
                // tracked child — the scope's drain joins it inside DisposeAsync, and the socket
                // close forces it to return.
                EndKind = RelayEndKind.Stalled;
                return;
            }

            // The faulting pump recorded its own fault before returning it, so surfacing it here is
            // the only observation the orphaned sibling needs: a pump never completes with an
            // exception of its own, so no pump task can ever be an unobserved fault.
            EndKind = RelayEndKind.Faulted;
            throw _scope.Fault ?? new IOException(PumpFaultMessage);
        }

        if (first == localToUpstream) ShutdownSend(upstream);
        else ShutdownSend(_localSocket);

        var results = await Task.WhenAll(localToUpstream, upstreamToLocal).ConfigureAwait(false);
        if (results[0] == PumpResult.Faulted || results[1] == PumpResult.Faulted)
        {
            _scope.Cancel();
            AbortLocalSocket();
            EndKind = RelayEndKind.Faulted;
            throw _scope.Fault ?? new IOException(PumpFaultMessage);
        }

        if (results[0] == PumpResult.Stalled || results[1] == PumpResult.Stalled)
        {
            _scope.Cancel();
            AbortLocalSocket();
            EndKind = RelayEndKind.Stalled;
            return;
        }

        EndKind = RelayEndKind.CleanEnded;
    }

    // One reusable per-operation stall window per pump direction: re-arms a single linked CTS via
    // TryReset + CancelAfter instead of allocating a fresh linked source + timer per chunk, which
    // dominated relay allocations at high throughput. TryReset keeps the lifetime-token link armed,
    // so session-wide and cross-pump cancellation still cancel an in-flight operation at once. The
    // source is recreated only when a previous stall timer raced with operation completion
    // (TryReset returns false). Re-arms are throttled by IsRearmDue, and the window is never
    // disarmed between operations.
    private sealed class StallWindow(CancellationToken lifetime) : IDisposable
    {
        private CancellationTokenSource _source = CreateArmed(lifetime);
        private long _lastArmTicks;

        public CancellationToken Token => _source.Token;

        public void Arm()
        {
            var now = Stopwatch.GetTimestamp();
            if (!IsRearmDue(_lastArmTicks, now)) return;
            _lastArmTicks = now;
            if (_source.TryReset())
            {
                _source.CancelAfter(s_stallTimeout);
                return;
            }
            _source.Dispose();
            _source = CreateArmed(lifetime);
        }

        public void Dispose() => _source.Dispose();

        private static CancellationTokenSource CreateArmed(CancellationToken lifetime)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            source.CancelAfter(s_stallTimeout);
            return source;
        }
    }

    // The first arm is unconditional; a later arm within one second of the last is skipped
    // because the window from the previous arm still covers the operations.
    internal static bool IsRearmDue(long lastArmTicks, long nowTicks)
        => lastArmTicks == 0 || nowTicks - lastArmTicks > s_armThrottleTicks;

    private async Task<PumpResult> PumpAsync(Stream source, Stream destination, NativeBufferPool pumpBufferPool, bool countsServerStream, CancellationToken cancellationToken)
    {
        // A sealed scope means disposal already began and this pump never ran: reporting a clean end
        // rather than a stall keeps an ordinary teardown from looking like a stall timeout, which
        // the acceptor would surface as a client reset.
        if (!_scope.TryEnter(out var workLease)) return PumpResult.Ended;
        // One native 64 KiB window per pump direction: directions have independent lifetimes via
        // half-close, so the lease brackets this whole pump and the pool bounds steady-state memory
        // while an 8 KiB fixed buffer paid ~8x the per-byte syscall/memcpy cost. The lease's Memory
        // view (allocation-free, backed by the per-allocation MemoryManager) feeds the async stream
        // APIs.
        var lease = pumpBufferPool.Rent();
        long serverStreamBytes = 0;
        try
        {
            using var stall = new StallWindow(cancellationToken);
            while (true)
            {
                int read;
                try
                {
                    stall.Arm();
                    read = await source.ReadAsync(lease.Memory, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return PumpResult.Stalled;
                }
                if (read == 0)
                {
                    return PumpResult.Ended;
                }
                try
                {
                    stall.Arm();
                    await destination.WriteAsync(lease.Memory[..read], stall.Token).ConfigureAwait(false);
                    if (countsServerStream) serverStreamBytes += read;
                }
                catch (OperationCanceledException)
                {
                    return PumpResult.Stalled;
                }
            }
        }
        catch (Exception exception)
        {
            RecordPumpFault(exception);
            return PumpResult.Faulted;
        }
        finally
        {
            if (countsServerStream) Volatile.Write(ref _serverStreamBytes, serverStreamBytes);
            lease.Dispose();
            workLease.Dispose();
        }
    }

    // Fault observation is intrinsic to the pump body: the exception is recorded on the scope and
    // reported as a result instead of being thrown, so a pump task can never be left faulted — and
    // therefore never needs an external observer to avoid an unobserved fault. The
    // `tcp.relay.faulted` event is raised here, with the fault.
    private void RecordPumpFault(Exception exception)
    {
        _scope.RecordFault(exception, "tcp.relay.pump");
        var error = exception.GetType().Name;
        TcpRedirectLog.TcpRelayFaulted(_logger, error);
    }

    private static void ShutdownSend(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException) { /* the peer is already gone */ }
        catch (ObjectDisposedException) { /* the socket is already disposed */ }
    }

    private static void ShutdownSend(Stream stream)
    {
        if (stream is not NetworkStream networkStream) return;
        ShutdownSend(networkStream.Socket);
    }

    /// <summary>
    /// Abortively closes the client-facing socket for a non-clean end: it arms linger 0 and closes the
    /// socket here, because the pump scope's stream disposal shuts the socket down gracefully first and
    /// a graceful shutdown turns the close into a FIN, defeating the linger. A no-op when the relay was
    /// not built for it, and when the socket is already gone.
    /// </summary>
    private void AbortLocalSocket()
    {
        if (!_abortiveCloseOnFault) return;
        try
        {
            _localSocket.LingerState = new LingerOption(enable: true, seconds: 0);
            _localSocket.Dispose();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or SocketException)
        {
            // The socket is already closed; the abortive intent needs no retry.
        }
    }

    private enum PumpResult
    {
        Ended,
        Stalled,
        Faulted,
    }

    public async ValueTask DisposeAsync()
    {
        // The scope's single-flight covers only the drain, and the seal happens *inside* DrainAsync,
        // so a precheck on IsSealed would be TOCTOU: two concurrent callers could both run the
        // socket/control teardown. The Interlocked claim restores the one-shot guarantee; every
        // caller — the claimant included — joins the drain below.
        if (Interlocked.Exchange(ref _teardownStarted, 1) != 0)
        {
            await _scope.DrainAsync().ConfigureAwait(false);
            await ObserveCompletionAsync().ConfigureAwait(false);
            return;
        }

        var drained = _scope.DrainAsync();
        _scope.Cancel();
        // A relay that faulted before its pumps could classify (for example a stream-construction
        // throw, where EndKind keeps its fail-visible default) never reached the pump path's abortive
        // close, so this disposal is the close for it. A relay whose pumps are still running, or that
        // ended cleanly, is closed plainly below: this disposal owns that close.
        if (Completion.IsCompleted && EndKind != RelayEndKind.CleanEnded) AbortLocalSocket();
        _localSocket.Dispose();
        try
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // The cancelled token plus the socket close terminate both pumps, so the drain is a real
            // quiescence point and stays bounded — closing the socket forces a pump the stall
            // fast-exit abandoned to return. Awaiting Completion afterwards observes the
            // orchestration task itself, so a fault it carries can never surface as an unobserved
            // task exception.
            await drained.ConfigureAwait(false);
            await ObserveCompletionAsync().ConfigureAwait(false);
        }
    }

    private async Task ObserveCompletionAsync()
    {
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _scope.RecordFault(exception, "tcp.relay.completion");
        }
    }
}
