using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Runtime.Socks5;

/// <summary>
/// The per-flow UDP-over-TCP v2 (connect mode) transport: one authenticated SOCKS5 connection per
/// flow whose stream is bound to the flow's single destination for its whole life, owned and
/// disposed with the flow — no sharing, no pooling, no warm reuse (R1).
/// <para>
/// Establishment is pipelined (R2). The dial writes the greeting (and the RFC 1929 message when
/// credentials are configured) and returns without reading a reply; the flow's first datagram then
/// writes one buffer — the <c>CONNECT</c> to <see cref="UotCodec.MagicAddress"/>, the UoT request
/// header, and the <c>u16be length | payload</c> frame — and does <b>not</b> await the CONNECT
/// reply. Reply validation moves to the receive path, which consumes the deferred method/auth
/// replies and then the CONNECT reply before the first frame; a refusal discovered there is
/// <see cref="UdpTransportHandshakeRejectedException"/>, a death after establishment
/// <see cref="UdpAssociationLostException"/> (R4).
/// </para>
/// </summary>
public sealed class Socks5UotTransport : IUdpProxyTransport, IUdpExchangeCounters
{
    /// <summary>
    /// The fixed prefix the first send reserves: the 30-byte magic <c>CONNECT</c>
    /// (<c>VER CMD RSV ATYP 3 | len 23 | "sp.v2.udp-over-tcp.arpa" | port</c>), the longest UoT
    /// request header (IPv6, 20 bytes), and the 2-byte frame prefix. The send buffer is
    /// <c>UotSendPrefixLength + maximumFrameSize</c>, so a captured payload (bounded at
    /// <c>cap - 42</c> by the pipeline) always fits (design §4).
    /// </summary>
    private const int UotSendPrefixLength = 30 + UotCodec.MaximumRequestHeaderLength + UotCodec.FrameHeaderSize;

    /// <summary>
    /// The longest SOCKS5 reply the CONNECT reply reader accepts: an IPv4/IPv6 bound address or a
    /// 255-byte domain form (<c>Socks5Messages.TryGetReplyLength</c>'s maximum).
    /// </summary>
    private const int ReplyScratchLength = 4 + 1 + 255 + 2;

    /// <summary>
    /// The window the CONNECT reply must arrive in. The deferred completion is already bounded by
    /// the connection's own attempt deadline; this read is not, and a server that accepts the
    /// CONNECT without ever answering must fail the flow's setup instead of parking its receive
    /// loop until idle expiry. The value mirrors the control connection's per-attempt timeout.
    /// </summary>
    private static readonly TimeSpan s_connectReplyTimeout = TimeSpan.FromSeconds(30);

    private readonly Socks5ControlConnection _control;
    private readonly Stream _stream;
    private readonly Socket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _sendBuffer;
    private readonly byte[] _replyScratch = new byte[ReplyScratchLength];
    private readonly int _maximumFrameSize;

    /// <summary>The flow's destination, captured by the first send; the receive path declares it as every reply's source.</summary>
    private Endpoint _destination;

    /// <summary>Whether <see cref="_destination"/> was captured, published after it and read before it by the receive loop.</summary>
    private bool _destinationRecorded;

    /// <summary>Whether the CONNECT request and the UoT request header were written; only ever read and written under <see cref="_sendGate"/>.</summary>
    private bool _handshakeWritten;

    /// <summary>Whether the deferred handshake and the CONNECT reply were consumed.</summary>
    private int _established;

    /// <summary>The recorded typed death: a setup rejection or an established-phase loss, thrown by every later send.</summary>
    private Exception? _fault;

    private int _datagramsSent;
    private int _sawResponse;
    private int _disposed;

    private Socks5UotTransport(Socks5ControlConnection control, Stream stream, Socket socket, IPEndPoint peerEndpoint, IPEndPoint localEndpoint, int maximumFrameSize)
    {
        _control = control;
        _stream = stream;
        _socket = socket;
        _maximumFrameSize = maximumFrameSize;
        _sendBuffer = new byte[UotSendPrefixLength + maximumFrameSize];
        PeerEndpoint = peerEndpoint;
        LocalEndpoint = localEndpoint;
    }

