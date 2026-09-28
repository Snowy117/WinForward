using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.UdpProxy;

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
    internal static Socks5UdpReceiveResult Received(Socks5UdpDatagram datagram) => new(datagram, Socks5UdpReceiveSkipReason.None);

    /// <summary>Marks one per-datagram anomaly; the caller must skip the datagram and keep receiving.</summary>
    internal static Socks5UdpReceiveResult Skipped(Socks5UdpReceiveSkipReason reason) => new(default, reason);
}

/// <summary>
/// The flow's authenticated SOCKS5 UDP association is gone and could not be recovered in place
/// (association death with a failed or address-family-changing re-association). The transport
/// refuses further datagrams with this exception before touching the relay socket, and the
/// coordinator removes the flow's slot with <c>UdpTeardownReason.AssociationLost</c> without arming
/// the setup cooldown, so the flow re-establishes on its next datagram.
/// </summary>
#pragma warning disable RCS1194 // The [SerializationInfo, StreamingContext] constructor is deliberately omitted: binary serialization is obsolete in .NET 8+ (SYSLIB0051) and this exception carries no state beyond its message and inner exception.
public sealed class UdpAssociationLostException : IOException
{
    // ReSharper disable once UnusedMember.Global // Conventional exception surface: RCS1194 requires the parameterless and message-only constructors, even though in-tree callers use only the (message, inner) overload.
    public UdpAssociationLostException() { }

    // ReSharper disable once UnusedMember.Global // Conventional exception surface: RCS1194 requires the parameterless and message-only constructors, even though in-tree callers use only the (message, inner) overload.
    public UdpAssociationLostException(string message) : base(message) { }

    public UdpAssociationLostException(string message, Exception? innerException) : base(message, innerException) { }
}
#pragma warning restore RCS1194

public interface IUdpProxyTransport : IAsyncDisposable
{
    IPEndPoint RelayEndpoint { get; }
    IPEndPoint LocalEndpoint { get; }

    /// <summary>
    /// Sends one datagram: the destination is encoded as the SOCKS5 UDP header target and the
    /// datagram is sent to the negotiated relay endpoint. The payload is consumed synchronously
    /// (encode into the reusable send buffer, then a non-blocking kernel send) before any
    /// asynchronous socket operation, so a native capture buffer whose span backs it is free to
    /// recycle once this call returns. Only the contended-gate slow shape cannot keep the span
    /// across its await and copies it (cold path).
    /// </summary>
    ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken);
    ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

public interface IUdpProxyTransportFactory
{
    ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken);
}

/// <summary>
/// Rents one association lease per flow from the pool and wraps it in a relay transport. The
/// factory owns no control connection: the pool keeps the authenticated association warm across
/// flows, and the transport owns only its relay socket, its self-traffic tuple, and the lease.
/// </summary>
public sealed class Socks5UdpTransportFactory : IUdpProxyTransportFactory
{
    private readonly UdpAssociationPool _pool;
    private readonly SelfTrafficRegistry _selfTraffic;
    private readonly int _maximumFrameSize;
    private readonly int _relayReceiveBufferBytes;
    private readonly Func<AddressFamily, Socket>? _socketFactory;
    private readonly Action<Socket>? _disableUdpConnectionReset;

