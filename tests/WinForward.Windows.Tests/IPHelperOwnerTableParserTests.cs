using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using WinForward.Core;
using Xunit;

namespace WinForward.Windows.Tests;

/// <summary>
/// The decode seam's exact facts: each driver row image becomes the rows it claims to, a rescan of a
/// table that fits the storage a slot already grew allocates nothing, and the fill protocol's two
/// failure timings stay distinguishable. The allocation gate is the regression lock for the
/// amplification this path caused under connection churn: one fresh row array plus two
/// <see cref="IPAddress"/> objects per row per scan measured 96 % of the process's allocation at 20
/// connections per second, against 42 KB per connection at one.
/// </summary>
public sealed class IPHelperOwnerTableParserTests
{
    private const int Tcp4RowSize = 24;
    private const int Udp4RowSize = 12;
    private const int Udp6RowSize = 28;
    private const int Tcp6RowSize = 56;

    private static readonly byte[] s_localV4 = IPAddress.Parse("192.0.2.10").GetAddressBytes();
    private static readonly byte[] s_remoteV4 = IPAddress.Parse("198.51.100.7").GetAddressBytes();
    private static readonly byte[] s_localV6 = IPAddress.Parse("2001:db8::10").GetAddressBytes();
    private static readonly byte[] s_remoteV6 = IPAddress.Parse("2001:db8::80").GetAddressBytes();

    [Fact]
    public void AFillDecodesTheDriverRows()
    {
        var image = BuildTcp4Image([(LocalPort: 53124, Pid: 4242u), (LocalPort: 53125, Pid: 4243u)]);
        var table = new OwnerTable(OwnerTableKind.Tcp4);

        Fill(table, image, RowKind.Tcp4);

        Assert.Equal(4242u, table.Lookup(TcpKey(53124)));
        Assert.Equal(4243u, table.Lookup(TcpKey(53125)));
        Assert.Null(table.Lookup(TcpKey(53126)));
    }

    [Fact]
    public unsafe void AFillAndItsLookupsAllocateNoManagedBytes()
    {
        var rows = new (ushort LocalPort, uint Pid)[256];
        for (var index = 0; index < rows.Length; index++) rows[index] = ((ushort)(53000 + index), (uint)(1000 + index));
        var image = BuildTcp4Image(rows);
        var table = new OwnerTable(OwnerTableKind.Tcp4);

        // Keys come from cold types that allocate by design (IPAddress.Parse), so they are built
        // before the window; the pin, the counts and every delegate are hoisted for the same reason:
        // the window measures the scan and the two searches, never the harness.
        var hitKey = TcpKey(53255);
        var missKey = TcpKey(9);
        var pinned = GCHandle.Alloc(image, GCHandleType.Pinned);
        try
        {
            var buffer = (void*)pinned.AddrOfPinnedObject();
            var rowCount = BinaryPrimitives.ReadInt32LittleEndian(image);
            var bytesWritten = (uint)image.Length;

            // Warm-up: the slot grows its row storage here, which is the one fill allowed to allocate.
            IPHelperOwnerTableParser.FillTcp4(table, buffer, rowCount, bytesWritten);
            Assert.Equal(1000u, table.Lookup(TcpKey(53000)));

            var before = GC.GetAllocatedBytesForCurrentThread();
            IPHelperOwnerTableParser.FillTcp4(table, buffer, rowCount, bytesWritten);
            var hit = table.Lookup(hitKey);
            var miss = table.Lookup(missKey);
            var after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
            Assert.Equal(1255u, hit);
            Assert.Null(miss);
        }
        finally
        {
            pinned.Free();
        }
    }

    [Fact]
    public void Udp4AndUdp6FillsDecodeTheirRows()
    {
        var udp4 = new OwnerTable(OwnerTableKind.Udp4);
        Fill(udp4, BuildUdp4Image([(LocalPort: 53124, Pid: 4242u)]), RowKind.Udp4);
        Assert.Equal(4242u, udp4.Lookup(UdpKey(53124)));
        Assert.Null(udp4.Lookup(UdpKey(53125)));

        var udp6 = new OwnerTable(OwnerTableKind.Udp6);
        Fill(udp6, BuildUdp6Image([(Address: s_localV6, ScopeId: 9u, LocalPort: 53124, Pid: 4242u)]), RowKind.Udp6);
        Assert.Equal(4242u, udp6.Lookup(Udp6Key(s_localV6, scopeId: 9, localPort: 53124)));
        // The scope travels with the address: the same bits with another scope are another socket.
        Assert.Null(udp6.Lookup(Udp6Key(s_localV6, scopeId: 0, localPort: 53124)));
        Assert.Null(udp6.Lookup(Udp6Key(s_localV6, scopeId: 9, localPort: 53125)));
    }

