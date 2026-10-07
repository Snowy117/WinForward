using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Lanes;

/// <summary>
/// A lane's TCP wire endpoint: the socket, the connect, the lane's one command frame and the frame
/// reader that turns the stream back into messages. It reports what it read and never judges whether
/// the bytes were legal — the policy owns every verdict and every counter — and it owns the socket's
/// lifetime, because the engine disposes nothing (D18.1).
/// </summary>
/// <remarks>
/// The reader hands up a frame's payload, so a message is copied once into the engine's destination
/// buffer: the stream has to be reassembled in a buffer of the reader's own before a frame boundary is
/// even known, and the copy is what lets the seam hand the policy a span that outlives no call. It
/// allocates nothing per message.
/// </remarks>
internal sealed class TcpLaneTransport : ILaneTransport
{
    private readonly Socket _socket;
    private readonly EndPoint _endPoint;
    private readonly uint _connectionId;
    private readonly FrameStreamReader _reader;

    /// <summary>
    /// Takes ownership of <paramref name="socket"/>: disposing this transport closes it.
    /// </summary>
    internal TcpLaneTransport(Socket socket, EndPoint endPoint, uint connectionId)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(endPoint);

        _socket = socket;
        _endPoint = endPoint;
        _connectionId = connectionId;
        _reader = new FrameStreamReader(socket);
    }

    /// <summary>Whether the lane reached its target and told it how to answer.</summary>
    internal bool ConnectOk { get; private set; }

    /// <summary>
    /// How long the successful connect took, in <see cref="Clock"/> ticks. The lane's connect sample is
    /// published as a mean, so the connect is timed on its own and the command frame below stays out of
    /// it, exactly as the per-lane connect has always been timed.
    /// </summary>
    internal long ConnectTicks { get; private set; }

    public async ValueTask<LaneOpenResult> OpenAsync(CancellationToken cancellationToken)
    {
        var begin = Clock.Now;
        var open = await SocketOps.TryConnectAsync(_socket, _endPoint, cancellationToken).ConfigureAwait(false);
        if (!open.Ok)
        {
            return open;
        }

        ConnectTicks = Clock.Now - begin;
        try
        {
            // The command frame is the lane's handshake, not one of its requests: it carries the command
            // sequence and stays out of every request counter. A target that never learns the mode would
            // answer frames the lane cannot score, so a handshake that fails is an open failure.
            await SocketOps.SendCommandAsync(_socket, _connectionId, TcpMode.Clean, 0, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            return new LaneOpenResult(Ok: false, Error: exception.Message);
        }

        ConnectOk = true;
        return open;
    }

    public async ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ValueTask<int> send;
        try
        {
            send = _socket.SendAsync(payload, SocketFlags.None, cancellationToken);
        }
        catch (SocketException exception)
        {
            // A socket error thrown before the await is the same failure as one thrown by it: the
            // request is lost, the lane is not.
            return new LaneSendResult(Accepted: false, WouldBlock: false, Error: exception.Message);
        }

        // A ReadOnlyMemory payload selects one of the three Memory overloads, and every one of them
        // allocates nothing when the socket takes the message inline; it is the byte[]/ArraySegment
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
        var status = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        // A socket error thrown by the read is the reader's own terminal outcome and is reported as
        // such; cancellation and disposal are the engine's to absorb, so they are not caught here.
        switch (status)
        {
            case FrameReadStatus.Frame:
                var payload = _reader.Payload;
                if (payload.Length > destination.Length)
                {
                    // The engine's receive buffer is sized from the plan and holds a whole frame, so a
                    // message that does not fit is a configuration error, not a peer's doing. The lane
                    // stops rather than measuring a run whose replies were silently dropped.
                    return new LaneReceiveResult(LaneReceiveKind.IoError, 0, FrameDecodeError.Truncated);
                }

                payload.Span.CopyTo(destination.Span);
                return new LaneReceiveResult(LaneReceiveKind.Payload, payload.Length);

            case FrameReadStatus.EndOfStream:
                return new LaneReceiveResult(LaneReceiveKind.EndOfStream, 0);

            case FrameReadStatus.BadChecksum:
                // One frame whose bytes do not match its checksum: the boundary was still readable, so
                // the next frame can be found and the lane continues.
                return new LaneReceiveResult(LaneReceiveKind.Malformed, 0, FrameDecodeError.BadChecksum);

            case FrameReadStatus.BadMagic:
            case FrameReadStatus.BadLength:
                // The stream's frame boundary is gone and no later message can be framed, so this is
                // terminal rather than one bad message (D18.6 #3).
                return new LaneReceiveResult(LaneReceiveKind.IoError, 0, status == FrameReadStatus.BadMagic ? FrameDecodeError.BadMagic : FrameDecodeError.BadLength);

            case FrameReadStatus.Truncated:
                // The same family as the two above -- the peer closed inside a frame, so the boundary
                // is gone -- and terminal for the same reason. It is not EndOfStream: a stream that
                // ends mid-frame is not the clean close the lane's modes are measured against, and
                // the policy books the two as different facts.
                return new LaneReceiveResult(LaneReceiveKind.IoError, 0, FrameDecodeError.Truncated);

            default:
                // Every status the reader defines is named above, so a new reader outcome has to be
                // mapped here rather than absorbed by whichever arm happened to be last.
                return new LaneReceiveResult(LaneReceiveKind.IoError, 0);
        }
    }

    public void Dispose() => _socket.Dispose();
}
