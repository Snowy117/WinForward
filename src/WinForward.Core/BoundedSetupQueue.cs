namespace WinForward.Core;

/// <summary>
/// A per-flow FIFO buffer for datagrams accepted while the flow's session is setting up. Dual
/// bounded in packets and bytes; the caller implements drop-oldest by dequeuing on overflow and
/// retrying. Entries hold a <see cref="NativeLease"/> rented from the udp-datagram pool (the
/// caller owns the payload copy), so enqueueing never materializes a managed datagram; the
/// queue takes ownership of a lease accepted by <see cref="TryEnqueue(NativeLease, int, DateTimeOffset)"/>
/// and leaves ownership with the caller when it refuses, which must release it exactly once. The
/// owner may re-stamp the queue in bulk when the age basis shifts (see
/// <see cref="RefreshEnqueuedStamps"/>). Not thread-safe by design: each queue is owned by one
/// flow's slot and every access is serialized by the owning coordinator's gate.
/// </summary>
public sealed class BoundedSetupQueue
{
    // The common UDP setup window buffers a single datagram (the DNS query that triggered the
    // flow), so the first buffered datagram lives inline and the Queue only materializes when a
    // second datagram overlaps the setup.
    private readonly record struct Entry(NativeLease Lease, int Length, DateTimeOffset EnqueuedAt);

    private Queue<Entry>? _items;
    private Entry _pending;
    private bool _hasPending;
    private readonly int _maxPackets;
    private readonly int _maxBytes;
    private int _bytes;

    public BoundedSetupQueue(int maxPackets, int maxBytes)
    {
        if (maxPackets <= 0) throw new ArgumentOutOfRangeException(nameof(maxPackets));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxPackets = maxPackets;
        _maxBytes = maxBytes;
    }

    public int Count => (_hasPending ? 1 : 0) + (_items?.Count ?? 0);
    public int Bytes => _bytes;

    /// <summary>
    /// Takes ownership of <paramref name="lease"/> (the caller copied the datagram's
    /// <paramref name="length"/> bytes into its span) when the queue accepts it. Both bounds
    /// (packets and bytes) are checked first; on refusal the lease is untouched and the caller
    /// keeps ownership.
    /// </summary>
    public bool TryEnqueue(NativeLease lease, int length, DateTimeOffset enqueuedAt)
    {
        if (length < 0 || length > lease.Length) return false;
        if (length > _maxBytes || Count >= _maxPackets || _bytes > _maxBytes - length) return false;
        var entry = new Entry(lease, length, enqueuedAt);
        if (_items is null)
        {
            if (!_hasPending)
            {
                _pending = entry;
                _hasPending = true;
                _bytes += length;
                return true;
            }

            _items = new Queue<Entry>(4);
            _items.Enqueue(_pending);
            _hasPending = false;
        }

        _items.Enqueue(entry);
        _bytes += length;
        return true;
    }

    public bool TryDequeue(out NativeLease lease, out int length) => TryDequeue(out lease, out length, out _);

    public bool TryDequeue(out NativeLease lease, out int length, out DateTimeOffset enqueuedAt)
    {
        Entry entry;
        if (_items is not null)
        {
            if (_items.Count == 0)
            {
                lease = default;
                length = 0;
                enqueuedAt = default;
                return false;
            }

            entry = _items.Dequeue();
        }
        else if (!_hasPending)
        {
            lease = default;
            length = 0;
            enqueuedAt = default;
            return false;
        }
        else
        {
            entry = _pending;
            _hasPending = false;
        }

        lease = entry.Lease;
        length = entry.Length;
        enqueuedAt = entry.EnqueuedAt;
        _bytes -= entry.Length;
        return true;
    }

    /// <summary>
    /// Re-stamps every buffered entry to <paramref name="enqueuedAt"/> and returns how many
    /// entries were refreshed. Membership, FIFO order, lease ownership, and byte accounting are
    /// untouched: only the ages observed by the flush TTL change. The setup path calls this when
    /// its flow's setup leaves the setup limiter (dial start) — datagrams buffered so far waited
    /// on admission, not on the client, so their staleness must not accrue from the enqueue.
    /// </summary>
    public int RefreshEnqueuedStamps(DateTimeOffset enqueuedAt)
    {
        var refreshed = 0;
        if (_hasPending)
        {
            _pending = _pending with { EnqueuedAt = enqueuedAt };
            refreshed++;
        }

        if (_items is { } items)
        {
            // Rotate the queue in place (bounded by the per-flow packet cap, cold path) so the
            // FIFO order survives the re-stamp.
            for (var index = 0; index < items.Count; index++)
            {
                var entry = items.Dequeue();
                items.Enqueue(entry with { EnqueuedAt = enqueuedAt });
                refreshed++;
            }
        }

        return refreshed;
    }
}