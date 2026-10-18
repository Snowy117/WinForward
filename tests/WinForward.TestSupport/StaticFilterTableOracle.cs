using System.Buffers.Binary;

namespace WinForward.TestSupport;

/// <summary>
/// An implementation-independent oracle for the WinpkFilter static filter table. It builds and reads
/// the 184-byte records from the documented byte offsets only — never through the production mirrors
/// or encoder — so an encoder bug cannot make both sides of an assertion agree.
/// </summary>
internal static class StaticFilterTableOracle
{
    private const int HeaderSize = 8;
    private const int RecordSize = 184;

    public const uint FilterPacketPass = 1;
    public const uint BothDirections = 3;
    private const uint NetworkAndTransportValid = 6;
    private const uint NetworkSelectorIPv4 = 1;
    private const uint NetworkSelectorIPv6 = 2;
    private const uint TransportSelectorTcpUdp = 1;
    private const uint AllIpFields = 7;
    private const uint BothPorts = 3;
    private const byte TcpProtocol = 6;

    /// <summary>Builds a whole table from oriented entries: the header counts records, then the records follow.</summary>
    public static byte[] Table(params Entry[] entries)
    {
        var table = new byte[HeaderSize + (entries.Length * RecordSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)entries.Length);
        for (var index = 0; index < entries.Length; index++) entries[index].WriteTo(table.AsSpan(HeaderSize + (index * RecordSize)));

        return table;
    }

    /// <summary>One oriented entry: source address/port toward destination address/port, exact matches.</summary>
    public static Entry Oriented(byte[] from, ushort fromPort, byte[] to, ushort toPort) =>
        new(from, fromPort, to, toPort);

    /// <summary>Reads one record back into its tuple so a test can assert what was actually encoded.</summary>
    public static (byte[] Source, ushort SourcePort, byte[] Destination, ushort DestinationPort, uint Action, ulong Adapter, uint DirectionFlags) ReadEntry(byte[] table, int entryIndex)
    {
        var entry = table.AsSpan(HeaderSize + (entryIndex * RecordSize));
        var action = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        var adapter = BinaryPrimitives.ReadUInt64LittleEndian(entry);
        var directionFlags = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        var sourcePort = BinaryPrimitives.ReadUInt16LittleEndian(entry[172..]);
        var destinationPort = BinaryPrimitives.ReadUInt16LittleEndian(entry[176..]);
        return BinaryPrimitives.ReadUInt32LittleEndian(entry[80..]) == NetworkSelectorIPv4
            ? (entry.Slice(92, 4).ToArray(), sourcePort, entry.Slice(104, 4).ToArray(), destinationPort, action, adapter, directionFlags)
            : (entry.Slice(92, 16).ToArray(), sourcePort, entry.Slice(128, 16).ToArray(), destinationPort, action, adapter, directionFlags);
    }


    /// <summary>Copies a table, writing the two per-rule packet counters of one record.</summary>
    public static byte[] WithCounters(byte[] table, int entryIndex, ulong packetsIn, ulong packetsOut)
    {
        var copy = (byte[])table.Clone();
        var entry = copy.AsSpan(HeaderSize + (entryIndex * RecordSize));
        BinaryPrimitives.WriteUInt64LittleEndian(entry[24..], packetsIn);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], packetsOut);
        return copy;
    }

    /// <summary>Copies a table, overwriting one byte of one record (a field a read-back must reject).</summary>
    public static byte[] WithByte(byte[] table, int entryIndex, int offset, byte value)
    {
        var copy = (byte[])table.Clone();
        copy[HeaderSize + (entryIndex * RecordSize) + offset] = value;
        return copy;
    }

    internal readonly struct Entry(byte[] from, ushort fromPort, byte[] to, ushort toPort)
    {
        public void WriteTo(Span<byte> entry)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(entry, 0UL);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], BothDirections);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], FilterPacketPass);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], NetworkAndTransportValid);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[164..], TransportSelectorTcpUdp);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[168..], BothPorts);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[172..], fromPort);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[174..], fromPort);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[176..], toPort);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[178..], toPort);

            if (from.Length == 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry[80..], NetworkSelectorIPv4);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[84..], AllIpFields);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[88..], 1u);
                from.CopyTo(entry[92..]);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[96..], uint.MaxValue);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[100..], 1u);
                to.CopyTo(entry[104..]);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[108..], uint.MaxValue);
                entry[112] = TcpProtocol;
                return;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(entry[80..], NetworkSelectorIPv6);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[84..], AllIpFields);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[88..], 1u);
            from.CopyTo(entry[92..]);
            entry[108..124].Fill(0xFF);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[124..], 1u);
            to.CopyTo(entry[128..]);
            entry[144..160].Fill(0xFF);
            entry[160] = TcpProtocol;
        }
    }

}
