using System.Buffers;

namespace WinForward.Core;

public enum PacketDisposition
{
    Pass,
    Block,
    ProxyConsumed,
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
#pragma warning disable IDE1006 // The t_ prefix is the team convention for [ThreadStatic] fields (2026-09-19): the editorconfig naming rules cannot match attributes, so the s_ rule for internal/private static fields would otherwise claim this field and rename it away from its thread-local marker.
    private static PacketLease? t_recycleCache;
#pragma warning restore IDE1006

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
    /// any frame read must happen before completion when this constructor is used. The callback
    /// shape is deliberately memory-based: a completion callback can never be combined with a
    /// producer-owned source (see <see cref="PacketLease(IFrameSource)"/>), so completing the
    /// lease can never become the path that materializes a native frame.
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

    internal PacketDisposition? Disposition => _disposition;

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
            return new PacketLease(source) { _fromRecyclePool = true };
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
        t_recycleCache ??= lease;
    }

    public bool TryComplete(PacketDisposition disposition)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
        _disposition = disposition;
        // The callback constructor only ever wraps stable memory (_source stays null there), so
        // invoking the callback on the stored frame can never materialize a pooled copy. The
        // Frame property is deliberately unreachable here: completion must not be the path that
        // materializes a producer-owned native frame.
        _onCompleted?.Invoke(_frame);
        return true;
    }

    public void Dispose() => TryComplete(PacketDisposition.Block);
}
