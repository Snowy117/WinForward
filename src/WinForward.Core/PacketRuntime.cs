using System.Buffers;

namespace WinForward.Core;

public enum PacketDisposition
{
    Pass,
    Block,
    ProxyConsumed
}

/// <summary>
/// A frame that lives in memory owned by its producer for the duration of the packet's dispatch
/// (a native capture buffer). The lease copies it into a managed pooled array lazily, the first
/// time a consumer needs a <see cref="ReadOnlyMemory{Byte}"/> view that may outlive the producer's
/// ownership window; consumers that finish inside the synchronous dispatch never pay the copy.
/// </summary>
public interface IFrameSource
{
    int FrameLength { get; }

    ReadOnlySpan<byte> GetFrameSpan();
}

public sealed class PacketLease : IDisposable
{
    [ThreadStatic]
    private static PacketLease? t_recycleCache;

    private IFrameSource? _source;
    private ReadOnlyMemory<byte> _frame;
    private byte[]? _rented;
    private readonly Action<ReadOnlyMemory<byte>>? _onCompleted;
    private PacketDisposition? _disposition;
    private int _completed;
    private bool _fromRecyclePool;

    public PacketLease(ReadOnlyMemory<byte> frame) => _frame = frame;

    /// <summary>
    /// Creates a lease whose frame is returned to its owner (for example an array pool) exactly
    /// once, on the lease's unique completion path. The callback fires inside
    /// <see cref="TryComplete(PacketDisposition)"/> (including the <see cref="Dispose"/> path), so
    /// any frame read must happen before completion when this constructor is used.
    /// </summary>
    public PacketLease(ReadOnlyMemory<byte> frame, Action<ReadOnlyMemory<byte>>? onCompleted)
    {
        _frame = frame;
        _onCompleted = onCompleted;
    }

    /// <summary>
    /// Creates a lease over a producer-owned frame. The source must stay valid for the whole
    /// dispatch of this packet; the first <see cref="Frame"/> access materializes a pooled copy
    /// that the capture processor releases after the dispatch completes.
    /// </summary>
    public PacketLease(IFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>
    /// The stable frame. Over a producer-owned source this materializes a pooled copy on first
    /// access, so readers that never touch the frame never allocate.
    /// </summary>
    public ReadOnlyMemory<byte> Frame => _source is null ? _frame : Materialize();

    /// <summary>True when no producer-owned source remains: the frame is a plain stable memory.</summary>
    public bool IsMaterialized => _source is null;

    public PacketDisposition? Disposition => _disposition;

    private ReadOnlyMemory<byte> Materialize()
    {
        var span = _source!.GetFrameSpan();
        var rented = ArrayPool<byte>.Shared.Rent(span.Length);
        span.CopyTo(rented);
        _rented = rented;
        _frame = new ReadOnlyMemory<byte>(rented, 0, span.Length);
        _source = null;
        return _frame;
    }

    /// <summary>
    /// Takes a lease over a producer-owned frame from the current thread's single-entry recycle
    /// cache, allocating only when the cache is empty. The taker must call <see cref="Release"/>
    /// on the SAME thread after the packet's dispatch completes; a recycled lease never carried a
    /// completion callback and its previous native source has gone out of scope by then.
    /// </summary>
    public static PacketLease TakeNative(IFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lease = t_recycleCache;
        if (lease is null)
        {
            lease = new PacketLease(source) { _fromRecyclePool = true };
            return lease;
        }

        t_recycleCache = null;
        lease._source = source;
        lease._frame = default;
        lease._rented = null;
        lease._disposition = null;
        lease._completed = 0;
        return lease;
    }

    /// <summary>
    /// Returns the materialized pooled array (if any) to the shared array pool and recycles this
    /// lease into the thread cache when it came from <see cref="TakeNative"/>. Every consumer of
    /// the frame must have finished; call from the dispatch thread only.
    /// </summary>
    public void Release()
    {
        if (_rented is { } rented)
        {
            _rented = null;
            ArrayPool<byte>.Shared.Return(rented);
        }

        if (!_fromRecyclePool) return;
        _source = null;
        _frame = default;
        _disposition = null;
        _completed = 0;
        RecycleToCache(this);
    }

    private static void RecycleToCache(PacketLease lease)
    {
        if (t_recycleCache is null) t_recycleCache = lease;
    }

    public bool TryComplete(PacketDisposition disposition)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
        _disposition = disposition;
        _onCompleted?.Invoke(Frame);
        return true;
    }

    public void Dispose() => TryComplete(PacketDisposition.Block);
}

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
