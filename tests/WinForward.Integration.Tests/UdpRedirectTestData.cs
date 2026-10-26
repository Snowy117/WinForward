using System.Buffers.Binary;
using System.Net;
using WinForward.Userland.Relay;

namespace WinForward.Integration.Tests;

/// <summary>
/// Builds the 36-byte <c>redirect_context</c> and the <c>WSACMSGHDR</c> chain a WFP listener receives,
/// byte for byte as <c>kernel/src/common/redirect_context.h</c> lays them out.
/// </summary>
internal static class UdpRedirectTestData
{
    internal const int ContextSize = 36;
    internal const int Type = 70;
    internal const int LevelIp = 0;
    internal const int LevelIpv6 = 41;

    internal static byte[] Context(
        byte protocol = 17,
        byte family = 4,
        string address = "192.0.2.53",
        ushort port = 53,
        uint ruleIndex = 0,
        uint processId = 4242,
        uint magic = 0x31434657,
        ushort version = 1,
        int length = ContextSize)
    {
        var bytes = new byte[length];
        if (length >= 20)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), magic);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ruleIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), processId);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), version);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), port);
            bytes[16] = family;
            bytes[17] = protocol;
        }

        if (length >= ContextSize) IPAddress.Parse(address).TryWriteBytes(bytes.AsSpan(20, 16), out _);
        return bytes;
    }

    /// <summary>One cmsg holding <paramref name="payload"/>, padded to the chain's pointer-size alignment.</summary>
    internal static byte[] Cmsg(byte[] payload, int level, int type = Type)
    {
        var header = HeaderSize;
        var total = header + payload.Length;
        var aligned = (total + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
        var buffer = new byte[aligned];
        WriteLength(buffer, (uint)total);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(IntPtr.Size), level);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(IntPtr.Size + sizeof(int)), type);
        payload.CopyTo(buffer, header);
        return buffer;
    }

    /// <summary>Concatenates cmsg buffers into one control buffer.</summary>
    internal static byte[] Chain(params byte[][] messages)
    {
        var total = messages.Sum(message => message.Length);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var message in messages)
        {
            message.CopyTo(buffer, offset);
            offset += message.Length;
        }

        return buffer;
    }

    internal static int HeaderSize => IntPtr.Size + (2 * sizeof(int));

    /// <summary>Reads a cmsg's declared length at the chain's pointer width.</summary>
    internal static uint ReadDeclaredLength(ReadOnlySpan<byte> control) =>
        IntPtr.Size == sizeof(ulong)
            ? (uint)BinaryPrimitives.ReadUInt64LittleEndian(control)
            : BinaryPrimitives.ReadUInt32LittleEndian(control);

    /// <summary>Overwrites a cmsg's declared length, for the malformed-chain cases.</summary>
    internal static void WriteDeclaredLength(Span<byte> control, uint length)
    {
        if (IntPtr.Size == sizeof(ulong)) BinaryPrimitives.WriteUInt64LittleEndian(control, length);
        else BinaryPrimitives.WriteUInt32LittleEndian(control, length);
    }

    private static void WriteLength(Span<byte> destination, uint length)
    {
        if (IntPtr.Size == sizeof(ulong)) BinaryPrimitives.WriteUInt64LittleEndian(destination, length);
        else BinaryPrimitives.WriteUInt32LittleEndian(destination, length);
    }
}