    /// <summary>
    /// Creates transports whose send buffer follows the pinned frame cap (6 + 16 + cap) — the
    /// same single source of truth the coordinator's receive windows (cap + 22 + 1) and the
    /// reinjector's rebuilt frames (cap) already use — and whose association comes from the
    /// per-server pool. Composition passes the native ABI constant explicitly, and the per-session
    /// relay receive buffer from the validated <c>udpRelayReceiveBufferKb</c> budget.
    /// </summary>
    /// <param name="pool">The per-server association pool every flow's lease is rented from.</param>
    /// <param name="selfTraffic">Loop-prevention registry the relay tuple is registered in before the first datagram.</param>
    /// <param name="maximumFrameSize">The pinned capture frame cap the send buffer follows (6 + 16 + cap).</param>
    /// <param name="relayReceiveBufferBytes">The per-session relay socket receive buffer from the validated <c>udpRelayReceiveBufferKb</c> budget.</param>
    /// <param name="socketFactory">Test seam: the relay socket constructor, so a construction failure can be driven through the production lease-release path.</param>
    /// <param name="disableUdpConnectionReset">Test seam: the SIO_UDP_CONNRESET posture applied before bind (asserted without a Windows host).</param>
    internal Socks5UdpTransportFactory(
        UdpAssociationPool pool,
        SelfTrafficRegistry selfTraffic,
        int maximumFrameSize,
        int relayReceiveBufferBytes = Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize,
        Func<AddressFamily, Socket>? socketFactory = null,
        Action<Socket>? disableUdpConnectionReset = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relayReceiveBufferBytes);
        _pool = pool;
        _selfTraffic = selfTraffic;
        _maximumFrameSize = maximumFrameSize;
        _relayReceiveBufferBytes = relayReceiveBufferBytes;
        _socketFactory = socketFactory;
        _disableUdpConnectionReset = disableUdpConnectionReset;
    }

    public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        var lease = await _pool.RentAsync(server, cancellationToken).ConfigureAwait(false);
        try
        {
            return Socks5UdpTransport.Create(lease, _selfTraffic, _socketFactory, _disableUdpConnectionReset, _maximumFrameSize, _relayReceiveBufferBytes);
        }
        catch
        {
            // A transport that never came into existence must not hold its association lease.
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class Socks5UdpTransport : IUdpProxyTransport
{
    /// <summary>
    /// The relay socket's default receive buffer, wired from the validated
    /// <c>udpRelayReceiveBufferKb</c> configuration key (default 128 KiB, range 16..1024 KiB).
    /// Relay responses can burst far faster than the single receive loop reinjects them, so the
    /// OS default datagram buffer would overflow and drop responses that were already relayed;
    /// the historical 512 KiB absorbed that. It is bounded now because this buffer is per
    /// session, so the aggregate kernel memory is per-session bytes x concurrent sessions — with
    /// relay sockets retained past their last datagram, a large constant multiplied by flow churn
    /// instead of following the active flow set. Config validation warns when the per-session
    /// value times a large session budget exceeds ~512 MiB.
    /// </summary>
    public const int DefaultRelaySocketReceiveBufferSize = 128 * 1024;

    /// <summary>
    /// SIO_UDP_CONNRESET (vendor IOCTL 0x9800000C). While TRUE (the Windows default for UDP
    /// sockets), an ICMP port-unreachable answering one of this socket's sends is surfaced as
    /// <see cref="SocketError.ConnectionReset"/> on the next receive, which would terminate the
    /// relay session's receive loop (S2).
    /// </summary>
    private const int SIOUdpConnreset = unchecked((int)0x9800000C);

    /// <summary>A 4-byte Win32 BOOL FALSE, the SIO_UDP_CONNRESET input value.</summary>
    private static readonly byte[] s_disableValue = new byte[4];

    /// <summary>Cached default for <c>disableUdpConnectionReset</c> so session setup never converts the method group per call.</summary>
    private static readonly Action<Socket> s_disableUdpConnectionResetAction = DisableUdpConnectionReset;

    private readonly Socket _socket;
    private readonly UdpAssociationLease _lease;
    private readonly SelfTrafficRegistry _selfTraffic;
    private readonly Endpoint _localRelayEndpoint;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _sendBuffer;
    private readonly IPEndPoint _receiveSenderTemplate;

    /// <summary>The current relay publication; replaced only by an in-place re-association (cold path).</summary>
    private UdpRelayTarget _relayTarget;

    /// <summary>The relay's loop-prevention registration; replaced with the destination on re-association.</summary>
    private SelfTrafficRegistry.SelfTrafficToken? _selfTrafficToken;

    private int _disposed;

    private Socks5UdpTransport(UdpAssociationLease lease, SelfTrafficRegistry selfTraffic, Socket socket, Endpoint localRelayEndpoint, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken, int maximumFrameSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameSize);
        _lease = lease;
        _selfTraffic = selfTraffic;
        _socket = socket;
        _localRelayEndpoint = localRelayEndpoint;
        _relayTarget = lease.RelayTarget;
        _selfTrafficToken = selfTrafficToken;
        // Sized from the same pinned frame cap the coordinator's receive windows (cap + 22 + 1)
        // and the reinjector's rebuilt frames (cap) use: 6 + 16 covers the worst SOCKS5 UDP
        // header (IPv6), and a captured payload can never exceed cap - 42 (Ethernet + IPv4 +
        // UDP), so the buffer always fits anything the pipeline can capture — including on a
        // jumbo-capable ABI fed a larger cap.
        _sendBuffer = new byte[6 + 16 + maximumFrameSize];
        // The receive-sender family is fixed by the relay endpoint, so one template per
        // transport replaces the per-receive endpoint allocation; ReceiveFromAsync reports the
        // actual remote in its result and never mutates the template.
        _receiveSenderTemplate = lease.RelayAddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(IPAddress.Any, 0)
            : new IPEndPoint(IPAddress.IPv6Any, 0);
    }

    /// <summary>
    /// The relay endpoint the flow currently sends to: read through the lease, so an in-place
    /// re-association of the shared association is visible without touching this transport.
    /// </summary>
    public IPEndPoint RelayEndpoint => _lease.RelayEndpoint;

    public IPEndPoint LocalEndpoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>
    /// The relay socket's applied receive buffer (SO_RCVBUF, as the OS settled it). Internal test
    /// seam: it proves the configured <c>udpRelayReceiveBufferKb</c> budget reaches a real socket
    /// through the production <see cref="Socks5UdpTransportFactory"/>, which the direct-construction
    /// test alone cannot show.
    /// </summary>
    internal int AppliedRelayReceiveBufferSize => _socket.ReceiveBufferSize;

    /// <summary>
    /// Builds the per-flow transport over a borrowed association lease: the relay socket is bound
    /// in the association's family, the configured receive buffer and SIO_UDP_CONNRESET posture are
    /// applied before bind, and the relay tuple is registered for loop prevention before the first
    /// datagram. The caller owns the lease and releases it if this throws.
    /// </summary>
    internal static Socks5UdpTransport Create(
        UdpAssociationLease lease,
        SelfTrafficRegistry selfTraffic,
        Func<AddressFamily, Socket>? socketFactory = null,
        Action<Socket>? disableUdpConnectionReset = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        int relayReceiveBufferBytes = DefaultRelaySocketReceiveBufferSize)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relayReceiveBufferBytes);
        Socket? socket = null;
        SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken = null;
        try
        {
            var relayAddressFamily = lease.RelayAddressFamily;
            socket = (socketFactory ?? (family => new Socket(family, SocketType.Dgram, ProtocolType.Udp)))(relayAddressFamily);
            socket.ReceiveBufferSize = relayReceiveBufferBytes;
            // Applied before bind per the IOCTL's contract (S2): an ICMP-driven reset must never
            // reach the receive loop. Injectable so tests can assert the call without a Windows socket.
            (disableUdpConnectionReset ?? s_disableUdpConnectionResetAction)(socket);
            socket.Bind(new IPEndPoint(relayAddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
            // Non-blocking mode keeps the send warm path synchronous: the kernel either takes
            // the datagram inline or reports WouldBlock, which falls back to the overlapped
            // send. Async receive operations are unaffected by the non-blocking mode.
            socket.Blocking = false;
            var local = (IPEndPoint)socket.LocalEndPoint!;
            var localRelayEndpoint = Endpoint.From(local.Address, checked((ushort)local.Port));
            var relay = lease.RelayEndpoint;
            // Register the relay transport tuple in the loop-prevention registry so catch-all proxy
            // rules never recursively intercept WinForward's own UDP relay traffic (design §10).
            selfTrafficToken = selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Udp,
                localRelayEndpoint,
                Endpoint.From(relay.Address, checked((ushort)relay.Port))));
            var transport = new Socks5UdpTransport(lease, selfTraffic, socket, localRelayEndpoint, selfTrafficToken, maximumFrameSize);
            socket = null;
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
                socket?.Dispose();
            }
            throw;
        }
    }

