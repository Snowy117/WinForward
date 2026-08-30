using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.TcpRedirect;

[SupportedOSPlatform("windows")]
public sealed class TcpProxyRelayFactory(SelfTrafficRegistry selfTraffic, IRuntimeLogger? logger = null) : ITcpProxyRelayFactory
{
    // The redirect leg completes the client's TCP handshake in tens of milliseconds, so the relay's
    // upstream connect budget bounds how long an unreachable/black-holed SOCKS5 server delays the
    // client's reset: worst case DNS + two ten-second attempts instead of the per-attempt 30s
    // defaults (worst case ~150s). Refused/unreachable failures still surface in sub-second time
    // because a rejected connect fails the attempt immediately.
    internal const int RelayConnectMaxAttempts = 2;
    internal static readonly TimeSpan RelayConnectAttemptTimeout = TimeSpan.FromSeconds(10);

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
        // intercepting WinForward's own SOCKS5 control traffic (design §10).
        var control = await Socks5ControlConnection.ConnectAsync(server, cancellationToken, (local, remote) =>
            selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Tcp,
                Endpoint.From(local.Address, checked((ushort)local.Port)),
                Endpoint.From(remote.Address, checked((ushort)remote.Port)))),
            maxAttempts: RelayConnectMaxAttempts,
            perAttemptTimeout: RelayConnectAttemptTimeout).ConfigureAwait(false);
        try
        {
            var destinationAddress = originalDestination.Address;
            await control.ConnectDestinationAsync(new IPEndPoint(destinationAddress.ToIPAddress(), originalDestination.Port), cancellationToken).ConfigureAwait(false);

            var upstream = control.GetUpstreamStream();
            return new TcpProxyRelay(concrete.Socket, upstream, control, logger);
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
}

[SupportedOSPlatform("windows")]
internal sealed class TcpProxyRelay : ITcpRelay, ITcpRelayEndInfo
{
    private const int PumpBufferSize = 64 * 1024;
    // A relay that makes no progress in one direction for this long is considered stalled and the
    // whole relay is reclaimed (M4). Established connections that are merely idle at the packet
    // level (e.g. SSH with keepalives) keep traffic flowing in both directions (data + ACKs), so
    // this generous stall window only fires for a genuinely dead peer and cannot be held forever
    // by <see cref="TcpProxyRelay"/>. Teardown is otherwise tied to the relay ending, not to a
    // per-flow wall-clock idle timeout.
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(30);
    // One re-arm per second is enough for a 30-minute window (X8a): the window drifts by at most
    // one second, while skipping the per-chunk TryReset + CancelAfter timer-queue updates saves
    // ~100-200 ns per operation at 10 Gbps single-flow chunk rates.
    internal static readonly long ArmThrottleTicks = Stopwatch.Frequency;

    private readonly Socket _localSocket;
    private readonly IAsyncDisposable _control;
    private readonly IRuntimeLogger _logger;
    private readonly Task _completion;
    // Defaults to the fail-visible kind: a relay whose pumps never started (a construction-time
    // throw before the run body) completes faulted without ever classifying itself, and that end
    // must still surface as a client reset. CleanEnded is only ever assigned explicitly, after
    // both pumps verifiably completed.
    private RelayEndKind _endKind = RelayEndKind.Faulted;
    private int _disposed;

    public TcpProxyRelay(Socket localSocket, Stream upstream, IAsyncDisposable control, IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(localSocket);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(control);
        _localSocket = localSocket;
        _control = control;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _completion = RunPumpAsync(upstream);
    }

    public Task Completion => _completion;

    public RelayEndKind EndKind => _endKind;

