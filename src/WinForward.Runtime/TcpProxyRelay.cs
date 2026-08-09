using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime;

[SupportedOSPlatform("windows")]
public sealed class TcpProxyRelayFactory(SelfTrafficRegistry selfTraffic) : ITcpProxyRelayFactory
{
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
                Endpoint.From(remote.Address, checked((ushort)remote.Port))))).ConfigureAwait(false);
        try
        {
            var destinationAddress = originalDestination.Address;
            await control.ConnectDestinationAsync(new IPEndPoint(destinationAddress, originalDestination.Port), cancellationToken).ConfigureAwait(false);

            var upstream = control.GetUpstreamStream();
            return new TcpProxyRelay(concrete.Socket, upstream, control);
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

    private readonly Socket _localSocket;
    private readonly Socks5ControlConnection _control;
    private readonly Task _completion;
    private int _disposed;

    public TcpProxyRelay(Socket localSocket, Stream upstream, Socks5ControlConnection control)
    {
        _localSocket = localSocket;
        _control = control;
        _completion = RunPumpAsync(upstream);
    }

    public Task Completion => _completion;

    private async Task RunPumpAsync(Stream upstream)
    {
        using var localStream = new NetworkStream(_localSocket, ownsSocket: true);
        var localToUpstream = PumpAsync(localStream, upstream);
        var upstreamToLocal = PumpAsync(upstream, localStream);
        await Task.WhenAll(localToUpstream, upstreamToLocal).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream source, Stream destination)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), CancellationToken.None).ConfigureAwait(false);
            if (read == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _localSocket.Dispose();
        return _control.DisposeAsync();
    }
}
