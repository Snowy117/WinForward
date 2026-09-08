using WinForward.Runtime.Capture;
using WinForward.Windows;

namespace WinForward.Core.Tests;

/// <summary>
/// Manual-switch <see cref="IAdapterEnumerationProvider"/> for capture-runner tests (task
/// 09-07-adapter-list-refresh): the test rewrites the visible enumeration between refresh signals.
/// </summary>
internal sealed class FakeAdapterEnumerationProvider(IReadOnlyList<AdapterEnumerationItem> initial) : IAdapterEnumerationProvider
{
    private readonly Lock _gate = new();
    private IReadOnlyList<AdapterEnumerationItem> _current = initial;

    public int EnumerationCount { get; private set; }

    public void SetAdapters(params AdapterEnumerationItem[] items)
    {
        lock (_gate) _current = items;
    }

    public IReadOnlyList<AdapterEnumerationItem> Enumerate()
    {
        lock (_gate)
        {
            EnumerationCount++;
            return _current;
        }
    }
}

/// <summary>
/// Scriptable <see cref="ICaptureGeneration"/>: runs until cancelled (returns normally, like the
/// real runtime), the test completes it, or the test faults it (the exception propagates).
/// </summary>
internal sealed class FakeCaptureGeneration : ICaptureGeneration
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeCaptureGeneration(int index, IReadOnlyList<AdapterEnumerationItem> scope)
    {
        Index = index;
        Scope = scope;
    }

    public int Index { get; }
    public IReadOnlyList<AdapterEnumerationItem> Scope { get; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DisposeCount { get; private set; }
    public bool CancelObserved { get; private set; }
    public Action? OnDisposed { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => CancelObserved = true);
        Started.TrySetResult();
        try
        {
            await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CancelObserved = true;
        }
    }

    public void Complete() => _completion.TrySetResult();

    public void Fault(Exception exception) => _completion.TrySetException(exception);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        OnDisposed?.Invoke();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeCaptureGenerationFactory : ICaptureGenerationFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeCaptureGeneration> _generations = [];

    public Action<FakeCaptureGeneration>? OnCreated { get; set; }

    public IReadOnlyList<FakeCaptureGeneration> Generations
    {
        get
        {
            lock (_gate) return _generations.ToArray();
        }
    }

    public ICaptureGeneration Create(IReadOnlyList<AdapterEnumerationItem> scope)
    {
        FakeCaptureGeneration generation;
        lock (_gate)
        {
            generation = new FakeCaptureGeneration(_generations.Count, scope);
            _generations.Add(generation);
        }
        OnCreated?.Invoke(generation);
        return generation;
    }
}

internal static class CaptureRunnerFakes
{
    public static AdapterEnumerationItem AdapterItem(string stableId, nint handle, ushort mtu = 1500, byte firstMacOctet = 1) =>
        new(new WindowsAdapter(stableId, stableId, stableId, handle, 0), [firstMacOctet, 2, 3, 4, 5, 6], mtu);

    /// <summary>A policy whose process rule constrains nothing, so capture scope widens to every adapter.</summary>
    public static WinForward.Core.PolicySnapshot UnconstrainedPolicy() => new(
        [new WinForward.Core.PolicyRule(new WinForward.Core.RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser.exe" }), new WinForward.Core.FlowDecision(FlowAction.Block, 0, null))],
        FlowAction.Pass);

    /// <summary>A policy with one adapter-constrained rule per selector, so scope follows the selectors.</summary>
    public static WinForward.Core.PolicySnapshot AdapterConstrainedPolicy(params string[] stableIds) => new(
        stableIds.Select((id, index) => new WinForward.Core.PolicyRule(
            new WinForward.Core.RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }),
            new WinForward.Core.FlowDecision(FlowAction.Pass, index, null))).ToArray(),
        FlowAction.Pass);
}

