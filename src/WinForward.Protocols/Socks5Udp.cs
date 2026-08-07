using System.Buffers.Binary;
using System.Net;
using WinForward.Core;

namespace WinForward.Protocols;

public readonly record struct Socks5UdpDatagram(IPAddress? DestinationAddress, string? DestinationDomain, ushort DestinationPort, ReadOnlyMemory<byte> Payload);

public static class Socks5UdpCodec
{
    public static byte[] Encode(IPAddress destinationAddress, ushort destinationPort, ReadOnlySpan<byte> payload)
    {
        var addressLength = destinationAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 4 : 16;
        var result = new byte[4 + addressLength + 2 + payload.Length];
        result[0] = 0;
        result[1] = 0;
        result[2] = 0;
        result[3] = destinationAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        destinationAddress.TryWriteBytes(result.AsSpan(4, addressLength), out _);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4 + addressLength, 2), destinationPort);
        payload.CopyTo(result.AsSpan(6 + addressLength));
        return result;
    }

    public static byte[] Encode(string destinationDomain, ushort destinationPort, ReadOnlySpan<byte> payload)
    {
        var domainBytes = System.Text.Encoding.UTF8.GetBytes(destinationDomain);
        if (domainBytes.Length is 0 or > 255) throw new ArgumentOutOfRangeException(nameof(destinationDomain));
        var result = new byte[4 + 1 + domainBytes.Length + 2 + payload.Length];
        result[3] = 3;
        result[4] = (byte)domainBytes.Length;
        domainBytes.CopyTo(result.AsSpan(5));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(5 + domainBytes.Length, 2), destinationPort);
        payload.CopyTo(result.AsSpan(7 + domainBytes.Length));
        return result;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out Socks5UdpDatagram datagram)
    {
        datagram = default;
        if (frame.Length < 4 || frame[0] != 0 || frame[1] != 0 || frame[2] != 0 || frame[3] is not (1 or 3 or 4)) return false;
        var offset = 4;
        IPAddress? address = null;
        string? domain = null;
        if (frame[3] is 1 or 4)
        {
            var addressLength = frame[3] == 1 ? 4 : 16;
            if (frame.Length < offset + addressLength + 2 || !TryReadAddress(frame.Slice(offset, addressLength), frame[3], out address)) return false;
            offset += addressLength;
        }
        else
        {
            if (frame.Length < offset + 1) return false;
            var domainLength = frame[offset++];
            if (domainLength == 0 || frame.Length < offset + domainLength + 2) return false;
            domain = System.Text.Encoding.UTF8.GetString(frame.Slice(offset, domainLength));
            offset += domainLength;
        }
        if (frame.Length < offset + 2) return false;
        datagram = new Socks5UdpDatagram(address, domain, BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2)), frame[(offset + 2)..].ToArray());
        return true;
    }

    private static bool TryReadAddress(ReadOnlySpan<byte> bytes, byte type, out IPAddress address)
    {
        try
        {
            address = new IPAddress(bytes);
            return (type == 1 && bytes.Length == 4) || (type == 4 && bytes.Length == 16);
        }
        catch (ArgumentException)
        {
            address = IPAddress.None;
            return false;
        }
    }
}
