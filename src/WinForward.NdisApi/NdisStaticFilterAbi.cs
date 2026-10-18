using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.NdisApi;

/// <summary>
/// The WinpkFilter static-filter-table ABI: the <c>STATIC_FILTER</c> record (184 bytes), its nested
/// selector records, the 8-byte <c>STATIC_FILTER_TABLE</c> header, and the encoder that lays out a
/// whole table in one contiguous buffer for <c>SetPacketFilterTable</c>. Every record mirrors
/// <c>Common.h</c> byte for byte under its <c>#pragma pack(push,1)</c>; the sizes and offsets are
/// pinned by <see cref="AssertManagedLayout"/> so a mirror mistake fails at driver open instead of on
/// the wire.
/// </summary>
/// <remarks>
/// <para>
/// The native table is variable length: a header plus <c>TableSize</c> fixed records, so N rules span
/// <c>8 + 184 * N</c> bytes. <see cref="TryWriteTable"/> encodes two oriented entries per flow rule —
/// one per header direction — and never encodes an empty table.
/// </para>
/// <para>
/// The driver wrapper computes the bytes it copies as
/// <c>sizeof(STATIC_FILTER_TABLE) + (m_TableSize - 1) * sizeof(STATIC_FILTER)</c> with
/// <c>ANY_SIZE == 1</c>, so a zero-entry table underflows that unsigned expression.
/// <c>SetPacketFilterTable</c> must therefore never see <c>m_TableSize == 0</c>; clearing the table is
/// <c>ResetPacketFilterTable</c>.
/// </para>
/// </remarks>
internal static class NdisStaticFilterAbi
{
    /// <summary>The fixed header in front of the record array (<c>m_TableSize</c>, padding).</summary>
    internal const int TableHeaderSize = 8;

    /// <summary><c>sizeof(STATIC_FILTER)</c> under <c>Pack=1</c>.</summary>
    internal const int StaticFilterSize = 184;

    /// <summary>How many flow rules fit a table whose byte length still fits a managed array.</summary>
    internal const int MaximumRules = (int.MaxValue - TableHeaderSize) / (StaticFilterSize * 2);

    // STATIC_FILTER.m_dwFilterAction values (Common.h).
    private const uint FilterPacketPass = 0x00000001;

    // STATIC_FILTER.m_dwDirectionFlags bits.
    private const uint DirectionFlagsBoth = NdisApiAbi.PacketFlagOnSend | NdisApiAbi.PacketFlagOnReceive;

    // STATIC_FILTER.m_dwValidFields bits: which nested selector records participate.
    private const uint NetworkLayerValid = 0x00000002;
    private const uint TransportLayerValid = 0x00000004;

    // NETWORK_LAYER_FILTER.UnionSelector: which address family the union holds.
    private const uint NetworkSelectorIPv4 = 0x00000001;
    private const uint NetworkSelectorIPv6 = 0x00000002;

    // TRANSPORT_LAYER_FILTER.UnionSelector: TCPUDP_FILTER (the only transport this encoder emits).
    private const uint TransportSelectorTcpUdp = 0x00000001;

    // IP_V4_FILTER.ValidFields; the IP_V6_FILTER values are identical.
    private const uint IPv4FilterSourceAddress = 0x00000001;
    private const uint IPv4FilterDestinationAddress = 0x00000002;
    private const uint IPv4FilterProtocol = 0x00000004;
    private const uint IPFilterAllFields = IPv4FilterSourceAddress | IPv4FilterDestinationAddress | IPv4FilterProtocol;

    // TCPUDP_FILTER.ValidFields.
    private const uint TcpUdpSourcePort = 0x00000001;
    private const uint TcpUdpDestinationPort = 0x00000002;
    private const uint TcpUdpBothPorts = TcpUdpSourcePort | TcpUdpDestinationPort;

    // IP_ADDRESS_V4/V6.UnionSelector: the union member is an exact-match subnet.
    private const uint IPv4SubnetAddressType = 0x00000001;
    private const uint IPv6SubnetAddressType = 0x00000001;

    private const byte IpProtocolTcp = 6;

    /// <summary>The byte length of a table holding <paramref name="ruleCount"/> flow rules (two entries each).</summary>
    internal static int EncodedSize(int ruleCount) => checked(TableHeaderSize + (ruleCount * 2 * StaticFilterSize));

