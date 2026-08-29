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

[SupportedOSPlatform("windows")]
internal sealed class TcpProxyRelay : ITcpRelay
{
    private const int BufferSize = 8192;
    // A relay that makes no progress in one direction for this long is considered stalled and the
    // whole relay is reclaimed (M4). Established connections that are merely idle at the packet
    // level (e.g. SSH with keepalives) keep traffic flowing in both directions (data + ACKs), so
    // this generous stall window only fires for a genuinely dead peer and cannot be held forever
    // by <see cref="TcpProxyRelay"/>. Teardown is otherwise tied to the relay ending, not to a
    // per-flow wall-clock idle timeout.
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(30);

    private readonly Socket _localSocket;
    private readonly IAsyncDisposable _control;
    private readonly IRuntimeLogger _logger;
    private readonly Task _completion;
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
                return;
            }

            if (first == localToUpstream) ShutdownSend(upstream);
            else ShutdownSend(_localSocket);

            var results = await Task.WhenAll(localToUpstream, upstreamToLocal).ConfigureAwait(false);
            if (results[0] == PumpResult.Stalled || results[1] == PumpResult.Stalled)
            {
                await pumpCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            await pumpCancellation.CancelAsync().ConfigureAwait(false);
            ObservePump(localToUpstream.IsCompleted ? upstreamToLocal : localToUpstream);
            throw;
        }
    }

    private static async Task<PumpResult> PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read;
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(StallTimeout);
                read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), readTimeout.Token).ConfigureAwait(false);
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
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeTimeout.CancelAfter(StallTimeout);
                await destination.WriteAsync(buffer.AsMemory(0, read), writeTimeout.Token).ConfigureAwait(false);
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
