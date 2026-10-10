using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using WinForward.Core;
using Xunit;

namespace WinForward.Windows.Tests;

/// <summary>
/// The decode seam's exact facts: the row image becomes the rows it claims to, and a rescan of a
/// table that fits the storage a slot already grew allocates nothing. The allocation gate is the
/// regression lock for the amplification this path caused under connection churn: one fresh row
/// array plus two <see cref="IPAddress"/> objects per row per scan measured 96 % of the process's
/// allocation at 20 connections per second, against 42 KB per connection at one.
/// </summary>
public sealed class IPHelperOwnerTableParserTests
{
    private const int Tcp4RowSize = 24;

    [Fact]
    public void AFillDecodesTheDriverRows()
    {
        var image = BuildTcp4Image([(LocalPort: 53124, Pid: 4242u), (LocalPort: 53125, Pid: 4243u)]);
        var table = new OwnerTable(OwnerTableKind.Tcp4);

        FillTcp4(table, image);

        Assert.Equal(4242u, table.Lookup(TcpKey(53124)));
        Assert.Equal(4243u, table.Lookup(TcpKey(53125)));
        Assert.Null(table.Lookup(TcpKey(53126)));
    }

    [Fact]
    public void AFillAndItsLookupsAllocateNothingInSteadyState()
    {
        var rows = new (ushort LocalPort, uint Pid)[256];
        for (var index = 0; index < rows.Length; index++) rows[index] = ((ushort)(53000 + index), (uint)(1000 + index));
        var image = BuildTcp4Image(rows);
        var table = new OwnerTable(OwnerTableKind.Tcp4);

        // Warm-up: the slot grows its row storage here, which is the one fill allowed to allocate.
        FillTcp4(table, image);
        Assert.Equal(1000u, table.Lookup(TcpKey(53000)));

        // Keys come from cold types that allocate by design (IPAddress.Parse), so they are built
        // before the window; the window measures the scan and the two searches.
        var hitKey = TcpKey(53255);
        var missKey = TcpKey(9);
        var before = GC.GetAllocatedBytesForCurrentThread();
        FillTcp4(table, image);
        var hit = table.Lookup(hitKey);
        var miss = table.Lookup(missKey);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.Equal(1255u, hit);
        Assert.Null(miss);
    }

    private static FlowKey TcpKey(ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("198.51.100.7"), 443),
        TransportProtocol.Tcp,
        FlowOriginKind.Host);

    private static void FillTcp4(OwnerTable table, byte[] image)
    {
        var pinned = GCHandle.Alloc(image, GCHandleType.Pinned);
        try
        {
            var rowCount = BinaryPrimitives.ReadInt32LittleEndian(image);
            IPHelperOwnerTableParser.FillTcp4(table, pinned.AddrOfPinnedObject(), rowCount, (uint)image.Length);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// One MIB_TCPROW_OWNER_PID image per row, little-endian scalars as the driver writes them and
    /// addresses/ports in network order (the same bytes the parser reads back).
    /// </summary>
    private static byte[] BuildTcp4Image((ushort LocalPort, uint Pid)[] rows)
    {
        var image = new byte[sizeof(int) + (rows.Length * Tcp4RowSize)];
        BinaryPrimitives.WriteInt32LittleEndian(image, rows.Length);
        var localAddress = IPAddress.Parse("192.0.2.10").GetAddressBytes();
        var remoteAddress = IPAddress.Parse("198.51.100.7").GetAddressBytes();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = image.AsSpan(sizeof(int) + (index * Tcp4RowSize), Tcp4RowSize);
            BinaryPrimitives.WriteUInt32LittleEndian(row, 5);                     // MIB_TCP_STATE_ESTAB
            localAddress.CopyTo(row[4..]);
            BinaryPrimitives.WriteUInt16BigEndian(row[8..], rows[index].LocalPort);
            remoteAddress.CopyTo(row[12..]);
            BinaryPrimitives.WriteUInt16BigEndian(row[16..], 443);
            BinaryPrimitives.WriteUInt32LittleEndian(row[20..], rows[index].Pid);
        }

        return image;
    }
}
