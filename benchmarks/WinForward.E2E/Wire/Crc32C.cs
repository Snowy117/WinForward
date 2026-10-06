using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;

namespace WinForward.E2E.Wire;

internal static class Crc32C
{
    private const uint ReflectedPolynomial = 0x82F63B78u;
    private const uint InitialValue = 0xFFFFFFFFu;
    private const uint FinalXor = 0xFFFFFFFFu;

    private static readonly uint[] s_table = BuildTable();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = InitialValue;

        if (Sse42.X64.IsSupported)
        {
            while (data.Length >= sizeof(ulong))
            {
                crc = (uint)Sse42.X64.Crc32(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
                data = data[sizeof(ulong)..];
            }
        }
        else if (Sse42.IsSupported)
        {
            while (data.Length >= sizeof(uint))
            {
                crc = Sse42.Crc32(crc, BinaryPrimitives.ReadUInt32LittleEndian(data));
                data = data[sizeof(uint)..];
            }
        }

        foreach (var value in data)
        {
            crc = (crc >> 8) ^ s_table[(crc ^ value) & 0xFF];
        }

        return crc ^ FinalXor;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var index = 0u; index < 256u; index++)
        {
            var crc = index;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0u ? (crc >> 1) ^ ReflectedPolynomial : crc >> 1;
            }

            table[index] = crc;
        }

        return table;
    }
}
