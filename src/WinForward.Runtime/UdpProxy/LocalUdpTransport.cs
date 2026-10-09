using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// Creates the per-flow transport for a local target: one socket per flow, bound like a relay
/// socket and pointed at the configured endpoint, with no association to rent. A target of any other
/// kind is refused up front, so a mis-composed factory fails closed instead of dereferencing a target
/// it does not serve.
/// </summary>
public sealed class LocalUdpTransportFactory : IUdpProxyTransportFactory
{
    private readonly SelfTrafficRegistry _selfTraffic;
    private readonly int _maximumFrameSize;
    private readonly int _receiveBufferBytes;
    private readonly Func<AddressFamily, Socket>? _socketFactory;
    private readonly Action<Socket>? _disableUdpConnectionReset;

    /// <summary>
    /// Creates transports whose send buffer follows the pinned frame cap (the same single source of
    /// truth the coordinator's receive windows and the reinjector's rebuilt frames use) and whose
    /// receive buffer follows the validated <c>udpRelayReceiveBufferKb</c> budget.
    /// </summary>
    /// <param name="selfTraffic">Loop-prevention registry the flow's tuple is registered in before the first datagram.</param>
    /// <param name="maximumFrameSize">The pinned capture frame cap the send buffer follows.</param>
    /// <param name="receiveBufferBytes">The per-session socket receive buffer from the validated <c>udpRelayReceiveBufferKb</c> budget.</param>
    /// <param name="socketFactory">Test seam: the socket constructor, so a construction failure can be driven through the production path.</param>
    /// <param name="disableUdpConnectionReset">Test seam: the SIO_UDP_CONNRESET posture applied before bind (asserted without a Windows host).</param>
    internal LocalUdpTransportFactory(
        SelfTrafficRegistry selfTraffic,
        int maximumFrameSize,
        int receiveBufferBytes = ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes,
        Func<AddressFamily, Socket>? socketFactory = null,
        Action<Socket>? disableUdpConnectionReset = null)
    {
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveBufferBytes);
        _selfTraffic = selfTraffic;
        _maximumFrameSize = maximumFrameSize;
        _receiveBufferBytes = receiveBufferBytes;
        _socketFactory = socketFactory;
        _disableUdpConnectionReset = disableUdpConnectionReset;
    }

    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        // This factory serves local targets only (a SOCKS5 server is the relay factory's), so the
        // impossible shape fails closed before any socket exists.
        if (target.Local is not { } local)
        {
            throw new InvalidOperationException($"The local UDP transport factory was asked for SOCKS5 target '{target.Name}'.");
        }

        var transport = LocalUdpTransport.Create(local, _selfTraffic, _socketFactory, _disableUdpConnectionReset, _maximumFrameSize, _receiveBufferBytes);
        _ = RuntimeCounters.Shared.Increment(RuntimeCounters.UdpLocalTargetFlows);
        return ValueTask.FromResult<IUdpProxyTransport>(transport);
    }
}

/// <summary>
/// The per-flow transport of a local target: exactly one socket, bound to the wildcard address of
/// the target's family, that forwards each datagram verbatim to the configured endpoint and reports
/// the endpoint's replies to the session with the flow's own destination as their declared source.
/// Nothing about the payload is parsed, rewritten, or cached, and no association is involved: the
/// flow's identity is its socket, so a reply of one flow can never be delivered to another's
/// session at this hop. Replies are accepted only from the configured endpoint (address bits, port,
/// and family), and every failure fails closed by throwing to the caller that owns the flow's slot.
/// </summary>
public sealed class LocalUdpTransport : IUdpProxyTransport, IUdpExchangeCounters
{
    /// <summary>
    /// SIO_UDP_CONNRESET (vendor IOCTL 0x9800000C). While TRUE (the Windows default for UDP
    /// sockets), an ICMP port-unreachable answering one of this socket's sends is surfaced as
    /// <see cref="SocketError.ConnectionReset"/> on the next receive, which would terminate the
    /// session's receive loop (S2).
    /// </summary>
    private const int SIOUdpConnreset = unchecked((int)0x9800000C);

    /// <summary>A 4-byte Win32 BOOL FALSE, the SIO_UDP_CONNRESET input value.</summary>
    private static readonly byte[] s_disableValue = new byte[4];

    /// <summary>Cached default for <c>disableUdpConnectionReset</c> so setup never converts the method group per call.</summary>
    private static readonly Action<Socket> s_disableUdpConnectionResetAction = DisableUdpConnectionReset;

    private readonly Socket _socket;
    private readonly SocketAddress _targetSocketAddress;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _sendBuffer;
    private readonly IPEndPoint _receiveSenderTemplate;

    /// <summary>The loop-prevention registration of this socket's send tuple.</summary>
    private SelfTrafficRegistry.SelfTrafficToken? _selfTrafficToken;

