using System.Runtime.InteropServices;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Lanes;

/// <summary>
/// What one datagram turned out to be, in the vocabulary the three UDP ladders share. The verdict is
/// about the wire format and this lane's connection id only; whether the sequence was ever sent is the
/// caller's question, because only the caller owns a send book (see <see cref="ReplyKind.Unmatched"/>).
/// </summary>
internal enum ReplyKind
{
    /// <summary>
    /// A frame that decoded, carries this socket's connection id and passes its own payload filler: a
    /// reply this lane may book. Whether it consumed a request is the caller's next question.
    /// </summary>
    Arrived = 0,

    /// <summary>
    /// A frame that decoded and matched the connection id, but whose payload does not match the filler
    /// the sequence derives. Corrupt bytes from this lane, not another flow's.
    /// </summary>
    Corrupt = 1,

    /// <summary>
    /// A frame that failed its checksum while its header still decoded, so the sequence it claims is
    /// known. One message could not be read; the lane continues.
    /// </summary>
    CorruptKnownSequence = 2,

    /// <summary>
    /// A datagram carrying a connection id this socket never used. The connection id is checked before
    /// the filler, as every arm has always ordered the two: it is what decides a datagram that is both
    /// foreign and fails its own filler, which is booked as this and not as an ordinary corrupt one.
    /// </summary>
    ForeignConnection = 3,

    /// <summary>
    /// A reply that corresponds to no request this lane sent. <see cref="ReplyClassifier.Classify"/>
    /// cannot answer that question — it holds no book and has no side effects — so the ladder's own
    /// WasSent step produces this verdict from the book it owns: the tracker's sent bitmap in the
    /// loss and mix arms, the pending book in the latency arm.
    /// </summary>
    Unmatched = 4,

    /// <summary>
    /// A datagram that could not be decoded at all as a frame: no magic, an impossible length, or too
    /// few bytes. Its sequence is unknown, so no sent datagram can be attributed to it.
    /// </summary>
    Undecodable = 5,
}

/// <summary>
/// One classified reply. <see cref="Sequence"/> and <see cref="PayloadBytes"/> are only meaningful when
/// the datagram got far enough to have them, and <see cref="Error"/> carries the decoder's own reason
/// for the two unreadable verdicts so a ladder can tell a bad checksum from a bad magic instead of
/// re-decoding the datagram to find out.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct ReplyVerdict(ReplyKind Kind, long Sequence, int PayloadBytes, FrameDecodeError Error)
{
    /// <summary>
    /// The same verdict under the ladder's WasSent answer: a reply whose sequence this lane never sent
    /// was not an arrival on it, whatever the wire format said.
    /// </summary>
    internal ReplyVerdict Resolve(bool wasSent) =>
        Kind == ReplyKind.Arrived && !wasSent ? this with { Kind = ReplyKind.Unmatched } : this;
}

/// <summary>
/// The reply ladder the three UDP arms share, as a pure function: decode, connection id, filler — and
/// nothing else. It takes no tracker, keeps no book, counts nothing and enqueues nothing, so a unit
/// test can drive it as well as a receive loop and each call site keeps its own WasSent step visible.
/// </summary>
internal static class ReplyClassifier
{
    /// <summary>
    /// Classifies one datagram against the connection id this lane's socket uses. The caller books the
    /// verdict, which is why nothing here touches a counter: the same ladder runs on the latency arm's
    /// receive thread (classify-and-enqueue only) and in the loss and mix arms' drain loops.
    /// </summary>
    internal static ReplyVerdict Classify(ReadOnlySpan<byte> datagram, uint expectedConnectionId)
    {
        if (!FrameCodec.TryDecode(datagram, out var header, out var payload, out var error))
        {
            // A bad checksum leaves the header readable, so the sequence it claims is known: the loss
            // and mix arms book those against that sequence rather than as an unattributable corrupt
            // datagram, and the latency arm folds both cases into its own corrupt counter.
            return error == FrameDecodeError.BadChecksum && FrameCodec.TryReadHeader(datagram, out var partial, out _)
                ? new ReplyVerdict(ReplyKind.CorruptKnownSequence, (long)partial.Sequence, 0, error)
                : new ReplyVerdict(ReplyKind.Undecodable, 0, 0, error);
        }

        // Connection id before filler, as every arm has always ordered it. A well-formed foreign
        // datagram validates against its own filler and reaches this check under either order, so the
        // order decides the doubly broken one -- foreign and failing its own filler -- which belongs in
        // the foreign bucket rather than among this lane's own corrupt datagrams.
        if (header.ConnectionId != expectedConnectionId)
        {
            return new ReplyVerdict(ReplyKind.ForeignConnection, (long)header.Sequence, payload.Length, FrameDecodeError.None);
        }

        return Filler.Matches(header.ConnectionId, header.Sequence, payload)
            ? new ReplyVerdict(ReplyKind.Arrived, (long)header.Sequence, payload.Length, FrameDecodeError.None)
            : new ReplyVerdict(ReplyKind.Corrupt, (long)header.Sequence, payload.Length, FrameDecodeError.None);
    }
}
