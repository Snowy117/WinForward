namespace WinForward.Runtime;

/// <summary>
/// Counted in-flight tracking with a seal-and-join drain for an owner of asynchronous work.
/// </summary>
/// <remarks>
/// <para>
/// An owner admits every piece of work that touches a resource it must outlive through
/// <see cref="TryEnter(out WorkLease)"/> (hot path) or <see cref="Run(Func{CancellationToken, Task}, string)"/>
/// (cold path), and releases the returned lease when that work completes. <see cref="DrainAsync"/>
/// seals the scope so no further work can be admitted, cancels the scope's own token, and completes
/// only after every outstanding lease has been released.
/// </para>
/// <para>
/// The scope's state is a single packed word (bit 0 = sealed, bits 1+ = pending count) mutated only by
/// interlocked operations, so it is a leaf: it acquires no lock and invokes no owner code. An owner may
/// therefore hold its own gate across <see cref="TryEnter(out WorkLease)"/>; no lock order can invert.
/// </para>
/// <para>
/// Lease rule: a lease is idempotent per copy, but disposing two different copies of the same lease
/// releases the scope twice. Exactly one copy may be disposed.
/// </para>
/// <para>
/// Lifetime: <see cref="Token"/> is owned by the scope and released once <see cref="DrainAsync"/>
/// completes, so it must not be read after that point. The drain completion is a single cell created
/// at seal, so every <see cref="DrainAsync"/> caller — concurrent or sequential — receives the same
/// <see cref="Task"/> instance.
/// </para>
/// </remarks>
internal sealed class QuiescenceScope(CancellationToken linkedTo = default) : IAsyncDisposable
{
    private const int Sealed = 1;
    private const int PendingStep = 2;

    private readonly CancellationTokenSource _cts = linkedTo.CanBeCanceled
        ? CancellationTokenSource.CreateLinkedTokenSource(linkedTo)
        : new CancellationTokenSource();

    private int _state;
    private TaskCompletionSource? _drained;
    private TaskCompletionSource? _joined;
    private Exception? _fault;
    private string? _faultSite;

    public CancellationToken Token => _cts.Token;

    public bool IsIdle => Volatile.Read(ref _state) >> 1 == 0;

    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// The call site of the first recorded fault, or <see langword="null"/> when none was supplied.
    /// Only meaningful while <see cref="Fault"/> is non-null.
    /// </summary>
    public string? FaultSite => Volatile.Read(ref _faultSite);

    /// <summary>
    /// Admits one piece of work. Returns <see langword="false"/> once the scope is sealed, in which
    /// case <paramref name="lease"/> is <see langword="default"/> and nothing is accounted.
    /// </summary>
    public bool TryEnter(out WorkLease lease)
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if ((current & Sealed) != 0)
            {
                lease = default;
                return false;
            }

            if (Interlocked.CompareExchange(ref _state, current + PendingStep, current) == current)
            {
                lease = new WorkLease(this);
                return true;
            }
        }
    }

    /// <summary>
    /// Records the first fault observed from an admitted child, together with the call site that
    /// reported it. Later faults are discarded; siblings are not cancelled and
    /// <see cref="DrainAsync"/> does not throw because of a recorded fault.
    /// </summary>
    /// <param name="exception">The fault to record.</param>
    /// <param name="site">
    /// Diagnostic name of the reporting site — the <c>name</c> of a <see cref="Run(Func{CancellationToken, Task}, string)"/>
    /// child, for example. Only the first fault's site is retained and it is exposed as
    /// <see cref="FaultSite"/>.
    /// </param>
    public void RecordFault(Exception exception, string? site = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.CompareExchange(ref _fault, exception, comparand: null) is null)
        {
            Volatile.Write(ref _faultSite, site);
        }
    }

    /// <summary>Cancels the scope-owned token. A no-op once the scope has drained.</summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The scope already drained and released its token; there is nothing left to cancel.
        }
    }

    /// <summary>
    /// Starts one scope-owned child and returns <see langword="false"/> without invoking
    /// <paramref name="body"/> once the scope is sealed. The child is admitted to the in-flight set,
    /// so <see cref="DrainAsync"/> joins it; a fault it throws is recorded and swallowed, and is
    /// observed through <see cref="Fault"/> (with <paramref name="name"/> as its
    /// <see cref="FaultSite"/>). <paramref name="name"/> identifies the child's call site for
    /// diagnostics.
    /// </summary>
    public bool Run(Func<CancellationToken, Task> body, string name)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!TryEnter(out var lease))
        {
            return false;
        }

        // The task is deliberately not handed out: the lease owns the child's lifetime, and the
        // scope is the observation point. Swallowing the fault keeps an abandoned child from
        // surfacing as an unobserved task exception.
        _ = RunChildAsync(body, lease, name);
        return true;
    }

    /// <summary>
    /// Seals the scope, cancels the owned token, and completes once every outstanding lease has been
    /// released. Single-flight and idempotent; never throws for a child fault.
    /// </summary>
    public Task DrainAsync()
    {
        // The completion cell is allocated at most once, at seal. Every caller — including the
        // sealer — observes this same cell's Task, so all of them wait on one completion signal.
        var drained = LazyInitializer.EnsureInitialized(
            ref _drained,
            static () => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        // Single-writer seal: exactly one caller observes an unsealed prior word and owns the drain.
        if ((Interlocked.Or(ref _state, Sealed) & Sealed) != 0)
        {
            return drained.Task;
        }

        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _joined, joined);
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _state) == Sealed)
        {
            // Already idle when sealed: the last exit could have observed the unsealed word, so the
            // sealer re-checks after publishing the join cell to close that window. The barrier makes
            // the published cell globally visible before the re-check, so an exit that has not yet
            // decremented to Sealed is guaranteed to observe the cell.
            joined.TrySetResult();
        }

        // The drain runs detached: callers await `drained.Task`, which completes after the leases are
        // joined and the owned CTS is released. DrainCoreAsync contains its own faults, so the
        // detached task cannot surface as an unobserved task exception.
        _ = DrainCoreAsync(drained, joined);
        return drained.Task;
    }

    public ValueTask DisposeAsync() => new(DrainAsync());

    private async Task DrainCoreAsync(TaskCompletionSource drained, TaskCompletionSource joined)
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await joined.Task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordFault(exception);
        }
        finally
        {
            _cts.Dispose();
            drained.TrySetResult();
        }
    }

    private async Task RunChildAsync(Func<CancellationToken, Task> body, WorkLease lease, string name)
    {
        try
        {
            await body(Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordFault(exception, name);
        }
        finally
        {
            lease.Dispose();
        }
    }

    internal void Exit()
    {
        if (Interlocked.Add(ref _state, -PendingStep) == Sealed)
        {
            Volatile.Read(ref _joined)?.TrySetResult();
        }
    }
}

/// <summary>
/// A mutable, allocation-free ticket returned by <see cref="QuiescenceScope.TryEnter(out WorkLease)"/>.
/// Release it exactly once; disposal is idempotent per copy, so a second <see cref="Dispose"/> on the
/// same copy is a no-op. Keep it unboxed as a local — <c>using IDisposable lease = ...</c> or an
/// interface/object-typed field boxes it and reintroduces a hot-path allocation.
/// </summary>
internal struct WorkLease : IDisposable
{
    private QuiescenceScope? _scope;

    internal WorkLease(QuiescenceScope scope) => _scope = scope;

    public void Dispose()
    {
        var scope = _scope;
        if (scope is null)
        {
            return;
        }

        _scope = null;
        scope.Exit();
    }
}