    /// <summary>
    /// The flow's destination, re-recorded by every <see cref="SendSpanAsync"/> call: the receive path
    /// declares it as the reply's source, so the value the session compares against its own
    /// <c>Flow.Remote</c> is the very destination the datagram was sent to — any drift between the two
    /// surfaces as a counted foreign-source reply instead of hiding behind a stale record.
    /// </summary>
    private Endpoint _destination;

    /// <summary>
    /// Whether <see cref="_destination"/> has been recorded by a send yet, published by a release write
    /// after the struct and read by an acquire read before it (a struct field cannot itself be
    /// <c>volatile</c>, and the receive loop runs on another thread than the session's send path). The
    /// destination is constant in production today — the coordinator passes <c>Flow.Remote</c> on every
    /// send — but the guard must not depend on that: a later re-routing of a live flow would otherwise
    /// let the reader observe a torn, stale value.
    /// </summary>
    private bool _destinationRecorded;

    private int _datagramsSent;
    private int _sawResponse;
    private int _disposed;

    private LocalUdpTransport(Socket socket, Endpoint target, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken, int maximumFrameSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameSize);
        _socket = socket;
        PeerEndpoint = new IPEndPoint(target.Address.ToIPAddress(), target.Port);
        _targetSocketAddress = PeerEndpoint.Serialize();
        _selfTrafficToken = selfTrafficToken;
        // The local hop carries no wire header, so the send buffer is the frame cap itself: a captured
        // payload can never exceed cap - 42 (Ethernet + IPv4 + UDP), and a datagram that does not fit
        // stays a fail-closed IOException rather than a truncated send.
        _sendBuffer = new byte[maximumFrameSize];
        // The receive-sender family is fixed by the target endpoint, so one template per transport
        // replaces the per-receive endpoint allocation; ReceiveFromAsync reports the actual remote in
        // its result and never mutates the template.
        _receiveSenderTemplate = target.AddressFamily == AddressFamilyKind.IPv4
            ? new IPEndPoint(IPAddress.Any, 0)
            : new IPEndPoint(IPAddress.IPv6Any, 0);
    }

    /// <summary>The configured local endpoint: replies are expected from it and this transport's own traffic is keyed on it.</summary>
    public IPEndPoint PeerEndpoint { get; }

    /// <summary>This flow's own local socket endpoint: the half of the flow's identity pair that is local to this host, owned by this flow alone.</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>
    /// The socket's applied receive buffer (SO_RCVBUF, as the OS settled it). Internal test seam: it
    /// proves the configured <c>udpRelayReceiveBufferKb</c> budget reaches a real socket through the
    /// production <see cref="LocalUdpTransportFactory"/>, which direct construction cannot show.
    /// </summary>
    internal int AppliedReceiveBufferSize => _socket.ReceiveBufferSize;

    // Explicit: the retention policy's evidence seam is deliberately not part of the transport
    // contract, so adding it cannot change what an IUdpProxyTransport implementer must provide.
    int IUdpExchangeCounters.DatagramsSent => Volatile.Read(ref _datagramsSent);

    bool IUdpExchangeCounters.SawResponse => Volatile.Read(ref _sawResponse) != 0;

    /// <summary>
    /// Builds the per-flow transport for one local target: the socket binds to the wildcard address of
    /// the target's family with the configured receive buffer, the SIO_UDP_CONNRESET posture is applied
    /// before bind, and the flow's send tuple is registered for loop prevention before the first
    /// datagram (load-bearing for a non-loopback target, whose traffic traverses a captured adapter).
    /// The caller owns the returned transport and disposes it if this throws.
    /// </summary>
    internal static LocalUdpTransport Create(
        LocalTarget target,
        SelfTrafficRegistry selfTraffic,
        Func<AddressFamily, Socket>? socketFactory = null,
        Action<Socket>? disableUdpConnectionReset = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        int receiveBufferBytes = ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveBufferBytes);
        Socket? socket = null;
        SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken = null;
        try
        {
            var family = target.Endpoint.AddressFamily == AddressFamilyKind.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
            socket = (socketFactory ?? (socketFamily => new Socket(socketFamily, SocketType.Dgram, ProtocolType.Udp)))(family);
            socket.ReceiveBufferSize = receiveBufferBytes;
            // Applied before bind per the IOCTL's contract (S2): an ICMP-driven reset must never
            // reach the receive loop. Injectable so tests can assert the call without a Windows socket.
            (disableUdpConnectionReset ?? s_disableUdpConnectionResetAction)(socket);
            socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
            // Non-blocking mode keeps the send warm path synchronous: the kernel either takes the
            // datagram inline or reports WouldBlock, which falls back to the overlapped send. Async
            // receive operations are unaffected by the non-blocking mode.
            socket.Blocking = false;
            var local = (IPEndPoint)socket.LocalEndPoint!;
            var localEndpoint = Endpoint.From(local.Address, checked((ushort)local.Port));
            // Register the flow's send tuple so catch-all proxy rules never recursively intercept this
            // transport's own traffic: for a loopback target it is hygiene, for a non-loopback one the
            // datagrams and the endpoint's replies really do traverse a captured adapter.
            selfTrafficToken = selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Udp,
                localEndpoint,
                target.Endpoint));
            var transport = new LocalUdpTransport(socket, target.Endpoint, selfTrafficToken, maximumFrameSize);
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
        // The destination is recorded before every send attempt, so the reply's declared source is the
        // destination this transport was asked to reach on this call — never a stale earlier one.
        _destination = destination;
        Volatile.Write(ref _destinationRecorded, true);
        // The gate is disposed last, so a sender that has not yet entered it must be refused here;
        // otherwise `_sendGate.WaitAsync` would observe the disposed gate. A single volatile read
        // keeps the warm shape allocation-free.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var gateWait = _sendGate.WaitAsync(cancellationToken);
        if (!gateWait.IsCompletedSuccessfully)
        {
            // Documented cold-path exemption (task 09-18 M2): this copy allocates, but only on the
            // contended-gate branch. The span cannot cross the gate await (it views native capture
            // memory that recycles once the dispatch returns), so the datagram is materialized and
            // rides the memory slow path; the warm uncontended shape below stays zero-alloc.
            return SendAfterGateAsync(gateWait, payload.ToArray(), cancellationToken);
        }

        try
        {
            if (payload.Length > _sendBuffer.Length)
            {
                throw new IOException("A local-target datagram exceeded the transport send buffer.");
            }

            payload.CopyTo(_sendBuffer);

            try
            {
                _ = _socket.SendTo(_sendBuffer.AsSpan(0, payload.Length), SocketFlags.None, _targetSocketAddress);
            }
            catch (SocketException)
            {
                return SendOverlappedAsync(payload.Length, cancellationToken);
            }

            _ = Interlocked.Increment(ref _datagramsSent);
            _sendGate.Release();
            return ValueTask.CompletedTask;
        }
        catch (Exception fault)
        {
            RecordFailureIfGenuine(fault, cancellationToken);
            _sendGate.Release();
            throw;
        }
    }

    private async ValueTask SendAfterGateAsync(Task gateWait, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await gateWait.ConfigureAwait(false);
        try
        {
            // Post-wait half of the warm entry's guard: a sender that waited through teardown is
            // refused here rather than sending to a closed socket.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (payload.Length > _sendBuffer.Length)
            {
                throw new IOException("A local-target datagram exceeded the transport send buffer.");
            }

            payload.CopyTo(_sendBuffer);
            _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, payload.Length), SocketFlags.None, _targetSocketAddress, cancellationToken).ConfigureAwait(false);
            _ = Interlocked.Increment(ref _datagramsSent);
        }
        catch (Exception fault)
        {
            RecordFailureIfGenuine(fault, cancellationToken);
            throw;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async ValueTask SendOverlappedAsync(int length, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _socket.SendToAsync(_sendBuffer.AsMemory(0, length), SocketFlags.None, _targetSocketAddress, cancellationToken).ConfigureAwait(false);
            _ = Interlocked.Increment(ref _datagramsSent);
        }
        catch (Exception fault)
        {
            RecordFailureIfGenuine(fault, cancellationToken);
            throw;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Receives one datagram from the configured endpoint. <paramref name="buffer"/> is the caller's
    /// receive window (frame cap + maximum SOCKS5 UDP header + one oversize sentinel at composition,
    /// so the local hop inherits the pipeline's deliverable ceiling), and a datagram that fills it is a
    /// skip: it may have been truncated. The accepted datagram is declared to have come from the
    /// flow's own destination, which is what keeps <c>UdpProxySession</c>'s source check, its
    /// foreign-source counter, and the response reinjector unchanged — the client observes the answer
    /// with the address and port it originally sent to.
    /// </summary>
    public async ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EndPoint sender = _receiveSenderTemplate;
        SocketReceiveFromResult result;
        try
        {
            result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fault) when (UdpTransportReceiveClassifier.ClassifyFault(fault) is { } skipReason)
        {
            return UdpTransportReceiveResult.Skipped(skipReason);
        }
        catch (Exception fault)
        {
            RecordReceiveFailureIfGenuine(fault, cancellationToken);
            throw;
        }

        // The socket is bound from construction, so its port is reachable before this flow ever sent a
        // datagram. There is no destination to declare as the reply's source yet, and delivering it with
        // the default endpoint would inject a bogus frame toward the client (and move the R2 counter), so
        // it is skipped as unexpected until a send records one. Acquire read, pairing the send path's
        // release write (see _destinationRecorded).
        if (!Volatile.Read(ref _destinationRecorded)) return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.UnexpectedSource);
        if (!IsAcceptableLocalSource(result.RemoteEndPoint, PeerEndpoint)) return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.UnexpectedSource);
        if (IsPossiblyTruncated(result.ReceivedBytes, buffer.Length)) return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.Oversized);
        // The local hop has no wire header, so the only datagram it cannot deliver is an empty one: a
        // zero-length payload has no data to reinject, and a real UDP datagram always carries its
        // 8-byte header, so this shape is a socket-level anomaly rather than a protocol case.
        if (result.ReceivedBytes == 0) return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.Malformed);
        Volatile.Write(ref _sawResponse, 1);
        var destination = _destination;
        return UdpTransportReceiveResult.Received(new UdpTransportDatagram(destination.Address, SourceDomain: null, destination.Port, buffer[..result.ReceivedBytes]));
    }

    /// <summary>
    /// Validates the observed sender of a reply against the configured local endpoint. A local target
    /// is one specific host this configuration chose, so the rule is exact where the relay's is loose:
    /// port, address family, and every address bit must match. The IPv6 scope is deliberately not
    /// compared — the receive interface legitimately reports its own scope, exactly as the session's
    /// own peer comparison ignores it.
    /// </summary>
    internal static bool IsAcceptableLocalSource(EndPoint observed, IPEndPoint expected) =>
        observed is IPEndPoint source
        && source.Port == expected.Port
        && source.AddressFamily == expected.AddressFamily
        && IPAddressValue.From(source.Address).Bits == IPAddressValue.From(expected.Address).Bits;

    /// <summary>
    /// A receive that fills the caller's buffer may be truncated, so it is skipped instead of
    /// delivered. The session's receive window is the same frame-cap-derived window the relay uses, so
    /// the deliverable payload ceiling is cap - 42 (Ethernet + IPv4 + UDP; 1472 bytes at the pinned
    /// 1514 ABI) and larger replies surface in the session's rate-limited skip summary.
    /// </summary>
    private static bool IsPossiblyTruncated(int receivedBytes, int bufferLength) => receivedBytes >= bufferLength;

    /// <summary>
    /// Disables SIO_UDP_CONNRESET so an ICMP port-unreachable answering one of this socket's sends is
    /// not surfaced as <see cref="SocketError.ConnectionReset"/> on the next receive (S2).
    /// Windows-only at runtime: the Linux test host rejects vendor IOCTLs, and tests assert the call
    /// through the injectable seam instead of executing it.
    /// </summary>
    private static void DisableUdpConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        socket.IOControl(SIOUdpConnreset, s_disableValue, optionOutValue: null);
    }

    /// <summary>
    /// The shared counter rule for <see cref="RuntimeCounters.UdpLocalTargetFailures"/>: a fault moves
    /// it unless it is teardown — a caller cancellation (idle expiry, shutdown), or a fault this
    /// transport's own disposal caused. The relay transport records nothing on those paths either;
    /// counting them would corrupt the population metric this counter exists to provide.
    /// </summary>
    private void RecordFailureIfGenuine(Exception fault, CancellationToken cancellationToken)
    {
        if (IsTeardownFault(fault, cancellationToken)) return;
        _ = RuntimeCounters.Shared.Increment(RuntimeCounters.UdpLocalTargetFailures);
    }

    /// <summary>
    /// The receive path's counter rule: the shared teardown exemption plus the socket itself going
    /// away. A parked receive that a socket close ends reports <see cref="ObjectDisposedException"/>, or
    /// <see cref="SocketError.OperationAborted"/> ("the operation was aborted because the socket was
    /// closed"), and this transport creates the socket and is its only owner — so that shape is always
    /// a teardown, never a fault of the flow's own traffic.
    /// </summary>
    private void RecordReceiveFailureIfGenuine(Exception fault, CancellationToken cancellationToken)
    {
        if (fault is ObjectDisposedException or SocketException { SocketErrorCode: SocketError.OperationAborted }) return;
        RecordFailureIfGenuine(fault, cancellationToken);
    }

    /// <summary>
    /// Whether a fault is the normal end of a session rather than a failure: the caller cancelled the
    /// operation, or this transport's own disposal is what faulted the socket.
    /// </summary>
    private bool IsTeardownFault(Exception fault, CancellationToken cancellationToken) =>
        Volatile.Read(ref _disposed) != 0
        || fault is OperationCanceledException
        || cancellationToken.IsCancellationRequested;

    /// <summary>
    /// Releases the socket and its self-traffic tuple — exactly once, through every path, even when an
    /// earlier release throws. The send gate is deliberately left undisposed; the body says why.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;

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
            Interlocked.Exchange(ref _selfTrafficToken, value: null)?.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
