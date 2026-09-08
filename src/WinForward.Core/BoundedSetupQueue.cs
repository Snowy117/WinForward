namespace WinForward.Core;

/// <summary>
/// A per-flow FIFO buffer for datagrams accepted while the flow's session is setting up. Dual
/// bounded in packets and bytes; the caller implements drop-oldest by dequeuing on overflow and
/// retrying. Entries may carry an enqueue timestamp (R4 setup TTL); the timestamp-free overloads
/// forward with a default stamp, which carries no age, and the owner may re-stamp the queue in
/// bulk when the age basis shifts (see <see cref="RefreshEnqueuedStamps"/>). Not thread-safe by
/// design: each queue is owned by one flow's slot and every access is serialized by the owning
/// coordinator's gate.
/// </summary>
public sealed class BoundedSetupQueue
{
    // The common UDP setup window buffers a single datagram (the DNS query that triggered the
    // flow), so the first buffered frame lives inline and the Queue only materializes when a
    // second datagram overlaps the setup.
    private readonly record struct Entry(ReadOnlyMemory<byte> Frame, DateTimeOffset EnqueuedAt);

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

    public bool TryEnqueue(ReadOnlyMemory<byte> frame) => TryEnqueue(frame, default);

    public bool TryEnqueue(ReadOnlyMemory<byte> frame, DateTimeOffset enqueuedAt)
    {
        if (frame.Length > _maxBytes || Count >= _maxPackets || _bytes > _maxBytes - frame.Length) return false;
        var copy = frame.ToArray();
        var entry = new Entry(copy, enqueuedAt);
        if (_items is null)
        {
            if (!_hasPending)
            {
                _pending = entry;
                _hasPending = true;
                _bytes += copy.Length;
                return true;
            }

            _items = new Queue<Entry>(4);
            _items.Enqueue(_pending);
            _hasPending = false;
        }

        _items.Enqueue(entry);
        _bytes += copy.Length;
        return true;
    }

    public bool TryDequeue(out ReadOnlyMemory<byte> frame) => TryDequeue(out frame, out _);

    public bool TryDequeue(out ReadOnlyMemory<byte> frame, out DateTimeOffset enqueuedAt)
    {
        Entry entry;
        if (_items is not null)
        {
            if (_items.Count == 0)
            {
                frame = default;
                enqueuedAt = default;
                return false;
            }

            entry = _items.Dequeue();
        }
        else if (!_hasPending)
        {
            frame = default;
            enqueuedAt = default;
            return false;
        }
        else
        {
            entry = _pending;
            _hasPending = false;
        }

        frame = entry.Frame;
        enqueuedAt = entry.EnqueuedAt;
        _bytes -= entry.Frame.Length;
        return true;
    }

    /// <summary>
    /// Re-stamps every buffered entry to <paramref name="enqueuedAt"/> and returns how many
    /// entries were refreshed. Membership, FIFO order, and byte accounting are untouched: only
    /// the ages observed by the flush TTL change. The setup path calls this when its flow's
    /// setup leaves the setup limiter (dial start) — datagrams buffered so far waited on
    /// admission, not on the client, so their staleness must not accrue from the enqueue.
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
