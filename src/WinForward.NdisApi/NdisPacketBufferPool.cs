using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WinForward.NdisApi;

/// <summary>
/// A bounded pool of <see cref="NdisPacketBuffer"/> instances shared by the injection paths
/// (pass reinjection and TCP redirect injection), so a hot path no longer performs a
/// <c>NativeMemory.AllocZeroed</c>/<c>Free</c> pair per injected packet. Returned buffers beyond
/// <see cref="Capacity"/>, and buffers returned after disposal, are freed to the native heap
/// immediately. <see cref="Dispose"/> drains the pool and frees every idle buffer; renting from a
/// disposed pool still works (it allocates fresh buffers), so disposal is a trim rather than a
/// shutdown of the type. <see cref="Shared"/> is the process-wide instance used by the runtime.
/// Rent/return accounting is exposed lock-free through <see cref="Stats"/> (interlocked counters
/// only; the rent/return paths take no locks).
/// </summary>
public sealed class NdisPacketBufferPool : IDisposable
{
    private const int DefaultCapacity = 256;
    private readonly ConcurrentQueue<NdisPacketBuffer> _buffers = new();
    private int _disposedState;
    private long _rented;
    private long _returned;
    private long _inPool;
    private long _disposedCount;
    private long _overflowAllocations;

    public NdisPacketBufferPool(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    /// <summary>The process-wide pool used by the injection paths when none is injected.</summary>
    public static NdisPacketBufferPool Shared { get; } = new();

    /// <summary>
    /// Optional per-rent/return accounting sink for process-wide diagnostics (the RuntimeCounters
    /// pool registry): invoked once per successful rent (<see langword="true"/>) and once per completed
    /// return (<see langword="false"/>). Composition sets it once at startup, before any capture pump can
    /// rent; it stays <see langword="null"/> when unwired. Diagnostics only — the sink must not throw (the
    /// production sink, interlocked counter increments over constant keys, cannot) and never
    /// influences pool behavior.
    /// </summary>
    public Action<bool>? AccountingSink { get; set; }

    private int Capacity { get; }
    public int Count => _buffers.Count;

    /// <summary>Point-in-time rent/return accounting for diagnostics and balance tests.</summary>
    public NdisPacketBufferPoolStats Stats => new(
        Interlocked.Read(ref _rented),
        Interlocked.Read(ref _returned),
        Interlocked.Read(ref _inPool),
        Interlocked.Read(ref _disposedCount),
        Interlocked.Read(ref _overflowAllocations));

    /// <summary>
    /// Rents a buffer, reusing a pooled instance when one is available. The renter owns the buffer
    /// until <see cref="NdisPacketBuffer.Dispose"/> (or <see cref="Return"/>) hands it back.
    /// </summary>
    public NdisPacketBuffer Rent()
    {
        while (_buffers.TryDequeue(out var buffer))
        {
            Interlocked.Decrement(ref _inPool);
            if (buffer.TryMarkRented())
            {
                Interlocked.Increment(ref _rented);
                AccountingSink?.Invoke(true);
                return buffer;
            }
        }
        // No reusable buffer was available: allocate fresh and account the overflow so a sizing
        // error is visible in diagnostics instead of silently churning native allocations.
        Interlocked.Increment(ref _overflowAllocations);
        Interlocked.Increment(ref _rented);
        AccountingSink?.Invoke(true);
        return new NdisPacketBuffer(this);
    }

    /// <summary>
    /// Returns a rented buffer to the pool. Equivalent to disposing the rented buffer; passing a
    /// buffer that was not rented from this pool (or was already returned) is rejected.
    /// </summary>
    public void Return(NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!buffer.IsRentedFrom(this)) throw new ArgumentException("The buffer is not rented from this pool.", nameof(buffer));
        buffer.Dispose();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposedState, 1);
        DrainQueuedBuffers();
    }

    /// <summary>
    /// Frees every buffer currently sitting in the queue. Shared by <see cref="Dispose"/> and the
    /// post-enqueue recheck in <see cref="OnReturned"/>: the two drainers race safely because the
    /// queue hands each buffer to exactly one <c>TryDequeue</c>.
    /// </summary>
    private void DrainQueuedBuffers()
    {
        while (_buffers.TryDequeue(out var buffer))
        {
            Interlocked.Decrement(ref _inPool);
            ReleaseBuffer(buffer);
        }
    }

    private void ReleaseBuffer(NdisPacketBuffer buffer)
    {
        buffer.ReleaseFromPool();
        Interlocked.Increment(ref _disposedCount);
    }

    /// <summary>
    /// Called by <see cref="NdisPacketBuffer.Dispose"/> exactly once per return. A buffer is
    /// either enqueued for reuse, or freed when the pool is full or already disposed. A return
    /// racing a concurrent <see cref="Dispose"/> may pass the disposed check just before the
    /// flag is set and enqueue after the disposer's drain already saw an empty queue; the
    /// post-enqueue recheck below closes that window by draining on the returner's side — one of
    /// the two drains always observes the buffer (the disposer's drain sees every enqueue that
    /// completed before it), so a raced return is freed now, never stranded until process exit and
    /// never freed twice.
    /// </summary>
    internal void OnReturned(NdisPacketBuffer buffer)
    {
        Interlocked.Increment(ref _returned);
        AccountingSink?.Invoke(false);
        if (Volatile.Read(ref _disposedState) != 0 || Volatile.Read(ref _inPool) >= Capacity)
        {
            ReleaseBuffer(buffer);
            return;
        }
        _buffers.Enqueue(buffer);
        Interlocked.Increment(ref _inPool);
        if (Volatile.Read(ref _disposedState) != 0) DrainQueuedBuffers();
    }
}

/// <summary>
/// Point-in-time rent/return accounting for one <see cref="NdisPacketBufferPool"/>. Totals are
/// cumulative since pool construction; <see cref="InPool"/> tracks idle queue occupancy. The
/// balance identity <c>OverflowAllocations == DisposedCount + InPool + Outstanding</c> holds once
/// every renter has returned (every allocation is either freed by the pool, idle in the queue, or
/// still checked out), which is what the pool balance tests assert.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NdisPacketBufferPoolStats(
    long Rented,
    long Returned,
    long InPool,
    long DisposedCount,
    long OverflowAllocations)
{
    /// <summary>Buffers currently checked out by renters (rents minus returns).</summary>
    public long Outstanding => Rented - Returned;
}
