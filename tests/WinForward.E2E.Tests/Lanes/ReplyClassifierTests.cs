using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The one reply ladder the three udp arms share (D18.3), as a pure function: every fact here builds
/// bytes and reads a verdict, with no book, no counter and no arm involved. The ordering facts are the
/// load-bearing ones — the connection id is checked before the filler, and a bad checksum whose header
/// still decodes keeps its sequence — because that is where the three hand-written ladders had drifted.
/// </summary>
public sealed class ReplyClassifierTests
{
    private const uint ConnectionId = 0x7100_0001u;
    private const int PayloadBytes = 24;

    [Fact]
    public void ALegalFrameForThisConnectionArrivesWithItsSequenceAndPayloadLength()
    {
        var verdict = ReplyClassifier.Classify(BuildFrame(ConnectionId, 42), ConnectionId);

        Assert.Equal(ReplyKind.Arrived, verdict.Kind);
        Assert.Equal(42, verdict.Sequence);
        Assert.Equal(PayloadBytes, verdict.PayloadBytes);
        Assert.Equal(FrameDecodeError.None, verdict.Error);
    }

    [Fact]
    public void AnotherFlowsConnectionIdIsRejectedBeforeItsOwnFillerIsRead()
    {
        // A well-formed frame from another flow is internally consistent -- its filler validates
        // against its own connection id -- so it reaches the id check whichever order the two checks
        // run in.
        var foreign = BuildFrame(ConnectionId + 1, 7);

        var verdict = ReplyClassifier.Classify(foreign, ConnectionId);

        Assert.Equal(ReplyKind.ForeignConnection, verdict.Kind);
        Assert.Equal(7, verdict.Sequence);

        // The order is only readable off a datagram that is foreign *and* fails its own filler: with
        // the connection id checked first it lands in the foreign bucket, and a filler check ordered
        // first would file another flow's wreckage among this lane's own corrupt datagrams.
        var broken = new FrameBuffer(PayloadBytes);
        broken.Build(ConnectionId + 1, 9, 0);
        broken.FlipPayloadByte(PayloadBytes, 0);
        var brokenLength = broken.RecomputeChecksum(PayloadBytes);

        var doublyBroken = ReplyClassifier.Classify(broken.Memory.Span[..brokenLength], ConnectionId);

        Assert.Equal(ReplyKind.ForeignConnection, doublyBroken.Kind);
        Assert.Equal(9, doublyBroken.Sequence);
    }

    [Fact]
    public void AFillerMismatchOnThisConnectionIsCorruptAndStillCarriesTheSequence()
    {
        var frame = new FrameBuffer(PayloadBytes);
        frame.Build(ConnectionId, 11, 0);
        frame.FlipPayloadByte(PayloadBytes, 0);
        var length = frame.RecomputeChecksum(PayloadBytes);

        var verdict = ReplyClassifier.Classify(frame.Memory.Span[..length], ConnectionId);

        Assert.Equal(ReplyKind.Corrupt, verdict.Kind);
        Assert.Equal(11, verdict.Sequence);
        Assert.Equal(PayloadBytes, verdict.PayloadBytes);
    }

    [Fact]
    public void ABadChecksumWithAReadableHeaderKeepsTheSequenceItClaims()
    {
        var frame = new FrameBuffer(PayloadBytes);
        var length = frame.Build(ConnectionId, 13, 0);
        frame.FlipPayloadByte(PayloadBytes, 0);

        var verdict = ReplyClassifier.Classify(frame.Memory.Span[..length], ConnectionId);

        Assert.Equal(ReplyKind.CorruptKnownSequence, verdict.Kind);
        Assert.Equal(13, verdict.Sequence);
        Assert.Equal(FrameDecodeError.BadChecksum, verdict.Error);
    }

    [Fact]
    public void BadMagicAndImpossibleLengthsAreUndecodable()
    {
        var noMagic = new byte[FrameCodec.HeaderSize];

        var magic = ReplyClassifier.Classify(noMagic, ConnectionId);

        Assert.Equal(ReplyKind.Undecodable, magic.Kind);
        Assert.Equal(FrameDecodeError.BadMagic, magic.Error);

        var tooLong = new byte[FrameCodec.HeaderSize];
        FrameCodec.WriteHeader(tooLong, ConnectionId, 1, 0, FrameCodec.MaxPayloadLength + 1);

        var length = ReplyClassifier.Classify(tooLong, ConnectionId);

        Assert.Equal(ReplyKind.Undecodable, length.Kind);
        Assert.Equal(FrameDecodeError.BadLength, length.Error);
    }

    [Fact]
    public void ADatagramTooShortToHoldAHeaderIsUndecodable()
    {
        var verdict = ReplyClassifier.Classify(new byte[3], ConnectionId);

        Assert.Equal(ReplyKind.Undecodable, verdict.Kind);
        Assert.Equal(FrameDecodeError.Truncated, verdict.Error);
    }

    [Fact]
    public void TheWasSentAnswerIsWhatTurnsAnArrivalIntoAnUnmatchedReply()
    {
        var arrived = ReplyClassifier.Classify(BuildFrame(ConnectionId, 5), ConnectionId);

        Assert.Equal(ReplyKind.Arrived, arrived.Resolve(wasSent: true).Kind);
        Assert.Equal(ReplyKind.Unmatched, arrived.Resolve(wasSent: false).Kind);

        // ...and it only ever rewrites an arrival: a datagram the classifier already judged keeps its
        // verdict whatever the sender's book would say about its sequence.
        var corrupt = arrived with { Kind = ReplyKind.Corrupt, Sequence = 999 };
        Assert.Equal(ReplyKind.Corrupt, corrupt.Resolve(wasSent: false).Kind);
    }

    [Fact]
    public void ClassifyingTwiceYieldsTheSameVerdict()
    {
        // Purity in the small: the classifier reads the bytes it is handed and owns nothing that a
        // second call could move.
        var frame = BuildFrame(ConnectionId, 64);

        var first = ReplyClassifier.Classify(frame, ConnectionId);
        var second = ReplyClassifier.Classify(frame, ConnectionId);

        Assert.Equal(first, second);
    }

    private static byte[] BuildFrame(uint connectionId, ulong sequence)
    {
        var frame = new FrameBuffer(PayloadBytes);
        var length = frame.Build(connectionId, sequence, 0);
        return frame.Memory[..length].ToArray();
    }
}
