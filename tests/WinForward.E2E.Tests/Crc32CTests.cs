using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class Crc32CTests
{
    // RFC 3720 B.4 anchors, written as literals on purpose: recomputing them with the routine under
    // test would make the test agree with any implementation, including a broken one.
    [Fact]
    public void EmptyInputHasNoRemainder()
    {
        Assert.Equal(0u, Crc32C.Compute([]));
    }

    [Fact]
    public void TheCheckValueOf123456789MatchesTheSpecification()
    {
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
    }

    // Lengths chosen to cover every branch of Compute: 0 (no loop at all), 8/16 (the SSE4.2 X64
    // step), 4 (the SSE4.2 32-bit step, or four table bytes where X64 is available), and 1/9/17
    // (the table tail after whole machine words).
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    public void EveryLengthMatchesAnIndependentBitwiseImplementation(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)((index * 31) + 7);
        }

        Assert.Equal(Reference(data), Crc32C.Compute(data));
    }

    [Fact]
    public void EverySingleByteValueMatchesTheReference()
    {
        for (var value = 0; value < 256; value++)
        {
            Assert.Equal(Reference([(byte)value]), Crc32C.Compute([(byte)value]));
        }
    }

    // Bit-at-a-time CRC-32C (reflected polynomial 0x82F63B78); shares no code with the table or the
    // SSE4.2 intrinsics the production routine uses.
    private static uint Reference(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
