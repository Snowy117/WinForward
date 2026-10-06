using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WinForward.E2E.Wire;

[StructLayout(LayoutKind.Auto)]
internal readonly struct FrameHeader
{
    internal FrameHeader(uint connectionId, ulong sequence, uint payloadLength)
    {
        ConnectionId = connectionId;
        Sequence = sequence;
        PayloadLength = payloadLength;
    }

    internal uint ConnectionId { get; }

    internal ulong Sequence { get; }

    internal uint PayloadLength { get; }

    internal int FrameLength => FrameCodec.HeaderSize + (int)PayloadLength + FrameCodec.TrailerSize;
}

internal enum FrameDecodeError
{
    None,
    BadMagic,
    BadLength,
    Truncated,
    BadChecksum,
}

internal static class FrameCodec
{
    private const uint Magic = 0x57464531u;
    internal const int HeaderSize = 28;
    internal const int TrailerSize = 4;
    private const uint MaxPayloadLength = 4u * 1024u * 1024u;
    internal const ulong CommandSequence = 0;

    private const int OffsetConnectionId = 4;
    private const int OffsetSequence = 8;
    private const int OffsetClientSendTicks = 16;
    private const int OffsetPayloadLength = 24;

    internal static int WriteFrameInPlace(Span<byte> frame, uint connectionId, ulong sequence, ulong clientSendTicks, int payloadLength)
    {
        WriteHeader(frame, connectionId, sequence, clientSendTicks, (uint)payloadLength);
        return FinishFrame(frame, payloadLength);
    }

    internal static void WriteHeader(Span<byte> destination, uint connectionId, ulong sequence, ulong clientSendTicks, uint payloadLength)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, Magic);
        BinaryPrimitives.WriteUInt32BigEndian(destination[OffsetConnectionId..], connectionId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[OffsetSequence..], sequence);
        BinaryPrimitives.WriteUInt64BigEndian(destination[OffsetClientSendTicks..], clientSendTicks);
        BinaryPrimitives.WriteUInt32BigEndian(destination[OffsetPayloadLength..], payloadLength);
    }

    internal static int FinishFrame(Span<byte> frame, int payloadLength)
    {
        var bodyLength = HeaderSize + payloadLength;
        BinaryPrimitives.WriteUInt32BigEndian(frame[bodyLength..], Crc32C.Compute(frame[..bodyLength]));
        return bodyLength + TrailerSize;
    }

    internal static bool TryReadHeader(ReadOnlySpan<byte> source, out FrameHeader header, out FrameDecodeError error)
    {
        header = default;
        error = FrameDecodeError.None;

        if (source.Length < HeaderSize)
        {
            error = FrameDecodeError.Truncated;
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(source) != Magic)
        {
            error = FrameDecodeError.BadMagic;
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(source[OffsetPayloadLength..]);
        if (payloadLength > MaxPayloadLength)
        {
            error = FrameDecodeError.BadLength;
            return false;
        }

        header = new FrameHeader(
            BinaryPrimitives.ReadUInt32BigEndian(source[OffsetConnectionId..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[OffsetSequence..]),
            payloadLength);
        return true;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> frame, out FrameHeader header, out ReadOnlySpan<byte> payload, out FrameDecodeError error)
    {
        payload = default;

        if (!TryReadHeader(frame, out header, out error))
        {
            return false;
        }

        if (frame.Length < header.FrameLength)
        {
            error = FrameDecodeError.Truncated;
            header = default;
            return false;
        }

        var bodyLength = HeaderSize + (int)header.PayloadLength;
        var expected = BinaryPrimitives.ReadUInt32BigEndian(frame[bodyLength..]);
        if (Crc32C.Compute(frame[..bodyLength]) != expected)
        {
            error = FrameDecodeError.BadChecksum;
            header = default;
            return false;
        }

        payload = frame.Slice(HeaderSize, (int)header.PayloadLength);
        return true;
    }
}