#pragma warning disable RCS1229 // Deliberate non-async warm entry (hot-path.md #3): the steady-state send path must not pay an async state machine; a synchronous failure before the returned ValueTask is part of the warm contract and handled by the dispatcher.
    public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
#pragma warning restore RCS1229
    {
        // Non-async warm entry (hot-path convention #3); only the contended-gate shape differs,
        // because a span over native capture memory must not cross the gate await — it is copied
        // there and rides the async slow path.
        // The gate is disposed last, so a sender that has not yet entered it must be refused here;
        // otherwise `_sendGate.WaitAsync` would observe the disposed gate. A single volatile read
        // keeps the warm shape allocation-free.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // I4 fail-closed: an association that died without recovering refuses the datagram before
        // the gate and before the socket, so the coordinator tears the flow down with the
        // association-lost reason instead of writing to a relay endpoint that no longer exists.
        // One volatile field read plus a reference read; the exception itself is cold.
        if (_lease.IsFaulted) throw new UdpAssociationLostException("The flow's SOCKS5 UDP association was lost; the flow must be re-established.", _lease.Fault);
        var gateWait = _sendGate.WaitAsync(cancellationToken);
        if (!gateWait.IsCompletedSuccessfully)
        {
            // Documented cold-path exemption (task 09-18 M2): this copy allocates, but only on the
            // contended-gate branch. The span cannot cross the gate await (it views native capture
            // memory that recycles once the dispatch returns), so the datagram is materialized and
            // rides the memory slow path; the warm uncontended shape below stays zero-alloc.
            return SendAfterGateAsync(gateWait, destination, payload.ToArray(), cancellationToken);
        }

        try
        {
            if (!Socks5UdpCodec.TryEncode(destination.Address, destination.Port, payload, _sendBuffer, out var written))
            {
                throw new IOException("A SOCKS5 UDP datagram exceeded the relay send buffer.");
            }

            try
            {
                _ = _socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, CurrentRelaySocketAddress());
            }
            catch (SocketException)
            {
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

    private async ValueTask SendAfterGateAsync(Task gateWait, Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await gateWait.ConfigureAwait(false);
        try
        {
            if (!Socks5UdpCodec.TryEncode(destination.Address, destination.Port, payload.Span, _sendBuffer, out var written))
            {
                throw new IOException("A SOCKS5 UDP datagram exceeded the relay send buffer.");
            }

            _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, written), SocketFlags.None, CurrentRelaySocketAddress(), cancellationToken).ConfigureAwait(false);
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
            _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, written), SocketFlags.None, CurrentRelaySocketAddress(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// The relay destination for the next send. The comparison is a reference check against the
    /// immutable publication the association replaces once per re-association, so the warm path
    /// stays allocation-free and its rebind reads the endpoint of the very publication it adopted;
    /// the rebind itself runs inside the send gate, which serializes it, so concurrent senders
    /// cannot race two registrations.
    /// </summary>
    private SocketAddress CurrentRelaySocketAddress()
    {
        var current = _lease.RelayTarget;
        return ReferenceEquals(current, _relayTarget) ? current.SocketAddress : RebindRelay(current);
    }

    /// <summary>
    /// Adopts a relay endpoint published by an in-place re-association. The self-traffic tuple is
    /// keyed by remote endpoint, so the old registration no longer covers this socket's traffic
    /// and must be replaced, not merely supplemented. Cold by construction: once per re-associated
    /// flow.
    /// </summary>
    private SocketAddress RebindRelay(UdpRelayTarget relay)
    {
        var registration = _selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
            TransportProtocol.Udp,
            _localRelayEndpoint,
            Endpoint.From(relay.Endpoint.Address, checked((ushort)relay.Endpoint.Port))));
        // The destination is adopted only once its tuple is registered, so a failed registration
        // leaves the rebind to be retried by the next send.
        Volatile.Write(ref _relayTarget, relay);
        Interlocked.Exchange(ref _selfTrafficToken, registration)?.Dispose();
        // Disposal does not take the send gate, so it may have swapped the token out while this
        // rebind ran; releasing the tuple here keeps a dead transport's registration from outliving
        // it in the process-wide registry, where a reused local port could match it.
        if (Volatile.Read(ref _disposed) != 0 && ReferenceEquals(Interlocked.Exchange(ref _selfTrafficToken, value: null), registration)) registration.Dispose();
        return relay.SocketAddress;
    }

    /// <summary>
    /// Receives one datagram from the relay. <paramref name="buffer"/> is the caller's receive
    /// window — at composition sized cap + 22 + 1 (frame cap, maximum SOCKS5 UDP header, one
    /// oversize sentinel) — so a datagram that fills it reports the
    /// <see cref="Socks5UdpReceiveSkipReason.Oversized"/> skip: the deliverable response-payload
    /// ceiling is cap - 42 (Ethernet + IPv4 + UDP; 1472 bytes at the pinned 1514 ABI), and larger
    /// relay responses surface in the session's rate-limited skip summary. A jumbo-capable ABI
    /// lifts the ceiling end-to-end because the send buffer follows the same cap.
    /// </summary>
    public async ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EndPoint sender = _receiveSenderTemplate;
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
        // datagram must not terminate the session's receive loop (R2). The relay endpoint is read
        // through the lease so a re-associated flow validates against its current relay.
        var relay = _lease.RelayEndpoint;
        if (!IsAcceptableRelaySource(result.RemoteEndPoint, relay)) return Socks5UdpReceiveResult.Skipped(Socks5UdpReceiveSkipReason.UnexpectedSource);
        if (IsPossiblyTruncated(result.ReceivedBytes, buffer.Length)) return Socks5UdpReceiveResult.Skipped(Socks5UdpReceiveSkipReason.Oversized);
        // M2: the SOCKS5 UDP wire format carries no interface scope, so propagate the relay
        // endpoint's IPv6 scope into reconstruction to keep a link-local decoded address routable.
        var scopeId = relay.Address.AddressFamily == AddressFamily.InterNetworkV6 ? relay.Address.ScopeId : 0;
        // ReSharper disable once ConvertIfStatementToReturnStatement // TryDecode decodes into an out parameter (side effect + binding); the early exit on malformed input must stay a separate step (B1 disposition).
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

    /// <summary>
    /// A receive that fills the caller's buffer may be truncated, so it is skipped instead of
    /// decoded. The session's receive window is cap + 22 + 1, which makes cap - 42 (Ethernet +
    /// IPv4 + UDP) the deliverable payload ceiling — 1472 bytes at the pinned 1514 ABI; larger
    /// relay responses skip as <see cref="Socks5UdpReceiveSkipReason.Oversized"/> and surface in
    /// the session's rate-limited skip summary. A jumbo-capable ABI lifts the ceiling end-to-end
    /// because the send buffer follows the same cap.
    /// </summary>
    internal static bool IsPossiblyTruncated(int receivedBytes, int bufferLength) => receivedBytes >= bufferLength;

    /// <summary>
    /// Disables SIO_UDP_CONNRESET on a relay socket so an ICMP port-unreachable answering one of
    /// its sends is not surfaced as <see cref="SocketError.ConnectionReset"/> on the next receive
    /// (S2). Windows-only at runtime: the Linux test host rejects vendor IOCTLs, and tests assert
    /// the call through the injectable seam instead of executing it.
    /// </summary>
    private static void DisableUdpConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        socket.IOControl(SIOUdpConnreset, s_disableValue, optionOutValue: null);
    }

    /// <summary>
    /// Maps a receive-path fault to its skip reason, or null when the fault is socket-level fatal
    /// and must keep tearing the session down. Only the ICMP-driven reset is skip-class (S2).
    /// </summary>
    internal static Socks5UdpReceiveSkipReason? ClassifyReceiveFault(Exception exception) =>
        exception is SocketException { SocketErrorCode: SocketError.ConnectionReset }
            ? Socks5UdpReceiveSkipReason.ConnectionReset
            : null;

    /// <summary>
    /// Releases the relay socket, its self-traffic tuple, and the association lease — exactly once,
    /// through every path, even when an earlier release throws. The lease release is what decrements
    /// the shared association's refcount (and closes a private association), so it must run before
    /// the send gate is disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // A repeat dispose returns without re-running the teardown; the first caller owns it.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _socket.Dispose();
        }
        finally
        {
            try
            {
                Interlocked.Exchange(ref _selfTrafficToken, value: null)?.Dispose();
            }
            finally
            {
                try
                {
                    await _lease.DisposeAsync().ConfigureAwait(false);
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
