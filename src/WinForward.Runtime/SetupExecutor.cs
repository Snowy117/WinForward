using System.Collections.Concurrent;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Runtime;

/// <summary>
/// One pooled unit of new-flow setup work: rented from an <see cref="ISetupExecutor"/>, populated by
/// a coordinator's cold new-flow branch, enqueued, and recycled by the worker that ran it. Reuse is
/// safe because <see cref="Reset"/> clears every payload reference before the slot returns to the
/// free list.
/// </summary>
public sealed class SetupWorkItem
{
    /// <summary>
    /// The item's setup pipeline, installed by <see cref="ISetupExecutor.RentItem"/> at rent time
    /// and never null while the item is in flight. A rented item therefore always has a pipeline.
    /// </summary>
    internal Func<SetupWorkItem, Task> Handler = null!;

    internal TaskCompletionSource? Completion;
    internal FlowKey Flow;
    internal Socks5Server? Server;
    internal CancellationToken CancellationToken;

    internal PendingSynSetup? TcpEntry;
    internal byte[]? TcpFrame;

    internal long FlowGeneration;
    internal MacAddress ClientMac;
    internal UdpProxyCoordinator.UdpSessionSlot? UdpSlot;

    internal void Reset()
    {
        Handler = null!;
        Completion = null;
        Flow = default;
        Server = null;
        CancellationToken = default;
        TcpEntry = null;
        TcpFrame = null;
        FlowGeneration = 0;
        ClientMac = default;
        UdpSlot = null;
    }
}

/// <summary>The seam coordinators use to hand new-flow setup off the pump thread.</summary>
/// <remarks>
/// This seam exists for composition and test infrastructure: the production adapter is
/// <see cref="SetupExecutor"/>, and callers may inject their own to change the enqueue policy.
/// No substituting fake ships with it, because none would exercise a scenario beyond what the
/// real executor's own tests already cover (pool balance, capacity rejection, worker survival
/// after a fault, and the dispose drain) — tests assert the ring semantics against the real
/// executor instead.
/// </remarks>
public interface ISetupExecutor : IDisposable
{
    /// <summary>
    /// Rents one pooled item whose pipeline is <paramref name="handler"/>, invoked by an executor
    /// worker until its task completes. The handler is required: it is the item's only pipeline,
    /// and a handler-less item is unrepresentable by construction.
    /// </summary>
    SetupWorkItem RentItem(Func<SetupWorkItem, Task> handler);

    /// <summary>Enqueues one populated item; false when the bounded ring is full (fail-closed).</summary>
    bool TryEnqueue(SetupWorkItem item);
}

/// <summary>
/// A fixed set of dedicated background threads (started lazily on the first enqueue) draining a
/// bounded MPMC ring of preallocated work items. Each worker invokes the item's asynchronous setup
/// pipeline and blocks on the dedicated thread until it completes, so the capture pump never waits
/// on listener bind or SOCKS5 dial and the thread pool is not starved by setup work. The ring is
/// bounded and refuses beyond capacity; slots recycle through a bounded free list.
/// </summary>
public sealed class SetupExecutor : ISetupExecutor
{
    /// <summary>
    /// Bound on queued setup items. It must not be smaller than the TCP pending-SYN index cap
    /// (<see cref="TcpRedirect.TcpPendingSynSetupIndex.DefaultCapacity"/>): that index can retain
    /// a full cap of pending SYNs at once and every one of them enqueues here, so a smaller ring
    /// could reject setups under load. The load-bearing relation is this ordering inequality, not
    /// an equality — a larger ring is always safe.
    /// </summary>
    public const int DefaultRingCapacity = 1_024;

    /// <summary>
    /// 2x the logical processor count, floored at 16 so the 8-wide UDP setup limiter (plus the
    /// queued-flow dial-start probe) stays drainable on small hosts.
    /// </summary>
    public static readonly int DefaultWorkerCount = Math.Max(2 * Environment.ProcessorCount, 16);

    private readonly ConcurrentQueue<SetupWorkItem> _ring = new();
    private readonly ConcurrentQueue<SetupWorkItem> _free = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _workerGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _workerCount;
    private readonly int _capacity;
    private Thread[]? _workers;
    private int _pendingCount;
    private int _disposed;
    private long _enqueuedCount;
    private long _completedCount;
    private long _rejectedCount;
    private long _overflowAllocations;