    /// <summary>
    /// Writes a complete <c>STATIC_FILTER_TABLE</c> into <paramref name="destination"/>: the header,
    /// then two oriented <see cref="FilterPacketPass"/> entries per rule (A→B at <c>2i</c>, B→A at
    /// <c>2i+1</c>). Every entry carries adapter 0 (all adapters), both direction flags, exact
    /// addresses and ports, and zero statistics; the header tuple — not the direction flag — selects
    /// the direction.
    /// </summary>
    /// <returns>False when the table is empty, wider than <see cref="MaximumRules"/>, the buffer is
    /// too short, or a rule mixes address families. The destination's contents are unspecified on
    /// failure; a caller must not hand its bytes to the driver.</returns>
    internal static bool TryWriteTable(Span<byte> destination, ReadOnlySpan<NdisTcpFlowRule> rules, out int bytesWritten)
    {
        bytesWritten = 0;
        if (rules.IsEmpty || rules.Length > MaximumRules) return false;
        var required = EncodedSize(rules.Length);
        if (destination.Length < required) return false;

        var table = destination[..required];
        table.Clear();
        // m_TableSize counts records, not rules: each rule is two oriented entries.
        BinaryPrimitives.WriteUInt32LittleEndian(table, checked((uint)(rules.Length * 2)));

        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            var first = TableHeaderSize + (index * 2 * StaticFilterSize);
            if (!TryWriteEntry(table.Slice(first, StaticFilterSize), rule.AddressA, rule.PortA, rule.AddressB, rule.PortB)) return false;
            if (!TryWriteEntry(table.Slice(first + StaticFilterSize, StaticFilterSize), rule.AddressB, rule.PortB, rule.AddressA, rule.PortA)) return false;
        }

