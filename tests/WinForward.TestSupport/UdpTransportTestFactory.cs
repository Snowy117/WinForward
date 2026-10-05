using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;

namespace WinForward.TestSupport;

/// <summary>
/// Builds a real per-flow transport the way production composition does: the factory dials the
/// flow's own association (control connection + <c>UDP ASSOCIATE</c>), the transport binds its
/// relay socket in that association's relay family, and the transport owns both — so disposing it
/// closes the control connection and the relay socket together.
/// </summary>
internal static class UdpTransportTestFactory
{
    internal static async ValueTask<Socks5UdpTransport> CreateAsync(
        Socks5Server server,
        SelfTrafficRegistry registry,
        Func<AddressFamily, Socket>? socketFactory = null,
        Action<Socket>? disableUdpConnectionReset = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        int relayReceiveBufferBytes = Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize,
        Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null,
        Socks5AddressCache? addressCache = null,
        IRuntimeLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var factory = new Socks5UdpTransportFactory(
            registry,
            maximumFrameSize,
            relayReceiveBufferBytes,
            addressCache,
            logger,
            createControl,
            socketFactory,
            disableUdpConnectionReset);
        return (Socks5UdpTransport)await factory.CreateAsync(ProxyTarget.FromServer(server), cancellationToken).ConfigureAwait(false);
    }
}
