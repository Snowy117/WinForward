using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WinForward.Core;

/// <summary>
/// A bounded pool of fixed-size native buffers for steady-state application paths that must not
/// allocate on the managed heap (relay pump windows, UDP receive windows, retained SYN copies).
/// Buffers are raw <see cref="NativeMemory.AllocZeroed"/> storage; each allocation carries a
/// one-word rental-state cell immediately before the payload so the lightweight
/// <see cref="NativeLease"/> handle stays a struct with idempotent release across copies.
/// Semantics mirror the M1-hardened <c>NdisPacketBufferPool</c>: a <see cref="ConcurrentQueue{T}"/>
/// free-list bounded by <see cref="Capacity"/>, interlocked rent/return accounting exposed through
/// <see cref="Stats"/>, an optional <see cref="AccountingSink"/> for process-wide diagnostics, and
/// a dispose that drains the queue (a return racing a concurrent dispose is freed by the returner's
/// post-enqueue recheck, never stranded until process exit). Renting from a disposed pool still
/// works (it allocates fresh), so disposal is a trim rather than a shutdown of the type.
/// </summary>
public sealed unsafe class NativeBufferPool : IDisposable
{
    public const int DefaultCapacity = 256;

    private const int StateIdle = 2;
    private const int StateRented = 1;

    private readonly ConcurrentQueue<NativeLease> _buffers = new();
    private readonly int _capacity;
    private readonly int _bufferSize;
    private int _disposedState;
    private long _rented;
    private long _returned;
    private long _inPool;
    private long _disposedCount;
    private long _overflowAllocations;

    public NativeBufferPool(int bufferSize, int capacity = DefaultCapacity)
    {
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _bufferSize = bufferSize;
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    /// <summary>The payload bytes every buffer of this pool provides; the rental-state word is additional.</summary>
    public int BufferSize => _bufferSize;

    public int Count => _buffers.Count;

    /// <summary>
    /// Optional per-rent/return accounting sink for process-wide diagnostics (the RuntimeCounters
    /// pool registry): invoked once per successful rent (<c>true</c>) and once per completed
    /// return (<c>false</c>). Composition sets it once at startup, before any consumer can rent;
    /// it stays <c>null</c> when unwired. Diagnostics only — the sink must not throw and never
    /// influences pool behavior.
    /// </summary>
    public Action<bool>? AccountingSink { get; set; }

    /// <summary>Point-in-time rent/return accounting for diagnostics and balance tests.</summary>
    internal NativeBufferPoolStats Stats => new(
        Interlocked.Read(ref _rented),
        Interlocked.Read(ref _returned),
        Interlocked.Read(ref _inPool),
        Interlocked.Read(ref _disposedCount),
        Interlocked.Read(ref _overflowAllocations));

    /// <summary>
    /// Rents a buffer, reusing a pooled instance when one is available. The renter owns the buffer
    /// until <see cref="NativeLease.Dispose"/> hands it back; the lease may be copied freely, and a
    /// release through any copy is idempotent (the rental-state cell admits exactly one release per
    /// rental window — a stale release after the pool has re-rented the same storage is a
    /// rental-contract violation documented on <see cref="NativeLease"/>, confined to that renter).
    /// </summary>
    public NativeLease Rent()
    {
        while (_buffers.TryDequeue(out var lease))
        {
            Interlocked.Decrement(ref _inPool);
            if (TryMarkRented(lease._pointer))
            {
                Interlocked.Increment(ref _rented);
                AccountingSink?.Invoke(true);
                return lease;
            }
        }
        // No reusable buffer was available: allocate fresh and account the overflow so a sizing
        // error is visible in diagnostics instead of silently churning native allocations.
        var allocation = (byte*)NativeMemory.AllocZeroed((nuint)sizeof(int) + (nuint)_bufferSize);
        if (allocation is null) throw new InvalidOperationException("Unable to allocate a native buffer.");
        var pointer = allocation + sizeof(int);
        Volatile.Write(ref *(int*)allocation, StateRented);
        Interlocked.Increment(ref _overflowAllocations);
        Interlocked.Increment(ref _rented);
        AccountingSink?.Invoke(true);
        return new NativeLease(this, pointer, _bufferSize, new NativeMemoryManager(pointer, _bufferSize));
    }

    /// <summary>
    /// Called by <see cref="NativeLease.Dispose"/> exactly once per rental window (the in-band
    /// state cell's CAS rejects every further copy). A buffer is either enqueued for reuse, or
    /// freed when the pool is full or already disposed. A return racing a concurrent
    /// <see cref="Dispose"/> may pass the disposed check just before the flag is set and enqueue
    /// after the disposer's drain already saw an empty queue; the post-enqueue recheck below
    /// closes that window by draining on the returner's side — one of the two drains always
    /// observes the buffer, so a raced return is freed now, never stranded until process exit and
    /// never freed twice.
    /// </summary>
    internal void Release(NativeLease lease)
    {
        var allocation = (byte*)lease._pointer - sizeof(int);
        if (Interlocked.CompareExchange(ref *(int*)allocation, StateIdle, StateRented) != StateRented) return;
        Interlocked.Increment(ref _returned);
        AccountingSink?.Invoke(false);
        if (Volatile.Read(ref _disposedState) != 0 || Volatile.Read(ref _inPool) >= _capacity)
        {
            NativeMemory.Free(allocation);
            Interlocked.Increment(ref _disposedCount);
            return;
        }
        _buffers.Enqueue(lease);
        Interlocked.Increment(ref _inPool);
        if (Volatile.Read(ref _disposedState) != 0) DrainQueuedBuffers();
    }

    private static bool TryMarkRented(void* pointer) =>
        Interlocked.CompareExchange(ref *(int*)((byte*)pointer - sizeof(int)), StateRented, StateIdle) == StateIdle;

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposedState, 1);
        DrainQueuedBuffers();
    }

    /// <summary>
    /// Frees every buffer currently sitting in the queue. Shared by <see cref="Dispose"/> and the
    /// post-enqueue recheck in <see cref="Release"/>: the two drainers race safely because the
    /// queue hands each lease to exactly one <c>TryDequeue</c>.
    /// </summary>
    private void DrainQueuedBuffers()
    {
        while (_buffers.TryDequeue(out var lease))
        {
            Interlocked.Decrement(ref _inPool);
            NativeMemory.Free((byte*)lease._pointer - sizeof(int));
            Interlocked.Increment(ref _disposedCount);
        }
    }

}