        bytesWritten = required;
        return true;
    }

    private static bool TryWriteEntry(Span<byte> entry, in IPAddressValue source, ushort sourcePort, in IPAddressValue destination, ushort destinationPort)
    {
        if (source.Family != destination.Family) return false;

        var filter = default(StaticFilter);
        filter.DirectionFlags = DirectionFlagsBoth;
        filter.FilterAction = FilterPacketPass;
        // The data-link selector stays empty: only the network and transport layers participate, so the
        // driver matches on the IP tuple and the record's ETH_802_3 union is never consulted.
        filter.ValidFields = NetworkLayerValid | TransportLayerValid;
        filter.TransportFilter.UnionSelector = TransportSelectorTcpUdp;
        filter.TransportFilter.TcpUdpFilter.ValidFields = TcpUdpBothPorts;
        filter.TransportFilter.TcpUdpFilter.SourcePort.StartRange = sourcePort;
        filter.TransportFilter.TcpUdpFilter.SourcePort.EndRange = sourcePort;
        filter.TransportFilter.TcpUdpFilter.DestinationPort.StartRange = destinationPort;
        filter.TransportFilter.TcpUdpFilter.DestinationPort.EndRange = destinationPort;

        if (source.Family == AddressFamilyKind.IPv4)
        {
            if (!WriteIPv4Selectors(ref filter, source, destination)) return false;
        }
        else if (!WriteIPv6Selectors(ref filter, source, destination))
        {
            return false;
        }

        MemoryMarshal.Write(entry, in filter);
        return true;
    }

    private static bool WriteIPv4Selectors(ref StaticFilter filter, in IPAddressValue source, in IPAddressValue destination)
    {
        Span<byte> addressBytes = stackalloc byte[8];
        if (!source.TryWrite(addressBytes, out _) || !destination.TryWrite(addressBytes[4..], out _)) return false;

        filter.NetworkFilter.UnionSelector = NetworkSelectorIPv4;
        filter.NetworkFilter.IPv4Filter.ValidFields = IPFilterAllFields;
        filter.NetworkFilter.IPv4Filter.Protocol = IpProtocolTcp;
        // The native DWORD is the little-endian reading of the four network-order address bytes, which
        // is exactly what the legacy sample gets from in_addr's byte fields.
        filter.NetworkFilter.IPv4Filter.SourceAddress.AddressType = IPv4SubnetAddressType;
        filter.NetworkFilter.IPv4Filter.SourceAddress.Subnet.Ip = BinaryPrimitives.ReadUInt32LittleEndian(addressBytes);
        filter.NetworkFilter.IPv4Filter.SourceAddress.Subnet.IpMask = uint.MaxValue;
        filter.NetworkFilter.IPv4Filter.DestinationAddress.AddressType = IPv4SubnetAddressType;
        filter.NetworkFilter.IPv4Filter.DestinationAddress.Subnet.Ip = BinaryPrimitives.ReadUInt32LittleEndian(addressBytes[4..]);
        filter.NetworkFilter.IPv4Filter.DestinationAddress.Subnet.IpMask = uint.MaxValue;
        return true;
    }

    private static unsafe bool WriteIPv6Selectors(ref StaticFilter filter, in IPAddressValue source, in IPAddressValue destination)
    {
        filter.NetworkFilter.UnionSelector = NetworkSelectorIPv6;
        filter.NetworkFilter.IPv6Filter.ValidFields = IPFilterAllFields;
        filter.NetworkFilter.IPv6Filter.Protocol = IpProtocolTcp;
        filter.NetworkFilter.IPv6Filter.SourceAddress.AddressType = IPv6SubnetAddressType;
        filter.NetworkFilter.IPv6Filter.DestinationAddress.AddressType = IPv6SubnetAddressType;
        fixed (byte* sourceIp = filter.NetworkFilter.IPv6Filter.SourceAddress.Subnet.Ip)
        {
            if (!source.TryWrite(new Span<byte>(sourceIp, 16), out _)) return false;
        }

        fixed (byte* destinationIp = filter.NetworkFilter.IPv6Filter.DestinationAddress.Subnet.Ip)
        {
            if (!destination.TryWrite(new Span<byte>(destinationIp, 16), out _)) return false;
        }

        fixed (byte* sourceMask = filter.NetworkFilter.IPv6Filter.SourceAddress.Subnet.IpMask)
        {
            new Span<byte>(sourceMask, 16).Fill(0xFF);
        }

        fixed (byte* destinationMask = filter.NetworkFilter.IPv6Filter.DestinationAddress.Subnet.IpMask)
        {
            new Span<byte>(destinationMask, 16).Fill(0xFF);
        }

        return true;
    }

    /// <summary>
    /// Pins every size and field offset of the mirrors to <c>Common.h</c>. Called from
    /// <see cref="NdisApiAbi.AssertManagedLayout"/> at driver open, so a packing or declaration
    /// mistake fails the run before any table IOCTL is issued.
    /// </summary>
    internal static void AssertManagedLayout()
    {
        NdisApiAbi.AssertSize<StaticFilterTable>(8);
        NdisApiAbi.AssertSize<StaticFilter>(184);
        NdisApiAbi.AssertSize<DataLinkLayerFilter>(24);
        NdisApiAbi.AssertSize<Eth8023Filter>(20);
        NdisApiAbi.AssertSize<NetworkLayerFilter>(84);
        NdisApiAbi.AssertSize<IPv4Filter>(32);
        NdisApiAbi.AssertSize<IPv6Filter>(80);
        NdisApiAbi.AssertSize<IPv4Address>(12);
        NdisApiAbi.AssertSize<IPv4Subnet>(8);
        NdisApiAbi.AssertSize<IPv6Address>(36);
        NdisApiAbi.AssertSize<IPv6Subnet>(32);
        NdisApiAbi.AssertSize<TransportLayerFilter>(20);
        NdisApiAbi.AssertSize<TcpUdpFilter>(16);
        NdisApiAbi.AssertSize<IcmpFilter>(8);
        NdisApiAbi.AssertSize<ByteRange>(2);
        NdisApiAbi.AssertSize<PortRange>(4);
        NdisApiAbi.AssertOffset<StaticFilterTable>(nameof(StaticFilterTable.TableSize), 0);
        NdisApiAbi.AssertOffset<StaticFilterTable>(nameof(StaticFilterTable.Padding), 4);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.Adapter), 0);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.DirectionFlags), 8);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.FilterAction), 12);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.ValidFields), 16);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.LastReset), 20);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.PacketsIn), 24);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.BytesIn), 32);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.PacketsOut), 40);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.BytesOut), 48);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.DataLinkFilter), 56);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.NetworkFilter), 80);
        NdisApiAbi.AssertOffset<StaticFilter>(nameof(StaticFilter.TransportFilter), 164);
        AssertNetworkLayout();
        AssertTransportLayout();
    }

    private static void AssertNetworkLayout()
    {
        NdisApiAbi.AssertOffset<DataLinkLayerFilter>(nameof(DataLinkLayerFilter.UnionSelector), 0);
        NdisApiAbi.AssertOffset<DataLinkLayerFilter>(nameof(DataLinkLayerFilter.Eth8023Filter), 4);
        NdisApiAbi.AssertOffset<Eth8023Filter>(nameof(Eth8023Filter.ValidFields), 0);
        NdisApiAbi.AssertOffset<Eth8023Filter>(nameof(Eth8023Filter.SourceAddress), 4);
        NdisApiAbi.AssertOffset<Eth8023Filter>(nameof(Eth8023Filter.DestinationAddress), 10);
        NdisApiAbi.AssertOffset<Eth8023Filter>(nameof(Eth8023Filter.Protocol), 16);
        NdisApiAbi.AssertOffset<Eth8023Filter>(nameof(Eth8023Filter.Padding), 18);
        NdisApiAbi.AssertOffset<NetworkLayerFilter>(nameof(NetworkLayerFilter.UnionSelector), 0);
        NdisApiAbi.AssertOffset<NetworkLayerFilter>(nameof(NetworkLayerFilter.IPv4Filter), 4);
        NdisApiAbi.AssertOffset<NetworkLayerFilter>(nameof(NetworkLayerFilter.IPv6Filter), 4);
        NdisApiAbi.AssertOffset<IPv4Filter>(nameof(IPv4Filter.ValidFields), 0);
        NdisApiAbi.AssertOffset<IPv4Filter>(nameof(IPv4Filter.SourceAddress), 4);
        NdisApiAbi.AssertOffset<IPv4Filter>(nameof(IPv4Filter.DestinationAddress), 16);
        NdisApiAbi.AssertOffset<IPv4Filter>(nameof(IPv4Filter.Protocol), 28);
        NdisApiAbi.AssertOffset<IPv4Filter>(nameof(IPv4Filter.Padding), 29);
        NdisApiAbi.AssertOffset<IPv6Filter>(nameof(IPv6Filter.ValidFields), 0);
        NdisApiAbi.AssertOffset<IPv6Filter>(nameof(IPv6Filter.SourceAddress), 4);
        NdisApiAbi.AssertOffset<IPv6Filter>(nameof(IPv6Filter.DestinationAddress), 40);
        NdisApiAbi.AssertOffset<IPv6Filter>(nameof(IPv6Filter.Protocol), 76);
        NdisApiAbi.AssertOffset<IPv6Filter>(nameof(IPv6Filter.Padding), 77);
        NdisApiAbi.AssertOffset<IPv4Address>(nameof(IPv4Address.AddressType), 0);
        NdisApiAbi.AssertOffset<IPv4Address>(nameof(IPv4Address.Subnet), 4);
        NdisApiAbi.AssertOffset<IPv4Subnet>(nameof(IPv4Subnet.Ip), 0);
        NdisApiAbi.AssertOffset<IPv4Subnet>(nameof(IPv4Subnet.IpMask), 4);
        NdisApiAbi.AssertOffset<IPv6Address>(nameof(IPv6Address.AddressType), 0);
        NdisApiAbi.AssertOffset<IPv6Address>(nameof(IPv6Address.Subnet), 4);
        NdisApiAbi.AssertOffset<IPv6Subnet>(nameof(IPv6Subnet.Ip), 0);
        NdisApiAbi.AssertOffset<IPv6Subnet>(nameof(IPv6Subnet.IpMask), 16);
    }

    private static void AssertTransportLayout()
    {
        NdisApiAbi.AssertOffset<TransportLayerFilter>(nameof(TransportLayerFilter.UnionSelector), 0);
        NdisApiAbi.AssertOffset<TransportLayerFilter>(nameof(TransportLayerFilter.TcpUdpFilter), 4);
        NdisApiAbi.AssertOffset<TransportLayerFilter>(nameof(TransportLayerFilter.IcmpFilter), 4);
        NdisApiAbi.AssertOffset<IcmpFilter>(nameof(IcmpFilter.ValidFields), 0);
        NdisApiAbi.AssertOffset<IcmpFilter>(nameof(IcmpFilter.TypeRange), 4);
        NdisApiAbi.AssertOffset<IcmpFilter>(nameof(IcmpFilter.CodeRange), 6);
        NdisApiAbi.AssertOffset<TcpUdpFilter>(nameof(TcpUdpFilter.ValidFields), 0);
        NdisApiAbi.AssertOffset<TcpUdpFilter>(nameof(TcpUdpFilter.SourcePort), 4);
        NdisApiAbi.AssertOffset<TcpUdpFilter>(nameof(TcpUdpFilter.DestinationPort), 8);
        NdisApiAbi.AssertOffset<TcpUdpFilter>(nameof(TcpUdpFilter.TcpFlags), 12);
        NdisApiAbi.AssertOffset<TcpUdpFilter>(nameof(TcpUdpFilter.Padding), 13);
        NdisApiAbi.AssertOffset<PortRange>(nameof(PortRange.StartRange), 0);
        NdisApiAbi.AssertOffset<PortRange>(nameof(PortRange.EndRange), 2);
        NdisApiAbi.AssertOffset<ByteRange>(nameof(ByteRange.StartRange), 0);
        NdisApiAbi.AssertOffset<ByteRange>(nameof(ByteRange.EndRange), 1);
    }
}

