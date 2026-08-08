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

        var control = await Socks5ControlConnection.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
        try
        {
            var destinationAddress = originalDestination.Address;
            await control.ConnectDestinationAsync(new IPEndPoint(destinationAddress, originalDestination.Port), cancellationToken).ConfigureAwait(false);

            var upstream = control.GetUpstreamStream();
            var selfTrafficToken = RegisterUpstreamLoopPrevention(server, destinationAddress.AddressFamily);

            return new TcpProxyRelay(concrete.Socket, upstream, control, selfTrafficToken);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SelfTrafficRegistry.SelfTrafficToken RegisterUpstreamLoopPrevention(Socks5Server server, System.Net.Sockets.AddressFamily destinationFamily)
    {
        var proxyAddress = Socks5EndpointResolver.ResolveProxyAddress(server.Host, destinationFamily);
        var proxyEndpoint = Endpoint.From(proxyAddress, server.Port);
        return selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Tcp, proxyEndpoint, proxyEndpoint));
    }
}

internal static class Socks5EndpointResolver
{
    public static IPAddress ResolveProxyAddress(string host, System.Net.Sockets.AddressFamily preferredFamily)
    {
        var addresses = Dns.GetHostAddresses(host);
        foreach (var address in addresses)
        {
            if (address.AddressFamily == preferredFamily) return address;
        }
        return addresses.Length > 0 ? addresses[0] : throw new SocketException((int)SocketError.HostNotFound);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class TcpProxyRelay : ITcpRelay
{
    private const int BufferSize = 8192;

    private readonly Socket _localSocket;
    private readonly Socks5ControlConnection _control;
    private readonly SelfTrafficRegistry.SelfTrafficToken? _selfTrafficToken;
    private readonly Task _completion;
    private int _disposed;

    public TcpProxyRelay(Socket localSocket, Stream upstream, Socks5ControlConnection control, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken)
    {
        _localSocket = localSocket;
        _control = control;
        _selfTrafficToken = selfTrafficToken;
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
        _selfTrafficToken?.Dispose();
        return _control.DisposeAsync();
    }
}
