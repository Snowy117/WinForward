using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// The latency arm's tcp book: what came back on one lane and what is still owed, plus the connect
/// facts the lane body measured. Every counter is a property and none is named after a
/// <see cref="LaneCounts"/> member: the send side belongs to the engine, this side to the
/// policy.
/// </summary>
/// <remarks>
/// The pending book is a plain FIFO of the instants requests were wanted for, because a tcp reply is
/// matched by arrival order and never by the sequence it echoes. Its size is the in-flight
/// count: an accepted send enters both and a matched reply leaves both. Single-writer by contract:
/// <see cref="LatencyTcpPolicy.Settle"/> on the send thread is the only writer, and the
/// receive thread only enqueues settlements.
/// </remarks>
internal sealed class LatencyTcpState
{
    private readonly Queue<long> _pending = new();

    /// <summary>0 or 1, set by the lane body itself, exactly as <see cref="UdpLatencyState.Started"/>.</summary>
    internal long Started { get; set; }

    /// <summary>Every framed message that arrived, which for this lane is one reply per request until the book runs out.</summary>
    internal long Received { get; set; }

    /// <summary>Frames the transport could not hand up as a message: one bad checksum, and the lane kept reading.</summary>
    internal long Corrupt { get; set; }

    /// <summary>A transport failure that ended the lane's receive loop: a socket error, or a stream whose frame boundary was lost.</summary>
    internal long ProtocolErrors { get; set; }

    /// <summary>The peer closed its side of the stream; the lane stops reading.</summary>
    internal long RemoteClosed { get; set; }

    /// <summary>Replies with no request left to match, which arrival-order matching cannot attribute to a sequence.</summary>
    internal long UnmatchedReplies { get; set; }

    /// <summary>Requests this socket sent that no reply has consumed: the window's occupancy.</summary>
    internal long InFlight { get; private set; }

    /// <summary>Requests still owed when the lane stopped: the pending book's size, and one half of the contract's <c>outstandingAtTeardown</c>.</summary>
    internal long Pending => _pending.Count;

    internal long ConnectSamples { get; private set; }

    internal long ConnectFailures { get; private set; }

    internal long ConnectTicks { get; private set; }

    /// <summary>Takes one accepted send into the book: its instant waits there until a reply arrives.</summary>
    internal void Book(long intendedTicks)
    {
        _pending.Enqueue(intendedTicks);
        InFlight++;
    }

    /// <summary>
    /// Trades this lane's WasSent answer for the request the oldest unanswered reply belongs to: the
    /// instants are matched in the order the replies arrive, exactly as the sequence-less tcp ladder has
    /// always matched them.
    /// </summary>
    internal bool TryConsume(out long intendedTicks)
    {
        if (_pending.Count == 0)
        {
            intendedTicks = 0;
            return false;
        }

        intendedTicks = _pending.Dequeue();
        return true;
    }

    /// <summary>Releases the slot a consumed request held.</summary>
    internal void ReleaseInFlight() => InFlight--;

    /// <summary>
    /// Books one connect attempt from the lane's own transport. The tcp lane is the only lane that
    /// publishes connect facts, because it is the only one whose connect is a handshake worth timing.
    /// </summary>

    internal void BookConnect(bool ok, long ticks)
    {
        if (ok)
        {
            ConnectSamples++;
            ConnectTicks += ticks;
            return;
        }

        ConnectFailures++;
    }
}

/// <summary>
/// One receive outcome waiting for the send thread. The stream's framing has already been
/// read by the transport, so there is no sequence here to carry: a tcp reply is matched by arrival
/// order, and the only decision left is which counter the outcome moves.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TcpSettlement(LaneReceiveKind Kind, long ReceivedTicks);

/// <summary>
/// The latency arm's tcp policy: window admission, frame construction and the reply book, matched in
/// arrival order because a tcp stream cannot tell a reply's sequence from any other frame's. It holds
/// the window the engine deliberately does not, and frames straight into the engine's buffer.
/// </summary>
/// <remarks>
/// The receive thread only classifies and enqueues; every counter and every round-trip
/// sample moves in <see cref="Settle"/>, on the send thread.
/// </remarks>
internal sealed class LatencyTcpPolicy : ILanePolicy
{
    private readonly LatencyTcpState _state;
    private readonly LogHistogram _rtt;
    private readonly ConcurrentQueue<TcpSettlement> _settlements = new();
    private readonly uint _connectionId;
    private readonly int _payloadBytes;
    private readonly int _window;

    internal LatencyTcpPolicy(LatencyTcpState state, uint connectionId, int payloadBytes, int window, LogHistogram rtt)
    {
        _state = state;
        _connectionId = connectionId;
        _payloadBytes = payloadBytes;
        _window = window;
        _rtt = rtt;
    }

    public bool IsDrained => _settlements.IsEmpty;

    public bool BookEmpty => _state is { InFlight: 0, Pending: 0 };

    public LaneSlotDecision BuildRequest(long sequence, long intendedTicks, Span<byte> destination, out int length)
    {
        if (_state.InFlight >= _window)
        {
            length = 0;
            return LaneSlotDecision.Defer;
        }

        Filler.Fill(_connectionId, (ulong)sequence, destination.Slice(FrameCodec.HeaderSize, _payloadBytes));
        length = FrameCodec.WriteFrameInPlace(destination, _connectionId, (ulong)sequence, (ulong)intendedTicks, _payloadBytes);
        return LaneSlotDecision.Send;
    }

    public void OnSent(long sequence, long intendedTicks, in LaneSendResult result)
    {
        // A refused send never enters the book: there is no reply to expect on that sequence, and no
        // slot of the window to hold.
        if (result.Accepted)
        {
            _state.Book(intendedTicks);
        }
    }

    // ReSharper disable once UnusedParameter.Global // The payload is the seam's record of what arrived; this lane matches replies by arrival order and reads no byte of it, which is not evidence that the parameter is unused.
    public void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long receivedTicks) =>
        _settlements.Enqueue(new TcpSettlement(result.Kind, receivedTicks));

    public void Settle(long nowTicks)
    {
        // The contract is to empty the queue, not to book one item.
        while (_settlements.TryDequeue(out var settlement))
        {
            switch (settlement.Kind)
            {
                case LaneReceiveKind.Payload:
                    _state.Received++;
                    if (_state.TryConsume(out var intended))
                    {
                        _rtt.Record(Clock.ToNanoseconds(settlement.ReceivedTicks - intended));
                        _state.ReleaseInFlight();
                    }
                    else
                    {
                        // Arrival order ran out of requests: the reply belongs to none, so it releases no
                        // slot of the window even though it arrived as a well-formed frame.
                        _state.UnmatchedReplies++;
                    }

                    break;

                case LaneReceiveKind.EndOfStream:
                    _state.RemoteClosed++;
                    break;

                case LaneReceiveKind.Malformed:
                    _state.Corrupt++;
                    break;

                case LaneReceiveKind.IOError:
                    _state.ProtocolErrors++;
                    break;

                default:
                    // Every kind the seam defines is named above, so a future one has to be wired here
                    // rather than booked by whichever arm happened to be last.
                    throw new InvalidOperationException($"Unhandled receive kind '{settlement.Kind}'.");
            }
        }
    }
}