/// <summary>
/// <c>STATIC_FILTER_TABLE</c>: the variable-length table header. <see cref="TableSize"/> counts
/// <see cref="StaticFilter"/> records; the records follow at byte 8 and there is no trailing padding.
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 8)]
internal struct StaticFilterTable
{
    [FieldOffset(0)] public uint TableSize;
    [FieldOffset(4)] public uint Padding;
}

/// <summary>
/// <c>STATIC_FILTER</c>: one static rule plus the driver's per-rule statistics. The statistics fields
/// are read back after traffic has matched the rule; their order and offsets are what the packet
/// counters decode.
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 184)]
internal struct StaticFilter
{
    [FieldOffset(0)] public ulong Adapter;
    [FieldOffset(8)] public uint DirectionFlags;
    [FieldOffset(12)] public uint FilterAction;
    [FieldOffset(16)] public uint ValidFields;
    [FieldOffset(20)] public uint LastReset;
    [FieldOffset(24)] public ulong PacketsIn;
    [FieldOffset(32)] public ulong BytesIn;
    [FieldOffset(40)] public ulong PacketsOut;
    [FieldOffset(48)] public ulong BytesOut;
    [FieldOffset(56)] public DataLinkLayerFilter DataLinkFilter;
    [FieldOffset(80)] public NetworkLayerFilter NetworkFilter;
    [FieldOffset(164)] public TransportLayerFilter TransportFilter;
}