    [Fact]
    public void Tcp6FillsDecodeAddressesAndScopes()
    {
        var table = new OwnerTable(OwnerTableKind.Tcp6);
        Fill(table, BuildTcp6Image([(LocalAddress: s_localV6, LocalScopeId: 9u, LocalPort: 53124, RemoteAddress: s_remoteV6, RemoteScopeId: 0u, RemotePort: 443, Pid: 4242u)]), RowKind.Tcp6);

        Assert.Equal(4242u, table.Lookup(Tcp6Key(localScopeId: 9, localPort: 53124, remotePort: 443)));
        Assert.Null(table.Lookup(Tcp6Key(localScopeId: 0, localPort: 53124, remotePort: 443)));
        Assert.Null(table.Lookup(Tcp6Key(localScopeId: 9, localPort: 53125, remotePort: 443)));
        Assert.Null(table.Lookup(Tcp6Key(localScopeId: 9, localPort: 53124, remotePort: 8443)));
    }

    [Fact]
    public void ABegunFillIsUnsearchableUntilItCompletes()
    {
        var table = new OwnerTable(OwnerTableKind.Tcp4, [], [TcpRow(53124, 7)]);
        Assert.Equal(7u, table.Lookup(TcpKey(53124)));

        var fill = table.BeginTcpFill(1);
        Assert.False(table.IsAvailable);
        Assert.Null(table.Lookup(TcpKey(53124)));

        fill[0] = TcpRow(53124, 9);
        table.CompleteFill();

        Assert.True(table.IsAvailable);
        Assert.Equal(9u, table.Lookup(TcpKey(53124)));
    }

    [Fact]
    public void ARejectedRowCountLeavesTheSlotsContentsAnswering()
    {
        var table = new OwnerTable(OwnerTableKind.Tcp4, [], [TcpRow(53124, 7)]);
        var image = BuildTcp4Image([(LocalPort: 53124, Pid: 7u)]);

        // The parser validates the announced row count against the driver-written byte count before
        // it begins a fill, so this failure is equivalent to the read never having happened.
        Assert.Throws<InvalidOperationException>(() => Fill(table, image, RowKind.Tcp4, rowCount: 2));
        Assert.True(table.IsAvailable);
        Assert.Equal(7u, table.Lookup(TcpKey(53124)));
    }

    [Fact]
    public void TheUnavailableTableCannotBeFilled()
    {
        Assert.False(OwnerTable.Unavailable.IsAvailable);
        Assert.Throws<InvalidOperationException>(() => OwnerTable.Unavailable.BeginTcpFill(1).Length);
        Assert.Throws<InvalidOperationException>(() => OwnerTable.Unavailable.BeginUdpFill(1).Length);
    }

