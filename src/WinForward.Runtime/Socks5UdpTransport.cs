using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime;

public interface IUdpProxyTransport : IAsyncDisposable
{
    IPEndPoint RelayEndpoint { get; }
    IPEndPoint LocalEndpoint { get; }
    ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    ValueTask<Socks5UdpDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

public interface IUdpProxyTransportFactory
{
    ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken);
}

public sealed class Socks5UdpTransportFactory : IUdpProxyTransportFactory
{
    private readonly SelfTrafficRegistry _selfTraffic;

    public Socks5UdpTransportFactory(SelfTrafficRegistry selfTraffic)
    {
        ArgumentNullException.ThrowIfNull(selfTraffic);
        _selfTraffic = selfTraffic;
    }

    public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken) =>
        await Socks5UdpTransport.CreateAsync(server, _selfTraffic, cancellationToken).ConfigureAwait(false);
}

public sealed class Socks5UdpTransport : IUdpProxyTransport
{
    private readonly Socket _socket;
    private readonly Socks5ControlConnection _control;
    private readonly SelfTrafficRegistry.SelfTrafficToken? _selfTrafficToken;

    private Socks5UdpTransport(Socket socket, Socks5ControlConnection control, IPEndPoint relayEndpoint, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken)
    {
        _socket = socket;
        _control = control;
        RelayEndpoint = relayEndpoint;
        _selfTrafficToken = selfTrafficToken;
    }

    public IPEndPoint RelayEndpoint { get; }
    public IPEndPoint LocalEndpoint => (IPEndPoint)_socket.LocalEndPoint!;

    public static async ValueTask<Socks5UdpTransport> CreateAsync(Socks5Server server, SelfTrafficRegistry selfTraffic, CancellationToken cancellationToken)
        => await CreateAsync(server, selfTraffic, cancellationToken, null, null).ConfigureAwait(false);

    internal static async ValueTask<Socks5UdpTransport> CreateAsync(
        Socks5Server server,
        SelfTrafficRegistry selfTraffic,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<Socks5ControlConnection>>? createControl,
        Func<AddressFamily, Socket>? socketFactory)
    {
        Socks5ControlConnection? control = null;
        Socket? socket = null;
        SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken = null;
        try
        {
            var controlFactory = createControl ?? (token =>
                Socks5ControlConnection.ConnectAsync(
                    server,
                    token,
                    (local, remote) => selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                        TransportProtocol.Tcp,
                        Endpoint.From(local.Address, checked((ushort)local.Port)),
                        Endpoint.From(remote.Address, checked((ushort)remote.Port))))));
            control = await controlFactory(cancellationToken).ConfigureAwait(false);
            var relay = await control.UdpAssociateAsync(cancellationToken).ConfigureAwait(false);
            var relayAddressFamily = relay.AddressFamily;
            socket = (socketFactory ?? (family => new Socket(family, SocketType.Dgram, ProtocolType.Udp)))(relayAddressFamily);
            socket.Bind(new IPEndPoint(relayAddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
            // Register the relay transport tuple in the loop-prevention registry so catch-all proxy
            // rules never recursively intercept WinForward's own UDP relay traffic (design §10).
            var local = Endpoint.From(((IPEndPoint)socket.LocalEndPoint!).Address, checked((ushort)((IPEndPoint)socket.LocalEndPoint!).Port));
            var remote = Endpoint.From(relay.Address, checked((ushort)relay.Port));
            selfTrafficToken = selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Udp, local, remote));
            var transport = new Socks5UdpTransport(socket, control, relay, selfTrafficToken);
            socket = null;
            control = null;
            selfTrafficToken = null;
            return transport;
        }
        catch
        {
            try
            {
                selfTrafficToken?.Dispose();
            }
            finally
            {
                try
                {
                    socket?.Dispose();
                }
                finally
                {
                    if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
                }
            }
            throw;
        }
    }

    public async ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // The header buffer covers the worst SOCKS5 UDP overhead (6 + 16-byte IPv6) plus an
        // Ethernet-sized payload; the encode writes into it and the socket send reads only the
        // written slice, so a datagram send allocates nothing.
        if (!Socks5UdpCodec.TryEncode(IPAddressValue.From(destination.Address), (ushort)destination.Port, payload.Span, _sendBuffer, out var written))
        {
            throw new IOException("A SOCKS5 UDP datagram exceeded the relay send buffer.");
        }

        _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, written), SocketFlags.None, RelayEndpoint, cancellationToken).ConfigureAwait(false);
    }

    private readonly byte[] _sendBuffer = new byte[6 + 16 + UdpFrameBuilder.MaximumEthernetFrame];

    public async ValueTask<Socks5UdpDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EndPoint sender = RelayEndpoint.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
        if (!IsAcceptableRelaySource(result.RemoteEndPoint, RelayEndpoint)) throw new IOException("SOCKS5 UDP packet came from an unexpected relay endpoint.");
        if (IsPossiblyTruncated(result.ReceivedBytes, buffer.Length)) throw new IOException("SOCKS5 UDP relay returned an oversized datagram.");
        // M2: the SOCKS5 UDP wire format carries no interface scope, so propagate the relay
        // endpoint's IPv6 scope into reconstruction to keep a link-local decoded address routable.
        var scopeId = RelayEndpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 ? RelayEndpoint.Address.ScopeId : 0;
        if (!Socks5UdpCodec.TryDecode(buffer[..result.ReceivedBytes], out var datagram, scopeId)) throw new IOException("SOCKS5 UDP relay returned a malformed datagram.");
        return datagram;
    }

    /// <summary>
    /// Validates the observed sender of a relay datagram against the negotiated relay endpoint.
    /// RFC 1928 does not pin relay replies to the BND address, so a multi-homed or anycast relay
    /// may answer from another address of the same scope; the port and address family must still
    /// match exactly so clearly unrelated sources stay rejected (R3).
    /// </summary>
    internal static bool IsAcceptableRelaySource(EndPoint observed, IPEndPoint relay) =>
        observed is IPEndPoint ip && ip.Port == relay.Port && ip.AddressFamily == relay.AddressFamily;

    internal static bool IsPossiblyTruncated(int receivedBytes, int bufferLength) => receivedBytes >= bufferLength;

    public async ValueTask DisposeAsync()
    {
        try
        {
            _socket.Dispose();
        }
        finally
        {
            try
            {
                _selfTrafficToken?.Dispose();
            }
            finally
            {
                await _control.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