    /// <summary>The SOCKS5 server endpoint this flow's connection was dialed to, fixed for the flow's life.</summary>
    public IPEndPoint PeerEndpoint { get; }

    /// <summary>This flow's own local endpoint: the half of the flow's identity pair that is local to this host.</summary>
    public IPEndPoint LocalEndpoint { get; }

    // Explicit: the retention policy's evidence seam is deliberately not part of the transport
    // contract, so adding it cannot change what an IUdpProxyTransport implementer must provide.
    int IUdpExchangeCounters.DatagramsSent => Volatile.Read(ref _datagramsSent);

    bool IUdpExchangeCounters.SawResponse => Volatile.Read(ref _sawResponse) != 0;

    /// <summary>
    /// Dials the flow's deferred-handshake connection: the same attempt loop, address cache, and
    /// before-the-SYN self-traffic registration the native association dials with, but the dial
    /// returns after writing the greeting (and the credential message, when configured) without
    /// reading a reply. The <c>createControl</c> test seam replaces the dial exactly as it does for
    /// the native path.
    /// </summary>
    internal static ValueTask<Socks5ControlConnection> DialAsync(Socks5Server server, Socks5UdpAssociationContext context, CancellationToken cancellationToken)
    {
        if (context.CreateControl is { } createControl) return createControl(server, cancellationToken);
        var selfTraffic = context.SelfTraffic;
        return Socks5ControlConnection.ConnectDeferredHandshakeAsync(
            server,
            cancellationToken,
            (local, remote) => selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Tcp,
                Endpoint.From(local.Address, checked((ushort)local.Port)),
                Endpoint.From(remote.Address, checked((ushort)remote.Port)))),
            addressCache: context.AddressCache);
    }

    /// <summary>
    /// Builds the per-flow transport over a connection the caller owns until this returns. The
    /// stream hand-off resets the per-attempt socket timeouts to infinite and the socket then goes
    /// non-blocking for the warm send path; both are legal before the deferred completion, whose
    /// reads are asynchronous and governed by neither (batch 3's documented order). The caller owns
    /// the connection and disposes it if this throws; on success the transport owns it.
    /// </summary>
    internal static Socks5UotTransport Create(Socks5ControlConnection control, int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameSize);
        var stream = control.GetUpstreamStream();
        if (stream is not NetworkStream network) throw new InvalidOperationException("The SOCKS5 control connection did not expose a network stream.");
        var socket = network.Socket;
        // Non-blocking mode keeps the send warm path synchronous: the kernel either takes the frame
        // inline or reports WouldBlock, which falls back to the overlapped send. The deferred
        // handshake's reads are asynchronous and unaffected by the mode.
        socket.Blocking = false;
        return new Socks5UotTransport(
            control,
            stream,
            socket,
            (IPEndPoint)socket.RemoteEndPoint!,
            (IPEndPoint)socket.LocalEndPoint!,
            maximumFrameSize);
    }

#pragma warning disable RCS1229 // Deliberate non-async warm entry (hot-path.md → warm-path-dispatch.md, "No async state machines on the steady-state path"): the steady-state send path must not pay an async state machine; a synchronous failure before the returned ValueTask is part of the warm contract and handled by the dispatcher.
    public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
