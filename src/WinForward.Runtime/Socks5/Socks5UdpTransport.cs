using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime.Socks5;

/// <summary>
/// The per-datagram anomaly that made a relay receive undeliverable. Skip reasons are surfaced
/// as results instead of exceptions so a single bad relay datagram never terminates a session's
/// receive loop; only socket-level failures keep throwing.
/// </summary>
public enum Socks5UdpReceiveSkipReason
{
    /// <summary>Not a skip: the datagram was decoded successfully.</summary>
    None = 0,

    /// <summary>The datagram arrived from an endpoint other than the negotiated relay (port or address-family mismatch).</summary>
    UnexpectedSource = 1,

    /// <summary>The datagram filled the receive buffer completely and may be truncated.</summary>
    Oversized = 2,

    /// <summary>The datagram is not a decodable SOCKS5 UDP datagram.</summary>
    Malformed = 3,

    /// <summary>
    /// The receive call itself faulted with <see cref="SocketError.ConnectionReset"/>: on Windows an
    /// ICMP port-unreachable answering one of this socket's sends surfaces this way. Skip-class like
    /// the datagram anomalies: the session keeps receiving (S2).
    /// </summary>
    ConnectionReset = 4,
}

/// <summary>
/// The discriminated result of <see cref="IUdpProxyTransport.ReceiveAsync"/>: either a decoded
/// datagram (<see cref="HasDatagram"/>) or a <see cref="SkipReason"/> that skips exactly one
/// datagram. A struct result keeps the receive hot path allocation-free.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct Socks5UdpReceiveResult(Socks5UdpDatagram Datagram, Socks5UdpReceiveSkipReason SkipReason)
{
    /// <summary>True when <see cref="Datagram"/> carries a decoded relay datagram.</summary>
    public bool HasDatagram => SkipReason == Socks5UdpReceiveSkipReason.None;

    /// <summary>Wraps a successfully decoded relay datagram.</summary>
    public static Socks5UdpReceiveResult Received(Socks5UdpDatagram datagram) => new(datagram, Socks5UdpReceiveSkipReason.None);

    /// <summary>Marks one per-datagram anomaly; the caller must skip the datagram and keep receiving.</summary>
    public static Socks5UdpReceiveResult Skipped(Socks5UdpReceiveSkipReason reason) => new(default, reason);
}

public interface IUdpProxyTransport : IAsyncDisposable
{
    IPEndPoint RelayEndpoint { get; }
    IPEndPoint LocalEndpoint { get; }
    ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
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
    /// <summary>
    /// Explicit relay-socket receive headroom (R4): relayed responses can burst far faster than
    /// the single receive loop reinjects them, and the OS default datagram buffer would overflow
    /// and drop responses that were already relayed. Not a config knob; the schema is frozen.
    /// </summary>
    private const int RelaySocketReceiveBufferSize = 512 * 1024;

    /// <summary>
    /// SIO_UDP_CONNRESET (vendor IOCTL 0x9800000C). While TRUE (the Windows default for UDP
    /// sockets), an ICMP port-unreachable answering one of this socket's sends is surfaced as
    /// <see cref="SocketError.ConnectionReset"/> on the next receive, which would terminate the
    /// relay session's receive loop (S2).
    /// </summary>
    private const int SIOUdpConnreset = unchecked((int)0x9800000C);

    /// <summary>A 4-byte Win32 BOOL FALSE, the SIO_UDP_CONNRESET input value.</summary>
    private static readonly byte[] DisableValue = { 0, 0, 0, 0 };

    /// <summary>Cached default for <c>disableUdpConnectionReset</c> so session setup never converts the method group per call.</summary>
    private static readonly Action<Socket> DisableUdpConnectionResetAction = DisableUdpConnectionReset;