/// <summary>
/// Wires one <see cref="LayeredCaptureRunner"/> over the fake enumeration/generation/change-source
/// triple with an ordered event seam (generation create/dispose, scope installs, durable
/// disposal), a durable-dispose counter, and <c>adapter.refresh</c> field accessors.
/// </summary>
internal sealed class CaptureRunnerHarness : IAsyncDisposable
{
    private readonly Lock _eventGate = new();
    private readonly List<string> _events = [];
    private int _durableDisposeCount;

    public CaptureRunnerHarness(IReadOnlyList<AdapterEnumerationItem> initialAdapters, WinForward.Core.PolicySnapshot policy, TimeSpan? minimumRefreshInterval = null, Action<IReadOnlyList<AdapterEnumerationItem>>? onScopeInstalled = null)
    {
        Enumeration = new FakeAdapterEnumerationProvider(initialAdapters);
        Generations.OnCreated = generation =>
        {
            generation.OnDisposed = () => AddEvent($"generation-{generation.Index}-disposed");
            AddEvent($"generation-{generation.Index}-created");
        };
        Runner = new LayeredCaptureRunner(
            Enumeration,
            Generations,
            ChangeSource,
            policy,
            Logger,
            _ =>
            {
                AddEvent("durable-dispose");
                Interlocked.Increment(ref _durableDisposeCount);
                return ValueTask.CompletedTask;
            },
            onScopeInstalled: scope =>
            {
                lock (InstalledScopes) InstalledScopes.Add(scope);
                AddEvent($"scope-installed({scope.Count})");
                // Composed after the harness's own bookkeeping, mirroring how the production
                // bundle's scope-installed callback composes its durable-layer updates.
                onScopeInstalled?.Invoke(scope);
            },
            minimumRefreshInterval: minimumRefreshInterval);
    }

    public FakeAdapterEnumerationProvider Enumeration { get; }
    public FakeCaptureGenerationFactory Generations { get; } = new();
    public FakeAdapterListChangeSource ChangeSource { get; } = new();
    public RecordingRuntimeLogger Logger { get; } = new();
    public List<IReadOnlyList<AdapterEnumerationItem>> InstalledScopes { get; } = [];
    public CancellationTokenSource Cancel { get; } = new();
    public LayeredCaptureRunner Runner { get; }
    public Task RunTask { get; private set; } = Task.CompletedTask;

    public int DurableDisposeCount => Volatile.Read(ref _durableDisposeCount);

    public string[] Events
    {
        get
        {
            lock (_eventGate) return _events.ToArray();
        }
    }

    public IReadOnlyList<IReadOnlyList<WinForward.Runtime.RuntimeLogField>> RefreshEvents =>
        Logger.Events.Where(entry => string.Equals(entry.Name, "adapter.refresh", StringComparison.Ordinal)).Select(entry => (IReadOnlyList<WinForward.Runtime.RuntimeLogField>)entry.Fields).ToList();

    public static string? FieldValue(IReadOnlyList<WinForward.Runtime.RuntimeLogField> fields, string key) =>
        fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal)).Value?.ToString();

    public void Start() => RunTask = Runner.RunAsync(Cancel.Token);

    public async Task WaitForGenerationStartedAsync(int index) =>
        await AsyncTestExtensions.WaitForAsync(() => Generations.Generations.Count > index && Generations.Generations[index].Started.Task.IsCompleted).ConfigureAwait(false);

    public FakeCaptureGeneration Generation(int index) => Generations.Generations[index];

    /// <summary>Records into the ordered event timeline, so tests can interleave their own markers with generation lifecycle events.</summary>
    internal void AddEvent(string name)
    {
        lock (_eventGate) _events.Add(name);
    }

    public async ValueTask DisposeAsync()
    {
        Cancel.Cancel();
        try
        {
            await RunTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The faulting-run assertions already observed this exception.
            GC.KeepAlive(exception);
        }
        ChangeSource.Dispose();
        Cancel.Dispose();
    }
}