    private async Task RunPumpAsync(Stream upstream)
    {
        using var localStream = new NetworkStream(_localSocket, ownsSocket: true);
        using var pumpCancellation = new CancellationTokenSource();
        var localToUpstream = PumpAsync(localStream, upstream, pumpCancellation.Token);
        var upstreamToLocal = PumpAsync(upstream, localStream, pumpCancellation.Token);

        try
        {
            var first = await Task.WhenAny(localToUpstream, upstreamToLocal).ConfigureAwait(false);
            var firstResult = await first.ConfigureAwait(false);
            if (firstResult == PumpResult.Stalled)
            {
                await pumpCancellation.CancelAsync().ConfigureAwait(false);
                ObservePump(first == localToUpstream ? upstreamToLocal : localToUpstream);
                _endKind = RelayEndKind.Stalled;
                return;
            }

            if (first == localToUpstream) ShutdownSend(upstream);
            else ShutdownSend(_localSocket);

            var results = await Task.WhenAll(localToUpstream, upstreamToLocal).ConfigureAwait(false);
            if (results[0] == PumpResult.Stalled || results[1] == PumpResult.Stalled)
            {
                await pumpCancellation.CancelAsync().ConfigureAwait(false);
                _endKind = RelayEndKind.Stalled;
            }
            else
            {
                _endKind = RelayEndKind.CleanEnded;
            }
        }
        catch
        {
            await pumpCancellation.CancelAsync().ConfigureAwait(false);
            ObservePump(localToUpstream.IsCompleted ? upstreamToLocal : localToUpstream);
            _endKind = RelayEndKind.Faulted;
            throw;
        }
    }

    // One reusable per-operation stall window per pump direction (P1): re-arms a single linked
    // CTS via TryReset + CancelAfter instead of allocating a fresh linked source + timer per
    // chunk, which dominated relay allocations at high throughput (measured 160 B/chunk).
    // TryReset keeps the lifetime-token link armed, so session-wide and cross-pump cancellation
    // still cancel an in-flight operation immediately; the source is recreated only when a
    // previous stall timer raced with operation completion (TryReset returns false). Re-arms are
    // throttled to one per second (X8a); the window is never disarmed between operations.
    private sealed class StallWindow : IDisposable
    {
        private readonly CancellationToken _lifetime;
        private CancellationTokenSource _source;
        private long _lastArmTicks;

        public StallWindow(CancellationToken lifetime)
        {
            _lifetime = lifetime;
            _source = CreateArmed(lifetime);
        }

        public CancellationToken Token => _source.Token;

        public void Arm()
        {
            var now = Stopwatch.GetTimestamp();
            if (!IsRearmDue(_lastArmTicks, now)) return;
            _lastArmTicks = now;
            if (_source.TryReset())
            {
                _source.CancelAfter(StallTimeout);
                return;
            }
            _source.Dispose();
            _source = CreateArmed(_lifetime);
        }

        public void Dispose() => _source.Dispose();

        private static CancellationTokenSource CreateArmed(CancellationToken lifetime)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            source.CancelAfter(StallTimeout);
            return source;
        }
    }

    // The first arm is unconditional; a later arm within one second of the last is skipped
    // because the window from the previous arm still covers the operations.
    internal static bool IsRearmDue(long lastArmTicks, long nowTicks)
        => lastArmTicks == 0 || nowTicks - lastArmTicks > ArmThrottleTicks;

    private static async Task<PumpResult> PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        // One pooled 64 KiB buffer per pump direction (X5): directions have independent
        // lifetimes via half-close, so the rent brackets this whole pump and the pool bounds
        // steady-state memory while an 8 KiB fixed buffer paid ~8x the per-byte
        // syscall/memcpy cost.
        var buffer = ArrayPool<byte>.Shared.Rent(PumpBufferSize);
        try
        {
            using var stall = new StallWindow(cancellationToken);
            while (true)
            {
                int read;
                try
                {
                    stall.Arm();
                    read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return PumpResult.Stalled;
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
                    await destination.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return PumpResult.Stalled;
                }
                catch (OperationCanceledException)
                {
                    return PumpResult.Stalled;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool ShutdownSend(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    private static void ShutdownSend(Stream stream)
    {
        if (stream is not NetworkStream networkStream) return;
        _ = ShutdownSend(networkStream.Socket);
    }

    private static void ObservePump(Task<PumpResult> first)
    {
        _ = first.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private enum PumpResult
    {
        Ended,
        Stalled,
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        // The dispose path discards the relay without ever awaiting its completion — and the
        // disposal itself faults an in-flight pump read — so the fault observer must be hooked
        // before the sockets go away (S3).
        TcpRelayFaultObserver.Observe(this, _logger);
        _localSocket.Dispose();
        return _control.DisposeAsync();
    }
}
