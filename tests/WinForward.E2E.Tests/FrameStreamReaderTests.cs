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

    // E3 D9: 将改为 Truncated —— 冻结当前行为，使那次改动表现为测试差异而不是静默的行为变化。
    [Fact]
    public async Task EndOfStreamInsideAFrameIsReportedAsEndOfStream()
    {
        var frame = BuildFrame();
        var feed = new ChunkFeed(frame[..10]);
        var reader = new FrameStreamReader(feed.Read);

        Assert.Equal(FrameReadStatus.EndOfStream, await reader.ReadAsync(CancellationToken.None));
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
