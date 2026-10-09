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

    public async ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptedConnection);
        ArgumentNullException.ThrowIfNull(server);

        if (acceptedConnection is not TcpAcceptedConnection concrete)
        {
            throw new ArgumentException("The accepted connection must be a TcpAcceptedConnection.", nameof(acceptedConnection));
        }

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
            return new TcpProxyRelay(concrete.Socket, upstream, control, logger, pumpBufferPool);
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
/// <see cref="ITcpRelay.Completion"/> completes; drives the acceptor's client-reset decision.
/// </summary>
internal enum RelayEndKind
{
    CleanEnded,
    Stalled,
    Faulted,
}

/// <summary>
/// The relay surface that reports <see cref="EndKind"/> to the acceptor. A separate internal
/// capability because <see cref="ITcpRelay"/> is public while the end kind is not; a relay that
/// does not implement it is treated as a clean end (no client reset).
/// </summary>
internal interface ITcpRelayEndInfo
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
    // Owns the pumps' lifetime token and joins the pumps when the relay is disposed. It is sealed
    // and drained only after the local socket is closed, which is what forces a pump the stall
    // fast-exit abandoned to return.
    private readonly QuiescenceScope _scope = new();
    private int _teardownStarted;
    private long _serverStreamBytes;

    public TcpProxyRelay(Socket localSocket, Stream upstream, IAsyncDisposable control, ILogger? logger = null, NativeBufferPool? pumpBufferPool = null)
    {
        ArgumentNullException.ThrowIfNull(localSocket);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(control);
        _localSocket = localSocket;
        _control = control;
        _logger = logger ?? NullLogger.Instance;
        _pumpBufferPool = pumpBufferPool ?? s_sharedPumpBufferPool;
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
            if (firstResult == PumpResult.Stalled)
            {
                // Stall fast-exit: the relay is reclaimed without awaiting the sibling, so Completion
                // still completes on a stall instead of waiting for both pumps. The sibling is a
                // tracked child — the scope's drain joins it inside DisposeAsync, and the socket close
                // there makes it return.
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
            EndKind = RelayEndKind.Faulted;
            throw _scope.Fault ?? new IOException(PumpFaultMessage);
        }

        if (results[0] == PumpResult.Stalled || results[1] == PumpResult.Stalled)
        {
            _scope.Cancel();
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
