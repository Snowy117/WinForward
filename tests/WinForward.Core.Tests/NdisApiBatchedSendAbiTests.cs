using System.Runtime.Versioning;
using WinForward.NdisApi;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// ABI-layer tests of the batched send request construction (task 08-30-batched-ioctls S3).
/// The native send calls themselves need ndisapi.dll and stay on the Windows path; these tests
/// pin the host-agnostic contract: the chunk-size budget derivation and the exact
/// ETH_M_REQUEST header/slot layout that <see cref="NdisApiDriver"/> sends to the driver.
/// </summary>
public sealed class NdisApiBatchedSendAbiTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void SendChunkCapacityStaysInsideStackallocBudget()
    {
        // 16 header bytes + one 8-byte pointer slot per packet must fit the 1KB budget the
        // driver stackallocs for a whole chunk.
        var worstChunkBytes = 16 + 8 * NdisApiDriver.MaxPacketsPerSendRequest;
        Assert.InRange(NdisApiDriver.MaxPacketsPerSendRequest, 1, 126);
        Assert.True(worstChunkBytes <= 1024, $"A full send chunk spans {worstChunkBytes} bytes and overflows the 1KB stack budget.");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public unsafe void BuildMultiRequestFillsHeaderAndSlotsInOrder()
    {
        using var first = new NdisPacketBuffer();
        using var second = new NdisPacketBuffer();
        using var third = new NdisPacketBuffer();
        var buffers = new[] { first, second, third };

        var requestBytes = stackalloc byte[16 + 8 * buffers.Length];
        NdisApiDriver.BuildMultiRequest(requestBytes, (nint)0x55, buffers, buffers.Length, offset: 0);

        var request = (EthernetMultiRequest*)requestBytes;
        var slots = (NdisrdEthernetPacket*)&request->FirstBuffer;
        Assert.Equal((nint)0x55, request->AdapterHandle);
        Assert.Equal(3u, request->PacketsNumber);
        Assert.Equal(0u, request->PacketsSuccess);
        Assert.Equal(first.Pointer, slots[0].Buffer);
        Assert.Equal(second.Pointer, slots[1].Buffer);
        Assert.Equal(third.Pointer, slots[2].Buffer);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public unsafe void BuildMultiRequestOffsetSelectsTheSuffixBuffers()
    {
        // Chunked flushes rebuild the request for each chunk; offset must address the chunk's
        // own buffers without copying or reordering.
        using var first = new NdisPacketBuffer();
        using var second = new NdisPacketBuffer();
        using var third = new NdisPacketBuffer();
        var buffers = new[] { first, second, third };

        var requestBytes = stackalloc byte[16 + 8 * 2];
        NdisApiDriver.BuildMultiRequest(requestBytes, (nint)0x66, buffers, count: 2, offset: 1);

        var request = (EthernetMultiRequest*)requestBytes;
        var slots = (NdisrdEthernetPacket*)&request->FirstBuffer;
        Assert.Equal((nint)0x66, request->AdapterHandle);
        Assert.Equal(2u, request->PacketsNumber);
        Assert.Equal(second.Pointer, slots[0].Buffer);
        Assert.Equal(third.Pointer, slots[1].Buffer);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public unsafe void BuildMultiRequestRejectsNullEntriesInsideTheRange()
    {
        using var buffer = new NdisPacketBuffer();
        var buffers = new NdisPacketBuffer?[] { buffer, null };
        var requestBytes = stackalloc byte[16 + 8 * 2];

        Assert.Throws<ArgumentNullException>(() =>
            NdisApiDriver.BuildMultiRequest(requestBytes, (nint)1, buffers!, count: 2, offset: 0));
    }
}
