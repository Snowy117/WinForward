using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class FrameCodecTests
{
    private const int PayloadLength = 300;

    [Fact]
    public void AFrameRoundTripsThroughTheDecoder()
    {
        var frame = BuildFrame(0x0A0B0C0Du, 4_000_000_001, PayloadLength);

        Assert.True(FrameCodec.TryDecode(frame, out var header, out var payload, out var error));
        Assert.Equal(FrameDecodeError.None, error);
        Assert.Equal(0x0A0B0C0Du, header.ConnectionId);
        Assert.Equal(4_000_000_001UL, header.Sequence);
        Assert.Equal((uint)PayloadLength, header.PayloadLength);
        Assert.Equal(PayloadLength, payload.Length);
        Assert.True(payload.SequenceEqual(frame.AsSpan(FrameCodec.HeaderSize, PayloadLength)));
    }

    [Fact]
    public void BadMagicIsReportedAsBadMagic()
    {
        var frame = BuildFrame(1u, 1, PayloadLength);
        frame[0] ^= 0xFF;

        Assert.False(FrameCodec.TryDecode(frame, out _, out _, out var error));
        Assert.Equal(FrameDecodeError.BadMagic, error);
    }

    [Fact]
    public void APayloadLengthAboveTheCapIsReportedAsBadLength()
    {
        var header = new byte[FrameCodec.HeaderSize];
        FrameCodec.WriteHeader(header, 1u, 1, 0, (4u * 1024u * 1024u) + 1u);

        Assert.False(FrameCodec.TryDecode(header, out _, out _, out var error));
        Assert.Equal(FrameDecodeError.BadLength, error);
    }

    [Fact]
    public void AShortHeaderIsReportedAsTruncated()
    {
        Assert.False(FrameCodec.TryDecode(new byte[FrameCodec.HeaderSize - 1], out _, out _, out var error));
        Assert.Equal(FrameDecodeError.Truncated, error);
    }

    [Fact]
    public void AFrameCutShortOfItsTrailerIsReportedAsTruncated()
    {
        var frame = BuildFrame(1u, 1, PayloadLength);

        Assert.False(FrameCodec.TryDecode(frame.AsSpan(0, frame.Length - 10), out _, out _, out var error));
        Assert.Equal(FrameDecodeError.Truncated, error);
    }

    [Fact]
    public void AChangedPayloadByteIsReportedAsBadChecksum()
    {
        var frame = BuildFrame(1u, 1, PayloadLength);
        frame[FrameCodec.HeaderSize + 5] ^= 0x01;

        Assert.False(FrameCodec.TryDecode(frame, out _, out _, out var error));
        Assert.Equal(FrameDecodeError.BadChecksum, error);
    }

    // The trailer is located from the header's length, never the span end, so a reader handing over
    // its whole buffer with frames behind this one still decodes this one.
    [Fact]
    public void BytesAfterTheFrameAreAccepted()
    {
        var frame = BuildFrame(0x0A0B0C0Du, 77, PayloadLength);
        var withTail = new byte[frame.Length + 32];
        frame.CopyTo(withTail, 0);

        Assert.True(FrameCodec.TryDecode(withTail, out var header, out var payload, out var error));
        Assert.Equal(FrameDecodeError.None, error);
        Assert.Equal(77UL, header.Sequence);
        Assert.Equal(PayloadLength, payload.Length);
    }

    [Fact]
    public void WriteFrameInPlaceReturnsTheWholeFrameLength()
    {
        var frame = new byte[FrameCodec.HeaderSize + PayloadLength + FrameCodec.TrailerSize];

        Assert.Equal(frame.Length, FrameCodec.WriteFrameInPlace(frame, 1u, 1, 2, PayloadLength));
        Assert.True(FrameCodec.TryReadHeader(frame, out var header, out var error));
        Assert.Equal(FrameDecodeError.None, error);
        Assert.Equal(PayloadLength, header.FrameLength - FrameCodec.HeaderSize - FrameCodec.TrailerSize);
    }

    private static byte[] BuildFrame(uint connectionId, ulong sequence, int payloadLength)
    {
        var frame = new byte[FrameCodec.HeaderSize + payloadLength + FrameCodec.TrailerSize];
        for (var index = 0; index < payloadLength; index++)
        {
            frame[FrameCodec.HeaderSize + index] = (byte)((index % 251) + 1);
        }

        Assert.Equal(frame.Length, FrameCodec.WriteFrameInPlace(frame, connectionId, sequence, 12_345, payloadLength));
        return frame;
    }
}
