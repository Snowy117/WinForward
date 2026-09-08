using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// Runs capture generations across Windows adapter-list changes (task 09-07-adapter-list-refresh,
/// design §3.5). Generation 0 resolves its scope with fail-closed startup semantics; every later
/// generation is rebuilt through the refresh pipeline: the change source — or a pump degradation
/// with native error 87 (R3) — raises a refresh demand, the runner waits out the storm-guard
/// interval, re-enumerates, and diffs the fresh in-scope link state (stable ID → handle/MAC/MTU)
/// against the running generation's. An identical diff is a logged no-op that never touches the
/// running pumps; a real change stops the current generation (its runtime cleanup performs the
/// best-effort mode restore), swaps the durable layer's adapter views via <c>onScopeInstalled</c>,
/// and starts the next generation on fresh handles. The durable layer is disposed exactly once,
/// after the final generation completes. A generation fault is fail-closed: it propagates out of
/// <see cref="RunAsync"/> after teardown.
/// </summary>
public sealed class LayeredCaptureRunner
{
    internal static readonly TimeSpan DefaultMinimumRefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>ERROR_INVALID_PARAMETER: every cached handle went stale because the driver rebuilt its bound-adapter list.</summary>
    internal const int AdapterListRebuiltNativeError = 87;

    private readonly IAdapterEnumerationProvider _enumerationProvider;
    private readonly ICaptureGenerationFactory _generationFactory;
    private readonly IAdapterListChangeSource _changeSource;
    private readonly PolicySnapshot _policy;
    private readonly IRuntimeLogger _logger;
    private readonly Func<CancellationToken, ValueTask> _disposeDurableAsync;
    private readonly Action<IReadOnlyList<AdapterEnumerationItem>>? _onScopeInstalled;
    private readonly TimeSpan _minimumRefreshInterval;
    private readonly TimeProvider _time;
    private readonly RefreshDemandGate _demandGate = new();
    private readonly ConcurrentQueue<string> _pendingDegradedAdapters = new();
    private int _started;

    private ICaptureGeneration? _generation;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _generationCancellation;
    private Task? _runTask;
    private IReadOnlyList<AdapterEnumerationItem> _currentScope = [];
    private DateTimeOffset _lastRebuildUtc;

