using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Lanes;

/// <summary>
/// A lane's datagram wire endpoint: a connected UDP socket and the buffer one datagram is read into.
/// It reports what arrived and never judges whether the bytes were legal — the policy owns every
/// verdict and every counter — and it owns the socket's lifetime, because the engine disposes nothing
/// (D18.1).
/// </summary>
/// <remarks>
/// <para>
/// Reading into a buffer of its own, one <see cref="TruncationSlack"/> wider than the destination the
/// engine offers, is what makes an oversized datagram visible at all: .NET exposes no equivalent of
/// <c>MSG_TRUNC</c>, so a socket that received straight into the destination could not tell a message
/// that filled it from one the kernel cut off. A datagram longer than the destination is reported as
/// <see cref="LaneReceiveKind.Malformed"/> with <see cref="FrameDecodeError.Truncated"/>, and one that
/// fills even the wide buffer is reported the same way with its true length unknown — never silently
/// as a corrupt frame.
/// </para>
/// <para>
/// The wide buffer is allocated once, in the constructor, so the receive path allocates nothing.
/// </para>
/// <para>
/// Unlike the stream adapter this one observes nothing about its connect: a udp connect has no
/// handshake to time, and the record publishes connect facts under <c>tcp.*</c> only, so a failed open
/// reaches the record as <c>scheduleTruncated</c> and <c>laneShortfall</c> instead.
/// </para>
/// </remarks>
internal sealed class UdpLaneTransport : ILaneTransport
{
    /// <summary>
    /// How much wider than the engine's destination this transport's buffer is: one framed message's
    /// historical slack. It is what distinguishes "too big for the destination" (measured exactly) from
    /// "too big for the kernel to hand over whole" (the buffer's own length is all that is known).
    /// </summary>
    private const int TruncationSlack = 64;

    private readonly Socket _socket;
    private readonly EndPoint _endPoint;
    private readonly byte[] _receiveBuffer;

    /// <summary>
    /// Takes ownership of <paramref name="socket"/>: disposing this transport closes it.
    /// <paramref name="datagramBytes"/> is the largest message the engine can take.
    /// </summary>
    internal UdpLaneTransport(Socket socket, EndPoint endPoint, int datagramBytes)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(endPoint);

        _socket = socket;
        _endPoint = endPoint;
        _receiveBuffer = new byte[datagramBytes + TruncationSlack];
    }

    public async ValueTask<LaneOpenResult> OpenAsync(CancellationToken cancellationToken) =>
        await SocketOps.TryConnectAsync(_socket, _endPoint, cancellationToken).ConfigureAwait(false);

    public async ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ValueTask<int> send;
        try
        {
            send = _socket.SendAsync(payload, SocketFlags.None, cancellationToken);
        }
        catch (SocketException exception)
        {
            // A connected datagram socket surfaces a previous datagram's icmp error here; the next send
            // may well succeed, so the failure is answered rather than thrown at the engine.
            return new LaneSendResult(Accepted: false, WouldBlock: false, Error: exception.Message);
        }

        // A ReadOnlyMemory payload selects one of the three Memory overloads, and every one of them
        // allocates nothing when the socket takes the datagram inline; it is the byte[]/ArraySegment
        // overload a byte[] argument binds to that allocates a Task per call (measured 72 B). The
        // real-transport allocation gate is what keeps this overload, and the parameter type that
        // selects it, in place.
        // The transport's own pre-await reading of the same property the engine reads off the returned
        // task (audit §9.6); the engine takes the union of the two, so neither side can double-count it.
        var wouldBlock = !send.IsCompleted;
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            return new LaneSendResult(Accepted: false, WouldBlock: wouldBlock, Error: exception.Message);
        }

        return new LaneSendResult(Accepted: true, WouldBlock: wouldBlock, Error: null);
    }

    public async ValueTask<LaneReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        int received;
        try
        {
            received = await _socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // The socket is gone: the lane has no further arrivals to report.
            return new LaneReceiveResult(LaneReceiveKind.IoError, 0);
        }

        if (received > destination.Length)
        {
            // Longer than the engine can take, so it cannot be handed up as a message; the datagram is
            // dropped and the lane continues, exactly as one corrupt datagram never ended a udp lane.
            return new LaneReceiveResult(LaneReceiveKind.Malformed, 0, FrameDecodeError.Truncated);
        }

        _receiveBuffer.AsSpan(0, received).CopyTo(destination.Span);
        return new LaneReceiveResult(LaneReceiveKind.Payload, received);
    }

    public void Dispose() => _socket.Dispose();
}
