using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace WinForward.E2E.Target;

/// <summary>
/// The source endpoints datagrams arrived from, one fixed table per receiver so a datagram costs neither
/// an allocation nor a lock: only the owning receiver writes a table, and the summariser is the only
/// reader. A datagram keeps the slot of its own <c>(address, port)</c> while that endpoint keeps
/// arriving; a slot the current interval has not touched yet -- every slot at the moment
/// <see cref="Harvest"/> opens an interval -- is one another endpoint may claim, so the table's capacity
/// is the endpoints one interval saw rather than every endpoint the process has ever seen, and a campaign
/// that outlives <see cref="SourceCapacity"/> endpoints does not go blind for the rest of its life.
/// <see cref="Harvest"/> publishes the delta since the previous interval and therefore reports what each
/// endpoint sent in that interval; a fresh claim starts its count over, so the whole of that count is
/// the claim's first interval. A table that is full puts nothing on the wire: the datagram is counted as
/// overflow instead of being silently dropped from the census.
/// </summary>
/// <remarks>
/// <para><b>One writer.</b> A census belongs to one receive loop, and the loop awaits one receive before
/// it records the next datagram, so <see cref="Record"/> never overlaps itself for a table. The receiver
/// reads and writes every slot field itself; only the summariser is on the other side of the table.</para>
/// <para><b>The claim handshake.</b> A claim publishes itself by writing <c>_claim</c> last, with a
/// release (<see cref="Volatile.Write(ref long, long)"/>), so a summariser that reads that value sees the
/// address and the count written before it. Re-claiming a slot first writes <c>_claim</c> back to zero
/// and then crosses <see cref="Interlocked.MemoryBarrier"/>, so the new identity can never be observed
/// before the invalidating write, on any architecture. <see cref="Harvest"/> brackets its read of a slot
/// with two reads of <c>_claim</c>: a slot claimed under the read is left alone and reported whole in the
/// next interval, rather than published as one claim's address beside another claim's count.</para>
/// <para><b>What reclaim does not change.</b> The delta is still the interval's own, and the map is keyed
/// by the endpoint rather than by the slot: an endpoint whose slot another endpoint claimed first in the
/// new interval starts its count over, and that fresh claim's whole count is this interval's datagrams
/// for the endpoint -- published under its own key exactly as a continuing claim's difference is. All a
/// re-claim costs is the datagrams recorded after the closing interval's read of the old count, which the
/// overwrite discards instead of publishing.</para>
/// </remarks>
internal sealed class SourceCensus
{
    private const int SourceCapacity = 64;

    private readonly Slot[] _slots = new Slot[SourceCapacity];
    private readonly long[] _published = new long[SourceCapacity];
    private readonly long[] _publishedClaim = new long[SourceCapacity];
    private long _epoch = 1;
    private long _unplaced;
    private long _publishedUnplaced;

    internal void Record(EndPoint remote)
    {
        if (remote is not IPEndPoint endPoint || !TryGetIdentity(endPoint.Address, out var high, out var low))
        {
            Interlocked.Increment(ref _unplaced);
            return;
        }

        var epoch = Volatile.Read(ref _epoch);
        var port = endPoint.Port;
        var free = -1;
        for (var index = 0; index < _slots.Length; index++)
        {
            ref var slot = ref _slots[index];
            if (slot._port == port && slot._high == high && slot._low == low)
            {
                slot._datagrams++;
                slot._lastSeen = epoch;
                return;
            }

            if (free < 0 && slot._lastSeen != epoch)
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
        Volatile.Write(ref claimed._claim, 0);
        Interlocked.MemoryBarrier();
        claimed._high = high;
        claimed._low = low;
        claimed._port = port;
        claimed._datagrams = 1;
        claimed._lastSeen = epoch;
        Volatile.Write(ref claimed._claim, epoch);
    }

    internal void Harvest(Dictionary<SourceKey, long> totals)
    {
        for (var index = 0; index < _slots.Length; index++)
        {
            ref var slot = ref _slots[index];
            var claim = Volatile.Read(ref slot._claim);
            if (claim == 0)
            {
                continue;
            }

            var high = slot._high;
            var low = slot._low;
            var port = slot._port;
            var datagrams = Interlocked.Read(ref slot._datagrams);
            if (Volatile.Read(ref slot._claim) != claim)
            {
                continue;
            }

            if (_publishedClaim[index] != claim)
            {
                _publishedClaim[index] = claim;
                _published[index] = 0;
            }

            var delta = datagrams - _published[index];
            if (delta <= 0)
            {
                continue;
            }

            _published[index] = datagrams;
            var key = new SourceKey(high, low, port);
            totals[key] = totals.TryGetValue(key, out var existing) ? existing + delta : delta;
        }

        Volatile.Write(ref _epoch, _epoch + 1);
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
        internal long _datagrams;

        /// <summary>The epoch this slot's current claim was published in; zero while unclaimed.</summary>
        internal long _claim;

        /// <summary>The epoch a datagram last touched this slot, which is what makes it reclaimable.</summary>
        internal long _lastSeen;

        internal int _port;
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SourceKey(ulong High, ulong Low, int Port);