/// <summary><c>DATA_LINK_LAYER_FILTER</c>: a union selector followed by the selected MAC-level filter.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 24)]
internal struct DataLinkLayerFilter
{
    [FieldOffset(0)] public uint UnionSelector;
    [FieldOffset(4)] public Eth8023Filter Eth8023Filter;
}

/// <summary><c>ETH_802_3_FILTER</c>: the Ethernet selector the encoder leaves empty.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 20)]
internal unsafe struct Eth8023Filter
{
    [FieldOffset(0)] public uint ValidFields;
#pragma warning disable MA0189 // Fixed buffers mirror the Common.h ETH_802_3_FILTER declaration byte-for-byte (size and every offset pinned by AssertManagedLayout); InlineArray would replace a native declaration mirror with generated types.
    [FieldOffset(4)] public fixed byte SourceAddress[NdisApiAbi.EthernetAddressLength];
    [FieldOffset(10)] public fixed byte DestinationAddress[NdisApiAbi.EthernetAddressLength];
#pragma warning restore MA0189
    [FieldOffset(16)] public ushort Protocol;
#pragma warning disable MA0189 // The trailing padding is part of the native record: Common.h declares it explicitly and the record's 20-byte size depends on it.
    [FieldOffset(18)] public fixed byte Padding[2];
#pragma warning restore MA0189
}

/// <summary><c>NETWORK_LAYER_FILTER</c>: a union selector followed by the address-family filter.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 84)]
internal struct NetworkLayerFilter
{
    [FieldOffset(0)] public uint UnionSelector;
    [FieldOffset(4)] public IPv4Filter IPv4Filter;
    [FieldOffset(4)] public IPv6Filter IPv6Filter;
}

// Sonar's PascalCase rule splits at the version digit and proposes "Pv4Filter"/"Pv6Subnet"; the names
// mirror Common.h's IP_V4_FILTER / IP_ADDRESS_V6 records and follow the repository acronym rule, under
// which "IPv4"/"IPv6" are single words (see IPAddressValue in WinForward.Core).
#pragma warning disable S101
/// <summary><c>IP_V4_FILTER</c>: exact source/destination addresses, protocol, and the selector bits that enable them.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 32)]
internal unsafe struct IPv4Filter
{
    [FieldOffset(0)] public uint ValidFields;
    [FieldOffset(4)] public IPv4Address SourceAddress;
    [FieldOffset(16)] public IPv4Address DestinationAddress;
    [FieldOffset(28)] public byte Protocol;
#pragma warning disable MA0189 // The trailing padding is part of the native record: Common.h declares it explicitly and the record's 32-byte size depends on it.
    [FieldOffset(29)] public fixed byte Padding[3];
#pragma warning restore MA0189
}

