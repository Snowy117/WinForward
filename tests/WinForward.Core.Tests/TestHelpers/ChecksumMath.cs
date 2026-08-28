using System.Buffers.Binary;
using WinForward.Protocols;

namespace WinForward.Core.Tests;

/// <summary>
/// Shared 16-bit internet checksum math for test frame construction and validation: every frame
/// builder and checksum validator folds sums identically through these primitives.
/// </summary>
internal static class ChecksumMath
{
    /// <summary>Sums big-endian 16-bit words, padding a trailing odd byte into the high octet.</summary>
    public static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < data.Length; index += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        if (index < data.Length) sum += (uint)data[index] << 8;
        return sum;
    }

    /// <summary>Folds carry bits until the sum fits in 16 bits (no complement).</summary>
    public static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)sum;
    }

    /// <summary>Finished (complemented) checksum: valid data folds to zero.</summary>
    public static ushort Finish(uint sum) => (ushort)~Fold(sum);

    public static void SetIpv4HeaderChecksum(byte[] frame)
    {
        var headerLength = (frame[14] & 0x0f) * 4;
        frame[24] = 0;
        frame[25] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(24, 2), PacketChecksums.InternetChecksum(frame.AsSpan(14, headerLength)));
    }

    public static void SetIpv4TcpChecksum(byte[] frame, int tcp, int tcpLength)
    {
        frame[tcp + 16] = 0;
        frame[tcp + 17] = 0;
        var sum = Sum(frame.AsSpan(26, 4)) + Sum(frame.AsSpan(30, 4)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcp, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 16, 2), Finish(sum));
    }

    public static void SetIpv6TcpChecksum(byte[] frame, int tcp, int tcpLength)
    {
        frame[tcp + 16] = 0;
        frame[tcp + 17] = 0;
        var sum = Sum(frame.AsSpan(22, 16)) + Sum(frame.AsSpan(38, 16)) + 6u + (uint)tcpLength + Sum(frame.AsSpan(tcp, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcp + 16, 2), Finish(sum));
    }
}
