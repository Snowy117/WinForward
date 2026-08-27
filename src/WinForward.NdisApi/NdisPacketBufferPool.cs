using System.Collections.Concurrent;

namespace WinForward.NdisApi;

/// <summary>
/// A bounded pool of <see cref="NdisPacketBuffer"/> instances shared by the injection paths
/// (pass reinjection and TCP redirect injection), so a hot path no longer performs a
/// <c>NativeMemory.AllocZeroed</c>/<c>Free</c> pair per injected packet. Returned buffers beyond
/// <see cref="Capacity"/>, and buffers returned after disposal, are freed to the native heap
/// immediately. <see cref="Dispose"/> drains the pool and frees every idle buffer; renting from a
/// disposed pool still works (it allocates fresh buffers), so disposal is a trim rather than a
/// shutdown of the type. <see cref="Shared"/> is the process-wide instance used by the runtime.
/// </summary>
public sealed class NdisPacketBufferPool : IDisposable
{
    public const int DefaultCapacity = 256;

    private readonly ConcurrentQueue<NdisPacketBuffer> _buffers = new();
    private readonly int _capacity;
    private bool _disposed;

    public NdisPacketBufferPool(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>The process-wide pool used by the injection paths when none is injected.</summary>
    public static NdisPacketBufferPool Shared { get; } = new();

    public int Capacity => _capacity;
    public int Count => _buffers.Count;

    /// <summary>
    /// Rents a buffer, reusing a pooled instance when one is available. The renter owns the buffer
    /// until <see cref="NdisPacketBuffer.Dispose"/> (or <see cref="Return"/>) hands it back.
    /// </summary>
    public NdisPacketBuffer Rent()
    {
        while (_buffers.TryDequeue(out var buffer))
        {
            if (buffer.TryMarkRented()) return buffer;
        }
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
        Volatile.Write(ref _disposed, true);
        while (_buffers.TryDequeue(out var buffer)) buffer.ReleaseFromPool();
    }

    /// <summary>
    /// Called by <see cref="NdisPacketBuffer.Dispose"/> exactly once per return. A buffer is
    /// either enqueued for reuse, or freed when the pool is full or already disposed. A return
    /// racing a concurrent <see cref="Dispose"/> may land in the queue just after the drain; the
    /// buffer then stays allocated until process exit or the next drain, never duplicated.
    /// </summary>
    internal void OnReturned(NdisPacketBuffer buffer)
    {
        if (Volatile.Read(ref _disposed) || _buffers.Count >= _capacity)
        {
            buffer.ReleaseFromPool();
            return;
        }
        _buffers.Enqueue(buffer);
    }
}
