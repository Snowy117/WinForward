using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class FrameStreamReaderTests
{
    private const uint ConnectionId = 0x0A0B0C0Du;
    private const ulong Sequence = 99;
    private const int PayloadLength = 300;

    [Fact]
    public async Task AFrameSplitIntoThreeChunksIsReadAsOneFrame()
    {
        var frame = BuildFrame();
        var feed = new ChunkFeed(frame[..5], frame[5..25], frame[25..]);
        var reader = new FrameStreamReader(feed.Read);

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(FrameReadStatus.Frame, status);
        Assert.Equal(3, feed.Reads);
        Assert.Equal(ConnectionId, reader.Header.ConnectionId);
        Assert.Equal(Sequence, reader.Header.Sequence);
        Assert.Equal(PayloadLength, reader.Payload.Length);
        Assert.True(reader.Payload.Span.SequenceEqual(frame.AsSpan(FrameCodec.HeaderSize, PayloadLength)));
        Assert.Equal(frame.Length, reader.Raw.Length);
    }

    [Fact]
    public async Task TwoFramesInOneChunkAreReadInOrder()
    {
        var first = BuildFrame();
        var second = BuildFrame(sequence: Sequence + 1);
        var feed = new ChunkFeed([.. first, .. second]);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.Frame, await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(Sequence, reader.Header.Sequence);
        Assert.Equal(FrameReadStatus.Frame, await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(Sequence + 1, reader.Header.Sequence);
    }

    [Fact]
    public async Task ABadChecksumSkipsTheFrameAndKeepsReading()
    {
        var broken = BuildFrame();
        broken[FrameCodec.HeaderSize] ^= 0xFF;
        var good = BuildFrame(sequence: Sequence + 1);
        var feed = new ChunkFeed([.. broken, .. good]);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.BadChecksum, await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(FrameReadStatus.Frame, await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(Sequence + 1, reader.Header.Sequence);
    }

    // A peer that closes inside a frame: the boundary is gone, so the reader reports the cut rather
    // than a close the peer never made.
    [Fact]
    public async Task EndOfStreamInsideAFrameIsReportedAsTruncated()
    {
        var frame = BuildFrame();
        var feed = new ChunkFeed(frame[..10]);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.Truncated, await reader.ReadAsync(CancellationToken.None));
    }

    // The same frame arriving in three chunks, the last of them cut short: the header decoded and the
    // payload was begun, which is the other half of "inside a frame" than a partial header.
    [Fact]
    public async Task EndOfStreamInsideAPayloadIsReportedAsTruncated()
    {
        var frame = BuildFrame();
        var feed = new ChunkFeed(frame[..5], frame[5..25], frame[25..^10]);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.Truncated, await reader.ReadAsync(CancellationToken.None));
    }

    // A whole frame followed by a cut one: the frames before the cut are still frames, and only the
    // frame the peer stopped inside is reported as truncated.
    [Fact]
    public async Task AWholeFrameFollowedByACutOneKeepsTheWholeFrame()
    {
        var first = BuildFrame();
        var second = BuildFrame(sequence: Sequence + 1);
        var feed = new ChunkFeed([.. first, .. second[..40]]);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.Frame, await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(Sequence, reader.Header.Sequence);
        Assert.Equal(FrameReadStatus.Truncated, await reader.ReadAsync(CancellationToken.None));
    }

    // Truncation is terminal, not a per-call accident: the reader is asked again by a loop that has
    // one more iteration coming and must not answer with a different status the second time.
    [Fact]
    public async Task TruncationIsReportedAgainOnTheNextRead()
    {
        var frame = BuildFrame();
        var feed = new ChunkFeed(frame[..10]);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.Truncated, await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(FrameReadStatus.Truncated, await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EndOfStreamAtAFrameBoundaryIsReportedAsEndOfStream()
    {
        var frame = BuildFrame();
        var feed = new ChunkFeed(frame);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.Frame, await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(FrameReadStatus.EndOfStream, await reader.ReadAsync(CancellationToken.None));
    }

    // The other side of the pair: a stream that closed before it carried anything is a clean close,
    // not a truncated frame.
    [Fact]
    public async Task AnEmptyStreamIsReportedAsEndOfStream()
    {
        var reader = new FrameStreamReader(new ChunkFeed().Read);

        Assert.Equal(FrameReadStatus.EndOfStream, await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BadMagicIsReportedAsBadMagic()
    {
        var frame = BuildFrame();
        frame[0] = 0x00;
        var feed = new ChunkFeed(frame);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.BadMagic, await reader.ReadAsync(CancellationToken.None));
    }

    private static byte[] BuildFrame(ulong sequence = Sequence)
    {
        var frame = new byte[FrameCodec.HeaderSize + PayloadLength + FrameCodec.TrailerSize];
        for (var index = 0; index < PayloadLength; index++)
        {
            frame[FrameCodec.HeaderSize + index] = (byte)((index % 251) + 1);
        }

        FrameCodec.WriteFrameInPlace(frame, ConnectionId, sequence, 4_242, PayloadLength);
        return frame;
    }

    private sealed class ChunkFeed(params byte[][] chunks)
    {
        private readonly Queue<byte[]> _chunks = new(chunks);

        internal int Reads { get; private set; }

        internal ValueTask<int> Read(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (_chunks.Count == 0)
            {
                return ValueTask.FromResult(0);
            }

            var chunk = _chunks.Dequeue();
            chunk.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(chunk.Length);
        }
    }
}