    private static FlowKey TcpKey(ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("198.51.100.7"), 443),
        TransportProtocol.Tcp,
        FlowOriginKind.Host);

    private static FlowKey UdpKey(ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("198.51.100.7"), 53),
        TransportProtocol.Udp,
        FlowOriginKind.Host);

    private static FlowKey Udp6Key(byte[] localAddress, uint scopeId, ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddressValue.FromIPv6(localAddress, scopeId), localPort),
        Endpoint.From(IPAddressValue.FromIPv6(s_remoteV6), 53),
        TransportProtocol.Udp,
        FlowOriginKind.Host);

    private static FlowKey Tcp6Key(uint localScopeId, ushort localPort, ushort remotePort) => FlowKey.Create(
        Endpoint.From(IPAddressValue.FromIPv6(s_localV6, localScopeId), localPort),
        Endpoint.From(IPAddressValue.FromIPv6(s_remoteV6), remotePort),
        TransportProtocol.Tcp,
        FlowOriginKind.Host);

    private static TcpOwnerRow TcpRow(ushort localPort, uint processId) => new(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("198.51.100.7"), 443),
        processId);

    private enum RowKind
    {
        Tcp4,
        Tcp6,
        Udp4,
        Udp6,
    }

    /// <summary>
    /// Pins the image and runs one parser fill over it. The kind dispatch keeps the helper free of a
    /// generic pointer type argument, which C# does not allow.
    /// </summary>
    private static unsafe void Fill(OwnerTable table, byte[] image, RowKind kind, int? rowCount = null)
    {
        var pinned = GCHandle.Alloc(image, GCHandleType.Pinned);
        try
        {
            var buffer = (void*)pinned.AddrOfPinnedObject();
            var count = rowCount ?? BinaryPrimitives.ReadInt32LittleEndian(image);
            var written = (uint)image.Length;
            switch (kind)
            {
                case RowKind.Tcp4: IPHelperOwnerTableParser.FillTcp4(table, buffer, count, written); break;
                case RowKind.Tcp6: IPHelperOwnerTableParser.FillTcp6(table, buffer, count, written); break;
                case RowKind.Udp4: IPHelperOwnerTableParser.FillUdp4(table, buffer, count, written); break;
                case RowKind.Udp6: IPHelperOwnerTableParser.FillUdp6(table, buffer, count, written); break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
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
        for (var index = 0; index < rows.Length; index++)
        {
            var row = image.AsSpan(sizeof(int) + (index * Tcp4RowSize), Tcp4RowSize);
            BinaryPrimitives.WriteUInt32LittleEndian(row, 5);                     // MIB_TCP_STATE_ESTAB
            s_localV4.CopyTo(row[4..]);
            BinaryPrimitives.WriteUInt16BigEndian(row[8..], rows[index].LocalPort);
            s_remoteV4.CopyTo(row[12..]);
            BinaryPrimitives.WriteUInt16BigEndian(row[16..], 443);
            BinaryPrimitives.WriteUInt32LittleEndian(row[20..], rows[index].Pid);
        }

        return image;
    }

    /// <summary>One MIB_UDPROW_OWNER_PID image per row (local address unbound, i.e. the wildcard).</summary>
    private static byte[] BuildUdp4Image((ushort LocalPort, uint Pid)[] rows)
    {
        var image = new byte[sizeof(int) + (rows.Length * Udp4RowSize)];
        BinaryPrimitives.WriteInt32LittleEndian(image, rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = image.AsSpan(sizeof(int) + (index * Udp4RowSize), Udp4RowSize);
            BinaryPrimitives.WriteUInt16BigEndian(row[4..], rows[index].LocalPort);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], rows[index].Pid);
        }

        return image;
    }

    /// <summary>One MIB_UDP6ROW_OWNER_PID image per row.</summary>
    private static byte[] BuildUdp6Image((byte[] Address, uint ScopeId, ushort LocalPort, uint Pid)[] rows)
    {
        var image = new byte[sizeof(int) + (rows.Length * Udp6RowSize)];
        BinaryPrimitives.WriteInt32LittleEndian(image, rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = image.AsSpan(sizeof(int) + (index * Udp6RowSize), Udp6RowSize);
            rows[index].Address.CopyTo(row);
            BinaryPrimitives.WriteUInt32LittleEndian(row[16..], rows[index].ScopeId);
            BinaryPrimitives.WriteUInt16BigEndian(row[20..], rows[index].LocalPort);
            BinaryPrimitives.WriteUInt32LittleEndian(row[24..], rows[index].Pid);
        }

        return image;
    }

    /// <summary>One MIB_TCP6ROW_OWNER_PID image per row.</summary>
    private static byte[] BuildTcp6Image((byte[] LocalAddress, uint LocalScopeId, ushort LocalPort, byte[] RemoteAddress, uint RemoteScopeId, ushort RemotePort, uint Pid)[] rows)
    {
        var image = new byte[sizeof(int) + (rows.Length * Tcp6RowSize)];
        BinaryPrimitives.WriteInt32LittleEndian(image, rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = image.AsSpan(sizeof(int) + (index * Tcp6RowSize), Tcp6RowSize);
            rows[index].LocalAddress.CopyTo(row);
            BinaryPrimitives.WriteUInt32LittleEndian(row[16..], rows[index].LocalScopeId);
            BinaryPrimitives.WriteUInt16BigEndian(row[20..], rows[index].LocalPort);
            rows[index].RemoteAddress.CopyTo(row[24..]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[40..], rows[index].RemoteScopeId);
            BinaryPrimitives.WriteUInt16BigEndian(row[44..], rows[index].RemotePort);
            BinaryPrimitives.WriteUInt32LittleEndian(row[48..], 5);               // MIB_TCP_STATE_ESTAB
            BinaryPrimitives.WriteUInt32LittleEndian(row[52..], rows[index].Pid);
        }

        return image;
    }
}
