using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace WinForward.E2E.Target;

/// <summary>
/// The source endpoints datagrams arrived from, one fixed table per receiver so a datagram costs neither
/// an allocation nor a lock: only the owning receiver writes a table, and the summariser is the only
/// reader. An endpoint keeps its slot for the life of the server, so a slot's identity is written once
/// and its count only grows; <see cref="Harvest"/> publishes the delta since the previous interval and
/// therefore reports the endpoints seen in that interval. A table that is full puts nothing on the wire:
/// the datagram is counted as overflow instead of being silently dropped from the census.
/// </summary>
internal sealed class SourceCensus
{
    private const int SourceCapacity = 64;

    private readonly Slot[] _slots = new Slot[SourceCapacity];
    private readonly long[] _published = new long[SourceCapacity];
    private long _unplaced;
    private long _publishedUnplaced;

    internal void Record(EndPoint remote)
    {
        if (remote is not IPEndPoint endPoint || !TryGetIdentity(endPoint.Address, out var high, out var low))
        {
            Interlocked.Increment(ref _unplaced);
            return;
        }

        var port = endPoint.Port;
        var free = -1;
        for (var index = 0; index < _slots.Length; index++)
        {
            ref var slot = ref _slots[index];
            if (slot._port == port && slot._high == high && slot._low == low)
            {
                slot._datagrams++;
                return;
            }

            if (slot._port == 0 && free < 0)
            {
                free = index;
            }
        }

        if (free < 0)
        {
            Interlocked.Increment(ref _unplaced);
            return;
        }

        ref var claimed = ref _slots[free];
        claimed._high = high;
        claimed._low = low;
        claimed._datagrams = 1;

        // A source port is never zero, so publishing the port last both marks the slot as claimed and
        // releases the identity and the count to the summariser without an atomic block.
        Volatile.Write(ref claimed._port, port);
    }

    internal void Harvest(Dictionary<SourceKey, long> totals)
    {
        for (var index = 0; index < _slots.Length; index++)
        {
            ref var slot = ref _slots[index];
            var port = Volatile.Read(ref slot._port);
            if (port == 0)
            {
                continue;
            }

            var datagrams = Interlocked.Read(ref slot._datagrams);
            var delta = datagrams - _published[index];
            if (delta <= 0)
            {
                continue;
            }

            _published[index] = datagrams;
            var key = new SourceKey(Volatile.Read(ref slot._high), Volatile.Read(ref slot._low), port);
            totals[key] = totals.TryGetValue(key, out var existing) ? existing + delta : delta;
        }
    }

    internal long HarvestUnplaced()
    {
        var unplaced = Interlocked.Read(ref _unplaced);
        var delta = unplaced - _publishedUnplaced;
        if (delta <= 0)
        {
            return 0;
        }

        _publishedUnplaced = unplaced;
        return delta;
    }

    /// <summary>The census key as the address a ledger row publishes, IPv4-mapped form included.</summary>
    internal static IPAddress ToAddress(SourceKey key)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, key.High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], key.Low);
        return key.High == 0 && (key.Low >> 32) == 0xFFFF
            ? new IPAddress(bytes[12..])
            : new IPAddress(bytes);
    }

    private static bool TryGetIdentity(IPAddress address, out ulong high, out ulong low)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
        {
            high = 0;
            low = 0;
            return false;
        }

        if (written == 4)
        {
            // Normalise IPv4 into its IPv4-mapped IPv6 form: one comparison then covers a v4 client and
            // the same client seen through a listener bound to IPv6Any.
            bytes[12] = bytes[0];
            bytes[13] = bytes[1];
            bytes[14] = bytes[2];
            bytes[15] = bytes[3];
            bytes[..12].Clear();
            bytes[10] = 0xFF;
            bytes[11] = 0xFF;
        }

        high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        return true;
    }

    [StructLayout(LayoutKind.Auto)]
    private struct Slot
    {
        internal ulong _high;
        internal ulong _low;
        internal int _port;
        internal long _datagrams;
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SourceKey(ulong High, ulong Low, int Port);