    private readonly Socket _socket;
    private readonly Socks5ControlConnection _control;
    private readonly SelfTrafficRegistry.SelfTrafficToken? _selfTrafficToken;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

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
        Func<AddressFamily, Socket>? socketFactory,
        Action<Socket>? disableUdpConnectionReset = null)
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
            socket.ReceiveBufferSize = RelaySocketReceiveBufferSize;
            // Applied before bind per the IOCTL's contract (S2): an ICMP-driven reset must never
            // reach the receive loop. Injectable so tests can assert the call without a Windows socket.
            (disableUdpConnectionReset ?? DisableUdpConnectionResetAction)(socket);
            socket.Bind(new IPEndPoint(relayAddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
            // Non-blocking mode keeps the send warm path synchronous: the kernel either takes
            // the datagram inline or reports WouldBlock, which falls back to the overlapped
            // send. Async receive operations are unaffected by the non-blocking mode.
            socket.Blocking = false;
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

    public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // Serialize the encode + send pair (R5): the shared send buffer must never observe
        // interleaved writers when one flow is dispatched from two pumps. This is a
        // non-async entry (hot-path convention #3): the warm shape — uncontended gate
        // (WaitAsync completes synchronously), encode into the reusable buffer, non-blocking
        // send the kernel accepts inline — runs without a state machine or any allocation,
        // removing the per-datagram IOCP hop; every other shape falls back to async slow
        // paths that honor the cancellation token.
        var gateWait = _sendGate.WaitAsync(cancellationToken);
        if (!gateWait.IsCompletedSuccessfully)
        {
            return SendAfterGateAsync(gateWait, destination, payload, cancellationToken);
        }

        try
        {
            // The header buffer covers the worst SOCKS5 UDP overhead (6 + 16-byte IPv6) plus an
            // Ethernet-sized payload; the encode writes into it and the socket send reads only the
            // written slice, so a datagram send allocates nothing.
            if (!Socks5UdpCodec.TryEncode(IPAddressValue.From(destination.Address), (ushort)destination.Port, payload.Span, _sendBuffer, out var written))
            {
                throw new IOException("A SOCKS5 UDP datagram exceeded the relay send buffer.");
            }

            try
            {
                _ = _socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, RelayEndpoint);
            }
            catch (SocketException)
            {
                // WouldBlock (kernel send queue momentarily full) or any other socket fault:
                // retry the datagram through the overlapped send, which parks until the socket
                // accepts it, and keep the gate until the buffer is consumed.
                return SendOverlappedAsync(written, cancellationToken);
            }

            _sendGate.Release();
            return ValueTask.CompletedTask;
        }
        catch
        {
            _sendGate.Release();
            throw;
        }
    }

    private async ValueTask SendAfterGateAsync(Task gateWait, IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await gateWait.ConfigureAwait(false);
        try
        {
            if (!Socks5UdpCodec.TryEncode(IPAddressValue.From(destination.Address), (ushort)destination.Port, payload.Span, _sendBuffer, out var written))
            {
                throw new IOException("A SOCKS5 UDP datagram exceeded the relay send buffer.");
            }

            _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, written), SocketFlags.None, RelayEndpoint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async ValueTask SendOverlappedAsync(int written, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, written), SocketFlags.None, RelayEndpoint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private readonly byte[] _sendBuffer = new byte[6 + 16 + UdpFrameBuilder.MaximumEthernetFrame];

    public async ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EndPoint sender = RelayEndpoint.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        SocketReceiveFromResult result;
        try
        {
            result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fault) when (ClassifyReceiveFault(fault) is { } skipReason)
        {
            return Socks5UdpReceiveResult.Skipped(skipReason);
        }
        // Per-datagram anomalies skip one datagram instead of throwing: a single bad relay
        // datagram must not terminate the session's receive loop (R2).
        if (!IsAcceptableRelaySource(result.RemoteEndPoint, RelayEndpoint)) return Socks5UdpReceiveResult.Skipped(Socks5UdpReceiveSkipReason.UnexpectedSource);
        if (IsPossiblyTruncated(result.ReceivedBytes, buffer.Length)) return Socks5UdpReceiveResult.Skipped(Socks5UdpReceiveSkipReason.Oversized);
        // M2: the SOCKS5 UDP wire format carries no interface scope, so propagate the relay
        // endpoint's IPv6 scope into reconstruction to keep a link-local decoded address routable.
        var scopeId = RelayEndpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 ? RelayEndpoint.Address.ScopeId : 0;
        if (!Socks5UdpCodec.TryDecode(buffer[..result.ReceivedBytes], out var datagram, scopeId)) return Socks5UdpReceiveResult.Skipped(Socks5UdpReceiveSkipReason.Malformed);
        return Socks5UdpReceiveResult.Received(datagram);
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

    /// <summary>
    /// Disables SIO_UDP_CONNRESET on a relay socket so an ICMP port-unreachable answering one of
    /// its sends is not surfaced as <see cref="SocketError.ConnectionReset"/> on the next receive
    /// (S2). Windows-only at runtime: the Linux test host rejects vendor IOCTLs, and tests assert
    /// the call through the injectable seam instead of executing it.
    /// </summary>
    internal static void DisableUdpConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        socket.IOControl(SIOUdpConnreset, DisableValue, null);
    }

    /// <summary>
    /// Maps a receive-path fault to its skip reason, or null when the fault is socket-level fatal
    /// and must keep tearing the session down. Only the ICMP-driven reset is skip-class (S2).
    /// </summary>
    internal static Socks5UdpReceiveSkipReason? ClassifyReceiveFault(Exception exception) =>
        exception is SocketException { SocketErrorCode: SocketError.ConnectionReset }
            ? Socks5UdpReceiveSkipReason.ConnectionReset
            : null;

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
                try
                {
                    await _control.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    // Disposed last: in-flight senders release the gate from their finally blocks
                    // as the disposed socket faults their pending sends.
                    _sendGate.Dispose();
                }
            }
        }
    }
}