    /// <summary>
    /// Creates the executor. <paramref name="workerCount"/> null selects
    /// <see cref="DefaultWorkerCount"/>; <paramref name="ringCapacity"/> bounds the queued items.
    /// </summary>
    public SetupExecutor(int? workerCount = null, int ringCapacity = DefaultRingCapacity)
    {
        if (workerCount is < 0) throw new ArgumentOutOfRangeException(nameof(workerCount));
        if (ringCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(ringCapacity));
        _workerCount = workerCount ?? DefaultWorkerCount;
        _capacity = ringCapacity;
    }

    internal int PendingCount => Volatile.Read(ref _pendingCount);
    internal int FreeCount => _free.Count;

    /// <summary>True once <see cref="Dispose"/> has begun; new work is refused and workers stop.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal long EnqueuedCount => Interlocked.Read(ref _enqueuedCount);
    internal long CompletedCount => Interlocked.Read(ref _completedCount);
    internal long RejectedCount => Interlocked.Read(ref _rejectedCount);

    /// <summary>Rents that missed the free list and allocated fresh (sizing diagnostic; zero at steady state).</summary>
    internal long OverflowAllocations => Interlocked.Read(ref _overflowAllocations);

    public SetupWorkItem RentItem(Func<SetupWorkItem, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_free.TryDequeue(out var item))
        {
            item.Handler = handler;
            return item;
        }

        Interlocked.Increment(ref _overflowAllocations);
        return new SetupWorkItem { Handler = handler };
    }

    public bool TryEnqueue(SetupWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Volatile.Read(ref _disposed) != 0)
        {
            Recycle(item);
            return false;
        }
        if (Interlocked.Increment(ref _pendingCount) > _capacity)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _rejectedCount);
            Recycle(item);
            return false;
        }

        EnsureWorkers();
        _ring.Enqueue(item);
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced the enqueue; the item is fail-closed rather than surfaced to the caller.
        }

        Interlocked.Increment(ref _enqueuedCount);
        return true;
    }

    private void Recycle(SetupWorkItem item)
    {
        item.Reset();
        if (_free.Count < _capacity) _free.Enqueue(item);
    }

    private void EnsureWorkers()
    {
        if (Volatile.Read(ref _workers) is not null) return;
        lock (_workerGate)
        {
            if (_workers is not null) return;
            var workers = new Thread[_workerCount];
            for (var index = 0; index < workers.Length; index++)
            {
                var thread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = $"wf-setup-{index}",
                };
                workers[index] = thread;
                thread.Start();
            }
            Volatile.Write(ref _workers, workers);
        }
    }

    private void WorkerLoop()
    {
        var token = _shutdown.Token;
        while (true)
        {
            try
            {
                _signal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_ring.TryDequeue(out var item)) continue;
            if (IsDisposed)
            {
                DrainItem(item);
                return;
            }
            Execute(item);
        }
    }

    private void Execute(SetupWorkItem item)
    {
        try
        {
            // The dedicated worker thread intentionally blocks until the asynchronous setup pipeline
            // completes: that is the whole point of the executor (no thread-pool thread is parked).
#pragma warning disable VSTHRD002
            item.Handler(item).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            item.Completion?.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            item.Completion?.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            item.Completion?.TrySetException(exception);
        }
        finally
        {
            item.Reset();
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _completedCount);
            if (_free.Count < _capacity) _free.Enqueue(item);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        if (Volatile.Read(ref _workers) is { } workers)
        {
            foreach (var thread in workers) thread.Join();
        }

        while (_ring.TryDequeue(out var item)) DrainItem(item);

        _signal.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Fail-closes one item that never started: cancels its completion, decrements the pending
    /// count, and recycles the slot. Shared by <see cref="Dispose"/> and a worker that observes
    /// <see cref="IsDisposed"/> after dequeuing, so both sinks account identically.
    /// </summary>
    private void DrainItem(SetupWorkItem item)
    {
        Interlocked.Decrement(ref _pendingCount);
        item.Completion?.TrySetCanceled();
        item.Reset();
        if (_free.Count < _capacity) _free.Enqueue(item);
    }
}