#pragma warning restore RCS1229
    {
        // The gate is disposed last, so a sender that has not yet entered it must be refused here;
        // otherwise `_sendGate.WaitAsync` would observe the disposed gate. A single volatile read
        // keeps the warm shape allocation-free.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // I4 fail-closed: a connection whose handshake was rejected or whose stream died refuses the
        // datagram before the gate and before the socket, so the coordinator tears the flow down with
        // the recorded reason instead of writing to a stream no one is reading. One volatile read
        // plus a reference read; the exception itself is cold and already carries the death.
        if (Volatile.Read(ref _fault) is { } fault) throw fault;
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
            var written = EncodeFrame(destination, payload);
            int sent;
            try
            {
                sent = _socket.Send(_sendBuffer.AsSpan(0, written), SocketFlags.None);
            }
            catch (SocketException)
            {
                // A refused or would-block inline send completes asynchronously. The failed call
                // wrote nothing (one syscall either takes bytes or fails), so the remainder restarts
                // at zero; a genuine fault surfaces from the asynchronous send translated.
                return SendFrameAsync(0, written, cancellationToken);
            }

            if (sent < written) return SendFrameAsync(sent, written, cancellationToken);
            // Recorded only after the kernel accepted the whole frame, so the one-shot retention
            // class reads sends that actually went out (I3: one interlocked increment, no allocation).
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
        // Post-wait half of the warm entry's guard: a sender that waited through teardown is refused
        // here rather than writing to a closed connection.
        if (Volatile.Read(ref _disposed) != 0)
        {
            _sendGate.Release();
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (Volatile.Read(ref _fault) is { } fault)
        {
            _sendGate.Release();
            throw fault;
        }

        int written;
        try
        {
            written = EncodeFrame(destination, payload.Span);
        }
        catch
        {
            _sendGate.Release();
            throw;
        }

        // SendFrameAsync owns the gate's release on every path from here.
        await SendFrameAsync(0, written, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a frame whose inline send was refused, would-blocked, or partially accepted, and
    /// releases the send gate exactly once through every path. A stream fault after establishment is
    /// the flow's death typed: it is recorded and thrown as
    /// <see cref="UdpAssociationLostException"/> so no raw socket fault reaches the session (R4).
    /// </summary>
    private async ValueTask SendFrameAsync(int offset, int written, CancellationToken cancellationToken)
    {
        try
        {
            while (offset < written)
            {
                offset += await _socket.SendAsync(_sendBuffer.AsMemory(offset, written - offset), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }

            RecordDatagramSent();
        }
        catch (Exception fault) when (IsConnectionFault(fault, cancellationToken))
        {
            throw RecordFault(new UdpAssociationLostException("The flow's UoT connection ended while writing a datagram frame; the flow must be re-established.", fault));
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Encodes one datagram frame into the reusable send buffer: the first send prepends the magic
    /// <c>CONNECT</c> and the UoT request header and captures the flow's destination, later sends
    /// write the frame prefix and payload only. Connect mode binds the stream to one destination and
    /// frames a payload of at most <see cref="ushort"/> bytes, so a mismatched destination or an
    /// unframeable payload fails closed instead of mis-framing the stream.
    /// </summary>
    private int EncodeFrame(Endpoint destination, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue) throw new IOException("A UoT datagram payload cannot exceed the 16-bit frame length.");
        if (payload.Length > _maximumFrameSize) throw new IOException("A UoT datagram exceeded the transport send buffer.");
        if (_handshakeWritten)
        {
            if (destination != _destination) throw new IOException($"A UoT flow bound to {_destination} cannot send to {destination}.");
        }
        else
        {
            _destination = destination;
        }

        var offset = 0;
        if (!_handshakeWritten)
        {
            offset = Socks5Messages.WriteRequest(Socks5Command.Connect, UotCodec.MagicAddress, 0, _sendBuffer);
            if (!UotCodec.TryWriteRequestHeader(isConnect: true, destination.Address, destination.Port, _sendBuffer.AsSpan(offset), out var headerWritten))
            {
                throw new IOException("The UoT request header did not fit the transport send buffer.");
            }

            offset += headerWritten;
            // Gate-confined: no second sender can encode a frame before this one is fully written,
            // because the gate is held across the whole send including its asynchronous remainder.
            _handshakeWritten = true;
        }

        if (!UotCodec.TryWriteFrameHeader(checked((ushort)payload.Length), _sendBuffer.AsSpan(offset), out var frameWritten))
        {
            throw new IOException("The UoT frame prefix did not fit the transport send buffer.");
        }

        offset += frameWritten;
        payload.CopyTo(_sendBuffer.AsSpan(offset));
        offset += payload.Length;
        // Published after the whole frame is encoded: the receive loop reads it as the reply's
        // declared source, so it must never name a destination whose frame was not written.
        Volatile.Write(ref _destinationRecorded, true);
        return offset;
    }

    /// <summary>
    /// Receives one datagram frame. The first call consumes the replies the dial left unread
    /// (method selection, <c>[+ credential reply]</c>, then the CONNECT reply status); every call
    /// then reads the <c>u16be</c> frame prefix and the payload. A frame longer than
    /// <paramref name="buffer"/> is consumed to keep the stream aligned and reported
    /// <see cref="UdpTransportSkipReason.Oversized"/>; a zero-length frame is a legal empty
    /// datagram. A frame's payload is delivered with the flow's captured destination as its source,
    /// because connect mode carries no on-wire source at all.
    /// </summary>
    public async ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        // The deferred completion runs exactly once; a transport whose setup was already refused
        // reports the recorded refusal instead of re-entering the connection's one-shot completion.
        if (Volatile.Read(ref _established) == 0)
        {
            if (Volatile.Read(ref _fault) is { } recorded) throw recorded;
            await EstablishAsync(cancellationToken).ConfigureAwait(false);
        }

        int frameLength;
        try
        {
            await _stream.ReadExactlyAsync(_replyScratch.AsMemory(0, UotCodec.FrameHeaderSize), cancellationToken).ConfigureAwait(false);
            frameLength = BinaryPrimitives.ReadUInt16BigEndian(_replyScratch);
        }
        catch (Exception fault) when (IsConnectionFault(fault, cancellationToken))
        {
            throw RecordFault(new UdpAssociationLostException("The flow's UoT connection ended while reading a datagram frame; the flow must be re-established.", fault));
        }

        if (frameLength > buffer.Length)
        {
            await ConsumeOversizedFrameAsync(frameLength, cancellationToken).ConfigureAwait(false);
            return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.Oversized);
        }

        if (frameLength > 0)
        {
            try
            {
                await _stream.ReadExactlyAsync(buffer[..frameLength], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (IsConnectionFault(fault, cancellationToken))
            {
                throw RecordFault(new UdpAssociationLostException("The flow's UoT connection ended while reading a datagram frame; the flow must be re-established.", fault));
            }
        }

        // Fail closed on a frame no send ever framed: delivering it would declare a default source
        // the session would count as foreign. Unreachable on a flow that sent its first datagram.
        if (!Volatile.Read(ref _destinationRecorded)) return UdpTransportReceiveResult.Skipped(UdpTransportSkipReason.UnexpectedSource);
        // Only a frame that decoded proves this flow's replies come back: a skip is one anomaly, not
        // evidence that the server answered (design §5).
        RecordResponseReceived();
        var destination = _destination;
        return UdpTransportReceiveResult.Received(new UdpTransportDatagram(destination.Address, SourceDomain: null, destination.Port, buffer[..frameLength]));
    }

    /// <summary>
    /// Consumes the replies the pipelined dial left unread, in the order the server wrote them: the
    /// deferred handshake's method selection (and credential reply), then the CONNECT reply status.
    /// Every failure here is the setup being refused — a non-success status, EOF before the reply,
    /// the setup window elapsing — and is recorded and thrown as
    /// <see cref="UdpTransportHandshakeRejectedException"/> so the coordinator arms the setup
    /// cooldown instead of re-dialing once per client retransmit.
    /// </summary>
    private async ValueTask EstablishAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _control.CompleteDeferredHandshakeAsync(cancellationToken).ConfigureAwait(false);
            await ReadConnectReplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fault) when (fault is IOException or SocketException)
        {
            throw RecordFault(new UdpTransportHandshakeRejectedException(
                "The UoT CONNECT request was rejected, or the handshake ended before its reply.",
                fault));
        }

        Volatile.Write(ref _established, 1);
    }

    private async ValueTask ReadConnectReplyAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(s_connectReplyTimeout);
        try
        {
            await ReadConnectReplyCoreAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The UoT CONNECT reply did not arrive within the setup window.", new SocketException((int)SocketError.TimedOut));
        }
    }

    private async ValueTask ReadConnectReplyCoreAsync(CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_replyScratch.AsMemory(0, 5), cancellationToken).ConfigureAwait(false);
        // Validate only the VER/REP prefix: a success prefix is 5 bytes and carries no bound
        // address yet, so the full-reply parser must not be used before the body is read.
        if (!Socks5Messages.TryParseReplyPrefix(_replyScratch.AsSpan(0, 5), out var status))
        {
            throw new IOException("The UoT CONNECT reply prefix is invalid.");
        }

        if (status != 0)
        {
            throw new IOException($"The UoT CONNECT request failed: {Socks5Messages.DescribeReplyStatus(status)} (REP {status}).");
        }

        if (!Socks5Messages.TryGetReplyLength(_replyScratch.AsSpan(0, 5), out var totalLength))
        {
            throw new IOException("The UoT CONNECT reply carries an invalid bound address.");
        }

        // The reply body is consumed even though connect mode has no use for the bound address:
        // the stream's next bytes are datagram frames, so the reply must be read to its end.
        await _stream.ReadExactlyAsync(_replyScratch.AsMemory(5, totalLength - 5), cancellationToken).ConfigureAwait(false);
        if (Socks5Messages.TryParseReply(_replyScratch.AsSpan(0, totalLength), out _, out _, out _) != Socks5ReplyKind.Success)
        {
            throw new IOException("The UoT CONNECT reply is malformed.");
        }
    }

    /// <summary>
    /// Consumes a frame larger than the caller's receive window so the stream stays aligned, then
    /// reports the <see cref="UdpTransportSkipReason.Oversized"/> skip: the frame's declared length
    /// bounds the drain (at most 65535 bytes), and one pooled rent per anomaly keeps it off the
    /// steady-state path's allocation budget.
    /// </summary>
    private async ValueTask ConsumeOversizedFrameAsync(int length, CancellationToken cancellationToken)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await _stream.ReadExactlyAsync(scratch.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fault) when (IsConnectionFault(fault, cancellationToken))
        {
            throw RecordFault(new UdpAssociationLostException("The flow's UoT connection ended while discarding an oversized datagram frame.", fault));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Whether a fault is the flow's connection dying rather than this transport's own teardown: a
    /// stream/socket fault on a live, non-cancelled transport. The translation is what keeps a raw
    /// <see cref="SocketError.ConnectionReset"/> from reaching the session, whose receive loop
    /// treats that code as a single-datagram skip and would spin on a dead stream (R4).
    /// </summary>
    private bool IsConnectionFault(Exception fault, CancellationToken cancellationToken) =>
        Volatile.Read(ref _disposed) == 0
        && !cancellationToken.IsCancellationRequested
        && fault is IOException or SocketException;

    /// <summary>
    /// Records the flow's typed death once and returns the exception to throw, so every later send
    /// and receive reports the same fault the first observation carried.
    /// </summary>
    private Exception RecordFault(Exception fault)
    {
        if (Volatile.Read(ref _fault) is { } recorded) return recorded;
        Volatile.Write(ref _fault, fault);
        return fault;
    }

    /// <summary>
    /// Records one datagram this flow sent successfully. One interlocked increment, no allocation —
    /// this is the whole hot-path accounting addition (I3).
    /// </summary>
    private void RecordDatagramSent() => Interlocked.Increment(ref _datagramsSent);

    /// <summary>
    /// Records the first frame this flow decoded successfully. A plain volatile read short-circuits
    /// every later frame, so the flag costs one interlocked operation per flow, not per datagram.
    /// </summary>
    private void RecordResponseReceived()
    {
        if (Volatile.Read(ref _sawResponse) != 0) return;
        _ = Interlocked.Exchange(ref _sawResponse, 1);
    }

    /// <summary>
    /// Releases the connection — which owns the socket, the network stream, and the self-traffic
    /// tuple the dial's <c>onSocketReady</c> callback registered before the SYN — and the send gate,
    /// exactly once, through every path, even when the connection's release throws. The gate is
    /// disposed last: in-flight senders release it from their finally blocks as the closed
    /// connection faults their pending sends.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // A repeat dispose returns without re-running the teardown; the first caller owns it.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // The send gate is deliberately left undisposed. SemaphoreSlim.Dispose only frees the lazily
        // created WaitHandle (never requested here), while disposing it with waiters parked strands
        // those waits forever — and a stranded sender holds its caller's work lease. The guard
        // refuses new senders; a parked one is released by the connection's fault and refused by the
        // post-gate re-check in SendAfterGateAsync.
        await _control.DisposeAsync().ConfigureAwait(false);
    }
}
