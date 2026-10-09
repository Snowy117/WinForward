using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Runtime.Socks5;

/// <summary>
/// Creates the per-flow relay transport: one authenticated SOCKS5 UDP association per flow (its own
/// control connection and its own <c>UDP ASSOCIATE</c>), plus the flow's own relay socket. The
/// factory owns no association: it builds one per call and hands it to the transport, which owns it
/// for the flow's life.
/// </summary>
public sealed class Socks5UdpTransportFactory : IUdpProxyTransportFactory
{
    /// <summary>
    /// The association-lost warn's window, held by the factory so it covers every association the
    /// composition creates.
    /// </summary>
    private static readonly TimeSpan s_lostLogInterval = TimeSpan.FromSeconds(5);

    private readonly Socks5UdpAssociationContext _associations;
    private readonly int _maximumFrameSize;
    private readonly int _relayReceiveBufferBytes;
    private readonly Func<AddressFamily, Socket>? _socketFactory;
    private readonly Action<Socket>? _disableUdpConnectionReset;

    /// <summary>
    /// Creates transports whose send buffer follows the pinned frame cap (6 + 16 + cap) — the
    /// same single source of truth the coordinator's receive windows (cap + 22 + 1) and the
    /// reinjector's rebuilt frames (cap) already use — and whose control connection is dialed per
    /// flow. Composition passes the native ABI constant explicitly, and the per-session relay
    /// receive buffer from the validated <c>udpRelayReceiveBufferKb</c> budget.
    /// </summary>
    /// <param name="selfTraffic">Loop-prevention registry the relay tuple is registered in before the first datagram.</param>
    /// <param name="maximumFrameSize">The pinned capture frame cap the send buffer follows (6 + 16 + cap).</param>
    /// <param name="relayReceiveBufferBytes">The per-session relay socket receive buffer from the validated <c>udpRelayReceiveBufferKb</c> budget.</param>
    /// <param name="addressCache">Shared SOCKS5 endpoint cache; null resolves on every dial.</param>
    /// <param name="logger">Lifecycle diagnostics; null keeps the association silent.</param>
    /// <param name="createControl">Test seam replacing the real dial.</param>
    /// <param name="socketFactory">Test seam: the relay socket constructor, so a construction failure can be driven through the production association-release path.</param>
    /// <param name="disableUdpConnectionReset">Test seam: the SIO_UDP_CONNRESET posture applied before bind (asserted without a Windows host).</param>
    internal Socks5UdpTransportFactory(
        SelfTrafficRegistry selfTraffic,
        int maximumFrameSize,
        int relayReceiveBufferBytes = Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize,
        Socks5AddressCache? addressCache = null,
        ILogger? logger = null,
        Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null,
        Func<AddressFamily, Socket>? socketFactory = null,
        Action<Socket>? disableUdpConnectionReset = null)
    {
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relayReceiveBufferBytes);
        _associations = new Socks5UdpAssociationContext(
            selfTraffic,
            addressCache,
            createControl,
            logger ?? NullLogger.Instance,
            new RuntimeLogThrottle(s_lostLogInterval));
        _maximumFrameSize = maximumFrameSize;
        _relayReceiveBufferBytes = relayReceiveBufferBytes;
        _socketFactory = socketFactory;
        _disableUdpConnectionReset = disableUdpConnectionReset;
    }

    public async ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        // This factory serves SOCKS5 targets only (a local target has no association to dial), so
        // the impossible shape fails closed instead of dereferencing a null server.
        if (target.Socks5 is not { } server)
        {
            throw new InvalidOperationException($"The SOCKS5 UDP transport factory was asked for local target '{target.Name}'.");
        }

        // UoT is a mode of the SOCKS5 target, not a third kind (design §1): the opt-in flag picks
        // the per-flow connection whose stream carries the flow's datagrams, and every other SOCKS5
        // server keeps the native per-flow association and relay socket path below unchanged.
        if (server.UdpOverTcp)
        {
            var control = await Socks5UotTransport.DialAsync(server, _associations, cancellationToken).ConfigureAwait(false);
            try
            {
                return Socks5UotTransport.Create(control, _maximumFrameSize);
            }
            catch
            {
                // A transport that never came into existence must not leave its control connection open.
                await control.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var association = await Socks5UdpAssociation.ConnectAsync(server, _associations, cancellationToken).ConfigureAwait(false);
        try
        {
            return Socks5UdpTransport.Create(association, _associations.SelfTraffic, _socketFactory, _disableUdpConnectionReset, _maximumFrameSize, _relayReceiveBufferBytes);
        }
        catch
        {
            // A transport that never came into existence must not leave its control connection open.
            await association.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class Socks5UdpTransport : IUdpProxyTransport, IUdpExchangeCounters
{
    /// <summary>
    /// The relay socket's default receive buffer, wired from the validated
    /// <c>udpRelayReceiveBufferKb</c> configuration key (default 64 KiB, range 16..1024 KiB).
    /// Relay responses can burst faster than the single receive loop reinjects them, so the OS
    /// default datagram buffer would overflow and drop responses that were already relayed; the
    /// historical 512 KiB absorbed that. It is bounded because this buffer is per session: the
    /// aggregate kernel memory is per-session bytes x concurrent sessions, and relay sockets are
    /// retained for the session's whole life, so a large constant multiplies by flow churn instead
    /// of following the active flow set. 64 KiB holds ≈44 maximum-size (standard-MTU) responses or
    /// ≈128 512-byte ones per socket — orders of magnitude above the receive loop's per-datagram
    /// latency at the recorded load — and halves the aggregate at any population. A response burst
    /// larger than the buffer arriving between two decode passes is dropped silently by the kernel;
    /// the recorded loss/burst/churn anchors are the only instrument that can observe that, and
    /// <c>udpRelayReceiveBufferKb: 128</c> restores the previous value. Config validation warns when
    /// the per-session value times a large session budget exceeds ~512 MiB.
    /// </summary>
    public const int DefaultRelaySocketReceiveBufferSize = 64 * 1024;

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
    private readonly Socks5UdpAssociation _association;
    private readonly SocketAddress _relaySocketAddress;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _sendBuffer;
    private readonly IPEndPoint _receiveSenderTemplate;
    private readonly SelfTrafficRegistry.SelfTrafficToken? _selfTrafficToken;

    /// <summary>The datagrams this flow sent successfully (the exchange-evidence read side).</summary>
    private int _datagramsSent;

    /// <summary>Whether this flow ever decoded a relay response (the exchange-evidence read side).</summary>
    private int _sawResponse;

    private int _disposed;

    private Socks5UdpTransport(Socks5UdpAssociation association, Socket socket, SocketAddress relaySocketAddress, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken, int maximumFrameSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameSize);
        _association = association;
        _socket = socket;
        _relaySocketAddress = relaySocketAddress;
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
        _receiveSenderTemplate = association.RelayAddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(IPAddress.Any, 0)
            : new IPEndPoint(IPAddress.IPv6Any, 0);
    }

    /// <summary>
    /// The relay endpoint this flow sends to and accepts replies from: the relay its own
    /// association negotiated, fixed for the flow's life.
    /// </summary>
    public IPEndPoint PeerEndpoint => _association.RelayEndpoint;

    public IPEndPoint LocalEndpoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>
    /// The relay socket's applied receive buffer (SO_RCVBUF, as the OS settled it). Internal test
    /// seam: it proves the configured <c>udpRelayReceiveBufferKb</c> budget reaches a real socket
    /// through the production <see cref="Socks5UdpTransportFactory"/>, which the direct-construction
    /// test alone cannot show.
    /// </summary>
    internal int AppliedRelayReceiveBufferSize => _socket.ReceiveBufferSize;

    // Explicit: the retention policy's evidence seam is deliberately not part of the transport
    // contract, so adding it cannot change what an IUdpProxyTransport implementer must provide.
    int IUdpExchangeCounters.DatagramsSent => Volatile.Read(ref _datagramsSent);

    bool IUdpExchangeCounters.SawResponse => Volatile.Read(ref _sawResponse) != 0;

    /// <summary>
    /// Builds the per-flow transport over one association the caller owns until this returns: the
    /// relay socket is bound in the association's relay family, the configured receive buffer and
    /// SIO_UDP_CONNRESET posture are applied before bind, and the relay tuple is registered for loop
    /// prevention before the first datagram. The caller owns the association and disposes it if this
    /// throws; on success the transport owns both the socket and the association.
    /// </summary>
    internal static Socks5UdpTransport Create(
        Socks5UdpAssociation association,
        SelfTrafficRegistry selfTraffic,
        Func<AddressFamily, Socket>? socketFactory = null,
        Action<Socket>? disableUdpConnectionReset = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        int relayReceiveBufferBytes = DefaultRelaySocketReceiveBufferSize)
    {
        ArgumentNullException.ThrowIfNull(association);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relayReceiveBufferBytes);
        Socket? socket = null;
        SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken = null;
        try
        {
            var relayAddressFamily = association.RelayAddressFamily;
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
            var relay = association.RelayEndpoint;
            // Register the relay transport tuple in the loop-prevention registry so catch-all proxy
            // rules never recursively intercept WinForward's own UDP relay traffic (design §10).
            selfTrafficToken = selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Udp,
                localRelayEndpoint,
                Endpoint.From(relay.Address, checked((ushort)relay.Port))));
            var transport = new Socks5UdpTransport(association, socket, association.RelaySocketAddress, selfTrafficToken, maximumFrameSize);
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

#pragma warning disable RCS1229 // Deliberate non-async warm entry (hot-path.md → warm-path-dispatch.md, "No async state machines on the steady-state path"): the steady-state send path must not pay an async state machine; a synchronous failure before the returned ValueTask is part of the warm contract and handled by the dispatcher.
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
        // I4 fail-closed: an association whose control stream ended refuses the datagram before the
        // gate and before the socket, so the coordinator tears the flow down with the
        // association-lost reason instead of writing to a relay no one is watching. One volatile
        // read plus a reference read; the exception itself is cold and already carries the death.
        if (_association.Fault is { } lost) throw lost;
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
                _ = _socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, _relaySocketAddress);
            }
            catch (SocketException)
            {
                return SendOverlappedAsync(written, cancellationToken);
            }

            // Recorded only after the kernel accepted the datagram, so the one-shot retention class
            // reads sends that actually went out (I3: one interlocked increment, no allocation).
            RecordDatagramSent();
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
            // Post-wait half of the warm entry's guard: a sender that waited through teardown is
            // refused here rather than sending to a closed relay socket.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_association.Fault is { } lost) throw lost;

            if (!Socks5UdpCodec.TryEncode(destination.Address, destination.Port, payload.Span, _sendBuffer, out var written))
            {
                throw new IOException("A SOCKS5 UDP datagram exceeded the relay send buffer.");
            }

            _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, written), SocketFlags.None, _relaySocketAddress, cancellationToken).ConfigureAwait(false);
            RecordDatagramSent();
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
            _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, written), SocketFlags.None, _relaySocketAddress, cancellationToken).ConfigureAwait(false);
            RecordDatagramSent();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Records one datagram this flow sent successfully. One interlocked increment, no allocation —
    /// this is the whole hot-path accounting addition (I3).
    /// </summary>
    private void RecordDatagramSent() => Interlocked.Increment(ref _datagramsSent);

    /// <summary>
    /// Records the first relay datagram this flow decoded successfully. A plain volatile read
    /// short-circuits every later response, so the flag costs one interlocked operation per flow,
    /// not per datagram.
    /// </summary>
    private void RecordResponseReceived()
    {
        if (Volatile.Read(ref _sawResponse) != 0) return;
        _ = Interlocked.Exchange(ref _sawResponse, 1);
    }

    /// <summary>
    /// Receives one datagram from the relay. <paramref name="buffer"/> is the caller's receive
    /// window — at composition sized cap + 22 + 1 (frame cap, maximum SOCKS5 UDP header, one
    /// oversize sentinel) — so a datagram that fills it reports the
    /// <see cref="UdpTransportSkipReason.Oversized"/> skip: the deliverable response-payload
    /// ceiling is cap - 42 (Ethernet + IPv4 + UDP; 1472 bytes at the pinned 1514 ABI), and larger
    /// relay responses surface in the session's rate-limited skip summary. A jumbo-capable ABI
    /// lifts the ceiling end-to-end because the send buffer follows the same cap.
    /// </summary>
    public async ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EndPoint sender = _receiveSenderTemplate;
        SocketReceiveFromResult result;
        try
        {
            result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fault) when (ClassifyReceiveFault(fault) is { } skipReason)
        {
            return UdpTransportReceiveResult.Skipped(skipReason);
        }
        // Per-datagram anomalies skip one datagram instead of throwing: a single bad relay
        // datagram must not terminate the session's receive loop (R2).
        var relay = _association.RelayEndpoint;
        if (!IsAcceptableRelaySource(result.RemoteEndPoint, relay)) return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.UnexpectedSource);
        if (IsPossiblyTruncated(result.ReceivedBytes, buffer.Length)) return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.Oversized);
        // M2: the SOCKS5 UDP wire format carries no interface scope, so propagate the relay
        // endpoint's IPv6 scope into reconstruction to keep a link-local decoded address routable.
        var scopeId = relay.Address.AddressFamily == AddressFamily.InterNetworkV6 ? relay.Address.ScopeId : 0;
        // ReSharper disable once ConvertIfStatementToReturnStatement // TryDecode decodes into an out parameter (side effect + binding); the early exit on malformed input must stay a separate step (B1 disposition).
        if (!Socks5UdpCodec.TryDecode(buffer[..result.ReceivedBytes], out var datagram, scopeId)) return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.Malformed);
        // Only a datagram that decoded proves this flow's replies come back: a skip is one anomaly,
        // not evidence that the server answered (design §5).
        RecordResponseReceived();
        // Both types are readonly record structs, so this field-for-field mapping is a plain struct
        // copy and adds no allocation to the receive path.
        return UdpTransportReceiveResult.Received(new UdpTransportDatagram(datagram.DestinationAddress, datagram.DestinationDomain, datagram.DestinationPort, datagram.Payload));
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
    /// relay responses skip as <see cref="UdpTransportSkipReason.Oversized"/> and surface in
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
    /// and must keep tearing the session down. Only the ICMP-driven reset is skip-class (S2); the
    /// adjudication itself is the seam's <see cref="UdpTransportReceiveClassifier"/>, the rule every
    /// transport implementation shares.
    /// </summary>
    internal static UdpTransportSkipReason? ClassifyReceiveFault(Exception exception) =>
        UdpTransportReceiveClassifier.ClassifyFault(exception);

    /// <summary>
    /// Releases the relay socket, its self-traffic tuple, and the flow's association (its control
    /// connection and its watchdog) — exactly once, through every path, even when an earlier release
    /// throws. The send gate is deliberately left undisposed; the body says why.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // A repeat dispose returns without re-running the teardown; the first caller owns it.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // The send gate is deliberately left undisposed. SemaphoreSlim.Dispose only frees the lazily
        // created WaitHandle (never requested here), while disposing it with waiters parked strands
        // those waits forever — and a stranded sender holds its caller's work lease, so a teardown
        // drain would wait on it. The guard refuses new senders; a parked sender is released by the
        // disposed socket's faulted send and refused by SendAfterGateAsync's post-wait re-check.
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
                await _association.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