    public LayeredCaptureRunner(
        IAdapterEnumerationProvider enumerationProvider,
        ICaptureGenerationFactory generationFactory,
        IAdapterListChangeSource changeSource,
        PolicySnapshot policy,
        IRuntimeLogger logger,
        Func<CancellationToken, ValueTask> disposeDurableAsync,
        Action<IReadOnlyList<AdapterEnumerationItem>>? onScopeInstalled = null,
        TimeSpan? minimumRefreshInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(enumerationProvider);
        ArgumentNullException.ThrowIfNull(generationFactory);
        ArgumentNullException.ThrowIfNull(changeSource);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(disposeDurableAsync);
        _enumerationProvider = enumerationProvider;
        _generationFactory = generationFactory;
        _changeSource = changeSource;
        _policy = policy;
        _logger = logger;
        _disposeDurableAsync = disposeDurableAsync;
        _onScopeInstalled = onScopeInstalled;
        _minimumRefreshInterval = minimumRefreshInterval ?? DefaultMinimumRefreshInterval;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Feeds a degraded-pump observation into the refresh pipeline (R3): a degradation with
    /// ERROR_INVALID_PARAMETER means the adapter's handle went stale, which is the in-process
    /// symptom of a bound-adapter-list rebuild. The pending re-check is storm-guarded like any
    /// other refresh demand, so a genuine defect cannot cause a rebuild loop. Other native errors
    /// are ignored — their degraded-adapter logging and mode restore happen in the generation.
    /// </summary>
    public void SignalDegraded(WindowsAdapter adapter, int nativeError)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (nativeError != AdapterListRebuiltNativeError) return;
        _pendingDegradedAdapters.Enqueue(adapter.StableId);
        _demandGate.Signal();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The capture runner has already been started.");
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var cancelRegistration = cancellationToken.Register(
            static state => ((RefreshDemandGate)state!).Signal(), _demandGate);
        var monitor = Task.Factory.StartNew(
            () => MonitorAsync(monitorCancellation.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            var initial = _enumerationProvider.Enumerate();
            if (!CaptureAdapterScopeResolver.TryResolve(AdaptersOf(initial), _policy, out var startupScope, out var errors))
            {
                foreach (var error in errors) _logger.Error(error);
                throw new InvalidOperationException("Capture scope resolution failed; the run cannot start.");
            }
            if (startupScope.Count == 0)
            {
                _logger.Error("No MSTCP-bound adapters are available to capture.");
                throw new InvalidOperationException("No MSTCP-bound adapters are available to capture.");
            }

            await InstallGenerationAsync(ScopeItemsOf(initial, startupScope), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var demandTask = _demandGate.WaitAsync();
                if (_runTask is { } runTask) await Task.WhenAny(runTask, demandTask).ConfigureAwait(false);
                else await demandTask.ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) break;
                if (demandTask.IsCompleted)
                {
                    if (await ProcessRefreshDemandAsync(cancellationToken).ConfigureAwait(false) == RefreshDemandOutcome.Exit) break;
                    continue;
                }

                // The generation ended on its own with no refresh pending: a capture fault
                // propagates below; a natural end (every pump degraded or exited) ends the run
                // cleanly, unless a demand raced the completion — then it wins and is processed.
                await ObserveGenerationExitAsync().ConfigureAwait(false);
                if (_demandGate.WaitAsync().IsCompleted) continue;
                break;
            }
        }
        finally
        {
            await TeardownAsync(monitor, monitorCancellation).ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                // Blocking wait: this loop owns its dedicated (LongRunning) thread by design.
                if (!_changeSource.WaitOne(cancellationToken)) return;
                _demandGate.Signal();
            }
        }
        catch (OperationCanceledException)
        {
            // Monitor shutdown via its cancellation token.
        }
        catch (ObjectDisposedException)
        {
            // The change source was disposed concurrently with shutdown; no signal can follow.
        }
    }

    private async Task<RefreshDemandOutcome> ProcessRefreshDemandAsync(CancellationToken cancellationToken)
    {
        _demandGate.Consume();
        var degraded = DrainDegradedAdapters();
        var guardRemaining = _lastRebuildUtc + _minimumRefreshInterval - _time.GetUtcNow();
        if (guardRemaining > TimeSpan.Zero)
        {
            try { await Task.Delay(guardRemaining, _time, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return RefreshDemandOutcome.Exit; }
        }
        if (cancellationToken.IsCancellationRequested) return RefreshDemandOutcome.Exit;

        var fresh = _enumerationProvider.Enumerate();
        var nextScope = CaptureAdapterScopeResolver.ResolveForRefresh(AdaptersOf(fresh), _policy, out var warnings);
        var nextItems = ScopeItemsOf(fresh, nextScope);
        var diff = AdapterEnumerationDiff.Diff(_currentScope, nextItems);
        if (diff.IsEmpty)
        {
            LogRefresh(diff, degraded, fresh);
            return RefreshDemandOutcome.Continue;
        }

        foreach (var warning in warnings) _logger.Warn(warning);
        _lastRebuildUtc = _time.GetUtcNow();
        await StopGenerationAsync().ConfigureAwait(false);
        LogRefresh(diff, degraded, fresh);
        if (cancellationToken.IsCancellationRequested) return RefreshDemandOutcome.Exit;
        if (nextItems.Count == 0)
        {
            _currentScope = nextItems;
            _onScopeInstalled?.Invoke(nextItems);
            _logger.Warn("Every capture-scope adapter disappeared; interception is paused until an adapter returns.");
        }
        else
        {
            await InstallGenerationAsync(nextItems, cancellationToken).ConfigureAwait(false);
        }
        return RefreshDemandOutcome.Continue;
    }

    private async ValueTask InstallGenerationAsync(IReadOnlyList<AdapterEnumerationItem> scope, CancellationToken cancellationToken)
    {
        var generation = _generationFactory.Create(scope);
        var refreshCancellation = new CancellationTokenSource();
        var generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, refreshCancellation.Token);
        try
        {
            _runTask = generation.RunAsync(generationCancellation.Token);
        }
        catch
        {
            await generation.DisposeAsync().ConfigureAwait(false);
            generationCancellation.Dispose();
            refreshCancellation.Dispose();
            throw;
        }
        _generation = generation;
        _refreshCancellation = refreshCancellation;
        _generationCancellation = generationCancellation;
        _currentScope = scope;
        _lastRebuildUtc = _time.GetUtcNow();
        _onScopeInstalled?.Invoke(scope);
    }

    /// <summary>Stops the running generation and rethrows the fault it ended with, if any.</summary>
    private async Task StopGenerationAsync()
    {
        if (_generation is not { } generation) return;
        var runTask = _runTask;
        _generation = null;
        _runTask = null;
        Exception? fault = null;
        await CancelBestEffortAsync(_refreshCancellation).ConfigureAwait(false);
        if (runTask is { } task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // The refresh token fired; the generation stopped gracefully.
            }
            catch (Exception exception) { fault = exception; }
        }
        await generation.DisposeAsync().ConfigureAwait(false);
        _generationCancellation?.Dispose();
        _generationCancellation = null;
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        if (fault is not null) ExceptionDispatchInfo.Capture(fault).Throw();
    }

    private async Task ObserveGenerationExitAsync()
    {
        try { await _runTask!.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Only the runner's own links carry this token; user cancel is handled by the loop.
        }
        _logger.Info("The capture generation ended; stopping the run.");
    }

    /// <summary>Cancels a refresh token source that may already have been disposed concurrently.</summary>
    private static async Task CancelBestEffortAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException)
        {
            // Nothing left to cancel.
        }
    }

    private async Task TeardownAsync(Task monitor, CancellationTokenSource monitorCancellation)
    {
        Exception? stopFault = null;
        try { await StopGenerationAsync().ConfigureAwait(false); }
        catch (Exception exception) { stopFault = exception; }
        await CancelBestEffortAsync(monitorCancellation).ConfigureAwait(false);
        await monitor.ConfigureAwait(false);
        try
        {
            await _disposeDurableAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (stopFault is not null)
            {
                _logger.Error($"Durable-layer disposal failed during shutdown: {exception.Message}");
            }
            else
            {
                throw;
            }
        }
        if (stopFault is not null) ExceptionDispatchInfo.Capture(stopFault).Throw();
    }

    private List<string> DrainDegradedAdapters()
    {
        var degraded = new List<string>();
        while (_pendingDegradedAdapters.TryDequeue(out var stableId)) degraded.Add(stableId);
        return degraded;
    }

    private void LogRefresh(AdapterScopeDiff diff, IReadOnlyList<string> degraded, IReadOnlyList<AdapterEnumerationItem> fresh)
    {
        var fields = new List<RuntimeLogField>(4);
        if (diff.IsEmpty) fields.Add(new("noop", "true"));
        AddScopeList(fields, "added", diff.Added);
        AddScopeList(fields, "removed", diff.Removed);
        AddScopeList(fields, "changed", diff.Changed);
        var degradedText = DescribeDegradedPresence(degraded, fresh);
        if (degradedText is not null) fields.Add(new("degraded", degradedText));
        _logger.Event(RuntimeLogLevel.Info, "adapter.refresh", fields.ToArray());
    }

    /// <summary>One <c>stableId=present</c> token per degraded adapter, resolved against the fresh enumeration.</summary>
    private static string? DescribeDegradedPresence(IReadOnlyList<string> degraded, IReadOnlyList<AdapterEnumerationItem> fresh)
    {
        if (degraded.Count == 0) return null;
        return string.Join(",", degraded.Select(stableId =>
            $"{stableId}={(fresh.Any(item => string.Equals(item.StableId, stableId, StringComparison.OrdinalIgnoreCase)) ? "true" : "false")}"));
    }

    private static void AddScopeList(List<RuntimeLogField> fields, string key, IReadOnlyList<AdapterEnumerationItem> items)
    {
        if (items.Count == 0) return;
        fields.Add(new RuntimeLogField(key, string.Join("; ", items.Select(item => $"{item.Adapter.FriendlyName}({item.StableId})"))));
    }

    private static IReadOnlyList<WindowsAdapter> AdaptersOf(IReadOnlyList<AdapterEnumerationItem> items) =>
        items.Select(item => item.Adapter).ToArray();

    private static IReadOnlyList<AdapterEnumerationItem> ScopeItemsOf(IReadOnlyList<AdapterEnumerationItem> enumeration, IReadOnlyList<WindowsAdapter> scope)
    {
        var byHandle = new Dictionary<nint, AdapterEnumerationItem>();
        foreach (var item in enumeration) byHandle[item.Adapter.RuntimeHandle] = item;
        var items = new List<AdapterEnumerationItem>(scope.Count);
        foreach (var adapter in scope)
        {
            if (!byHandle.TryGetValue(adapter.RuntimeHandle, out var item)) throw new InvalidOperationException($"Scope adapter '{adapter.StableId}' is absent from its own enumeration.");
            items.Add(item);
        }
        return items;
    }

    private enum RefreshDemandOutcome
    {
        Continue,
        Exit
    }

    /// <summary>
    /// Coalescing async demand gate: <see cref="Signal"/> marks one pending demand (repeated
    /// signals while a demand is pending change nothing), <see cref="WaitAsync"/> hands the
    /// consumer the task that completes when a demand is pending, and <see cref="Consume"/> —
    /// called when processing starts — arms the next wait while leaving signals that arrived
    /// meanwhile pending, so bursts collapse into one follow-up demand.
    /// </summary>
    private sealed class RefreshDemandGate
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource _waiter = NewWaiter();
        private bool _signalled;

        public void Signal()
        {
            lock (_gate)
            {
                if (_signalled) return;
                _signalled = true;
                _waiter.TrySetResult();
            }
        }

        public Task WaitAsync()
        {
            lock (_gate) return _waiter.Task;
        }

        public void Consume()
        {
            lock (_gate)
            {
                _signalled = false;
                _waiter = NewWaiter();
            }
        }

        private static TaskCompletionSource NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