/// <summary>
/// A lightweight handle over one <see cref="NativeBufferPool"/> buffer: the pool reference, the
/// payload pointer, and the length. The handle is a copyable struct; <see cref="Dispose"/> returns
/// the buffer to its pool and is idempotent across copies (exactly one release per rental window
/// succeeds — the rental-state cell lives in-band, before the payload). The default value carries
/// no pool and disposing it is a no-op. The span is valid until the first release through any copy;
/// like every native rental, a stale release after the pool has re-rented the same storage would
/// hand the buffer back under its current renter, so renters must release exactly once per rental.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly unsafe struct NativeLease : IDisposable
{
    internal readonly NativeBufferPool? _pool;
    internal readonly void* _pointer;
    private readonly int _length;
    private readonly NativeMemoryManager? _manager;

    internal NativeLease(NativeBufferPool pool, void* pointer, int length, NativeMemoryManager manager)
    {
        _pool = pool;
        _pointer = pointer;
        _length = length;
        _manager = manager;
    }

    /// <summary>The payload bytes this lease provides (excludes the in-band rental-state word).</summary>
    public int Length => _length;

    /// <summary>A writable view of the buffer; valid until the lease is released.</summary>
    public Span<byte> Span => new(_pointer, _length);

    /// <summary>
    /// A <see cref="Memory{T}"/> view for asynchronous APIs (<c>ReadAsync</c>, <c>WriteAsync</c>,
    /// <c>ReceiveAsync</c>) that cannot take a <see cref="Span{T}"/>. The view is backed by the
    /// per-allocation <see cref="NativeMemoryManager"/> carried on this lease, so obtaining it is
    /// allocation-free; valid until the lease is released. A default lease yields an empty memory.
    /// </summary>
    public Memory<byte> Memory => _manager is null ? default : _manager.Memory;

    public void Dispose() => _pool?.Release(this);
}

/// <summary>
/// Bridges one pooled native buffer to <see cref="Memory{T}"/> so async socket and stream APIs can
/// consume pooled native storage without a managed copy. Exactly one manager is constructed per
/// fresh native allocation in <see cref="NativeBufferPool.Rent"/> (the cold/overflow path) and then
/// travels with its buffer through the free list, so steady-state rents reuse it and allocate
/// nothing. The native storage never moves, so <see cref="Pin"/> hands out the raw pointer and
/// <see cref="Unpin"/> is a no-op.
/// </summary>
internal sealed unsafe class NativeMemoryManager : MemoryManager<byte>
{
    private readonly void* _pointer;
    private readonly int _length;

    public NativeMemoryManager(void* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);
        return new MemoryHandle((byte*)_pointer + elementIndex, pinnable: this);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// Point-in-time rent/return accounting for one <see cref="NativeBufferPool"/>. Totals are
/// cumulative since pool construction; <see cref="InPool"/> tracks idle queue occupancy. The
/// balance identity <c>OverflowAllocations == DisposedCount + InPool + Outstanding</c> holds once
/// every renter has returned (every allocation is either freed by the pool, idle in the queue, or
/// still checked out), which is what the pool balance tests assert.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct NativeBufferPoolStats(
    long Rented,
    long Returned,
    long InPool,
    long DisposedCount,
    long OverflowAllocations)
{
    /// <summary>Buffers currently checked out by renters (rents minus returns).</summary>
    public long Outstanding => Rented - Returned;
}