/// <summary><c>IP_V6_FILTER</c>: the IPv6 twin of <see cref="IPv4Filter"/>.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 80)]
internal unsafe struct IPv6Filter
{
    [FieldOffset(0)] public uint ValidFields;
    [FieldOffset(4)] public IPv6Address SourceAddress;
    [FieldOffset(40)] public IPv6Address DestinationAddress;
    [FieldOffset(76)] public byte Protocol;
#pragma warning disable MA0189 // The trailing padding is part of the native record: Common.h declares it explicitly and the record's 80-byte size depends on it.
    [FieldOffset(77)] public fixed byte Padding[3];
#pragma warning restore MA0189
}

/// <summary><c>IP_ADDRESS_V4</c>: the address-type selector followed by the selected union member.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 12)]
internal struct IPv4Address
{
    [FieldOffset(0)] public uint AddressType;
    [FieldOffset(4)] public IPv4Subnet Subnet;
}

/// <summary><c>IP_SUBNET_V4</c>: the IPv4 address and its mask as the native DWORD pair.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 8)]
internal struct IPv4Subnet
{
    [FieldOffset(0)] public uint Ip;
    [FieldOffset(4)] public uint IpMask;
}

/// <summary><c>IP_ADDRESS_V6</c>: the address-type selector followed by the selected union member.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 36)]
internal struct IPv6Address
{
    [FieldOffset(0)] public uint AddressType;
    [FieldOffset(4)] public IPv6Subnet Subnet;
}

/// <summary><c>IP_SUBNET_V6</c>: the 16-byte IPv6 address and its 16-byte mask.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 32)]
internal unsafe struct IPv6Subnet
{
#pragma warning disable MA0189 // Fixed buffers mirror the Common.h IP_SUBNET_V6 declaration byte-for-byte (size and every offset pinned by AssertManagedLayout); the encoder writes them through spans.
    [FieldOffset(0)] public fixed byte Ip[16];
    [FieldOffset(16)] public fixed byte IpMask[16];
#pragma warning restore MA0189
}
#pragma warning restore S101

/// <summary><c>TRANSPORT_LAYER_FILTER</c>: a union selector followed by the transport filter.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 20)]
internal struct TransportLayerFilter
{
    [FieldOffset(0)] public uint UnionSelector;
    [FieldOffset(4)] public TcpUdpFilter TcpUdpFilter;
    [FieldOffset(4)] public IcmpFilter IcmpFilter;
}

/// <summary><c>TCPUDP_FILTER</c>: exact source/destination port ranges plus the TCP flags selector.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 16)]
internal unsafe struct TcpUdpFilter
{
    [FieldOffset(0)] public uint ValidFields;
    [FieldOffset(4)] public PortRange SourcePort;
    [FieldOffset(8)] public PortRange DestinationPort;
    [FieldOffset(12)] public byte TcpFlags;
#pragma warning disable MA0189 // The trailing padding is part of the native record: Common.h declares it explicitly and the record's 16-byte size depends on it.
    [FieldOffset(13)] public fixed byte Padding[3];
#pragma warning restore MA0189
}

/// <summary>
/// <c>ICMP_FILTER</c>: the transport union's ICMP member, unused by a TCP rule. The native record is
/// <c>DWORD m_ValidFields; BYTE_RANGE m_TypeRange; BYTE_RANGE m_CodeRange;</c> — two two-byte ranges,
/// not a single type byte.
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 8)]
internal struct IcmpFilter
{
    [FieldOffset(0)] public uint ValidFields;
    [FieldOffset(4)] public ByteRange TypeRange;
    [FieldOffset(6)] public ByteRange CodeRange;
}

/// <summary><c>BYTE_RANGE</c>: the native two-byte inclusive range record (start, end).</summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 2)]
internal struct ByteRange
{
    [FieldOffset(0)] public byte StartRange;
    [FieldOffset(1)] public byte EndRange;
}

/// <summary>
/// <c>PORT_RANGE</c>: an inclusive range in host byte order. A single exact port is encoded with
/// both ends set to it.
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 4)]
internal struct PortRange
{
    [FieldOffset(0)] public ushort StartRange;
    [FieldOffset(2)] public ushort EndRange;
}
