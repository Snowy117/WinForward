using System.Globalization;
using WinForward.Core;
using WinForward.Runtime.Capture;
using WinForward.Windows;

namespace WinForward.TestSupport;

/// <summary>
/// Manual-switch <see cref="IAdapterEnumerationProvider"/> for capture-runner tests (task
/// 09-07-adapter-list-refresh): the test rewrites the visible enumeration between refresh signals.
/// </summary>
internal sealed class FakeAdapterEnumerationProvider(IReadOnlyList<AdapterEnumerationItem> initial) : IAdapterEnumerationProvider
{
    private readonly Lock _gate = new();
    private IReadOnlyList<AdapterEnumerationItem> _current = initial;
    private int _enumerationCount;

    public int EnumerationCount => Volatile.Read(ref _enumerationCount);

    /// <summary>
    /// Opt-in hold for the next <see cref="Enumerate"/> call. The runner re-reads the enumeration inside
    /// its refresh demand path, after the storm guard and immediately before it stops the outgoing
    /// generation, so parking here lets a fact stage state that the stop is then guaranteed to observe
    /// instead of hoping the fact wins a scheduling race against the stop's cancellation.
    /// </summary>
    public TaskCompletionSource? NextEnumerationGate { get; set; }

    public void SetAdapters(params AdapterEnumerationItem[] items)
    {
        lock (_gate) _current = items;
    }

    public IReadOnlyList<AdapterEnumerationItem> Enumerate()
    {
        TaskCompletionSource? hold;
        IReadOnlyList<AdapterEnumerationItem> current;
        lock (_gate)
        {
            Interlocked.Increment(ref _enumerationCount);
            current = _current;
            hold = NextEnumerationGate;
            NextEnumerationGate = null;
        }

        hold?.Task.GetAwaiter().GetResult();
        return current;
    }
}

/// <summary>
/// Scriptable <see cref="ICaptureGeneration"/>: runs until cancelled (returns normally, like the
/// real runtime), the test completes it, or the test faults it (the exception propagates).
/// Latches <see cref="ReachedPumpRun"/> once its run starts, mirroring the latch position of
/// the real runtime's start sequence; a <see cref="FaultAtStartupWith"/> exception escapes
/// before that latch — the pre-pump startup signature (task 09-11).
/// </summary>
internal sealed class FakeCaptureGeneration(int index, IReadOnlyList<AdapterEnumerationItem> scope) : ICaptureGeneration
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Index { get; } = index;
    public IReadOnlyList<AdapterEnumerationItem> Scope { get; } = scope;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DisposeCount { get; private set; }
    public bool ReachedPumpRun { get; private set; }

    /// <summary>Scriptable pump telemetry for the runner's heartbeat snapshot accessor; default reports none.</summary>
    public CapturePumpState Pumps { get; set; }

    public Action? OnDisposed { get; set; }

    /// <summary>The startup fault thrown before the pumps-started latch; null keeps the generation healthy.</summary>
    public Exception? FaultAtStartupWith { get; set; }

    /// <summary>
    /// When set, a pending <see cref="FaultAtStartupWith"/> is held until this source completes, so a
    /// test can stage a racing refresh demand before the fault lands. Once the source has completed the
    /// fault is authoritative: a stop that cancels at the same instant must not convert it into a
    /// silent cancellation, because <see cref="Task.WaitAsync(CancellationToken)"/> races the source's
    /// completion against the token and the token's callback can win even when the source completed
    /// first. Cancellation still breaks the wait while the source is pending, so a failing fact cannot
    /// hang its harness's teardown.
    /// </summary>
    public TaskCompletionSource? StartupFaultRelease { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (FaultAtStartupWith is { } startupFault)
        {
            if (StartupFaultRelease is { } release)
            {
                try
                {
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (release.Task.IsCompleted)
                {
                    // The release landed, so the scripted fault is the outcome: a stop that cancels in
                    // the same instant must not convert it into a silent cancellation. Control falls
                    // through to the throw below.
                }
            }

            throw startupFault;
        }
        ReachedPumpRun = true;
        Started.TrySetResult();
        try
        {
            await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run completes normally, like the real runtime's shutdown path.
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

    /// <summary>Per-creation script (task 09-11): assigns the startup-fault knobs on each freshly created generation before it can run.</summary>
    public Action<FakeCaptureGeneration>? StartupFaultScript { get; set; }

    public IReadOnlyList<FakeCaptureGeneration> Generations
    {
        get
        {
            lock (_gate) return [.. _generations];
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
        StartupFaultScript?.Invoke(generation);
        OnCreated?.Invoke(generation);
        return generation;
    }
}

internal static class CaptureRunnerFakes
{
    public static AdapterEnumerationItem AdapterItem(string stableId, nint handle, ushort mtu = 1500, byte firstMacOctet = 1, string addressFingerprint = "") =>
        new(new WindowsAdapter(stableId, stableId, stableId, handle, 0), [firstMacOctet, 2, 3, 4, 5, 6], mtu, addressFingerprint);

    /// <summary>A policy whose process rule constrains nothing, so capture scope widens to every adapter.</summary>
    public static PolicySnapshot UnconstrainedPolicy() => new(
        [new PolicyRule(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser.exe" }), new FlowDecision(FlowAction.Block, 0, TargetName: null))],
        FlowAction.Pass);

    /// <summary>A policy with one adapter-constrained rule per selector, so scope follows the selectors.</summary>
    public static PolicySnapshot AdapterConstrainedPolicy(params string[] stableIds) => new(
        [.. stableIds.Select((id, index) => new PolicyRule(
            new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }),
            new FlowDecision(FlowAction.Pass, index, TargetName: null)))],
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
    private int _installedScopes;

    public CaptureRunnerHarness(IReadOnlyList<AdapterEnumerationItem> initialAdapters, PolicySnapshot policy, TimeSpan? minimumRefreshInterval = null, Action<IReadOnlyList<AdapterEnumerationItem>>? onScopeInstalled = null, TimeSpan? periodicRefreshInterval = null)
    {
        Enumeration = new FakeAdapterEnumerationProvider(initialAdapters);
        Generations.OnCreated = generation =>
        {
            generation.OnDisposed = () => AddEvent(string.Create(CultureInfo.InvariantCulture, $"generation-{generation.Index}-disposed"));
            AddEvent(string.Create(CultureInfo.InvariantCulture, $"generation-{generation.Index}-created"));
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
                // bundle's scope-installed callback composes its durable-layer updates. The install
                // counter is published last so a waiter that observes it also observes every side
                // effect of the install, including the composed callback's own.
                onScopeInstalled?.Invoke(scope);
                Interlocked.Increment(ref _installedScopes);
            },
            minimumRefreshInterval: minimumRefreshInterval,
            periodicRefreshInterval: periodicRefreshInterval);
    }

    public FakeAdapterEnumerationProvider Enumeration { get; }
    public FakeCaptureGenerationFactory Generations { get; } = new();
    public FakeAdapterListChangeSource ChangeSource { get; } = new();
    public RecordingLogger Logger { get; } = new();
    public List<IReadOnlyList<AdapterEnumerationItem>> InstalledScopes { get; } = [];
    public CancellationTokenSource Cancel { get; } = new();
    public LayeredCaptureRunner Runner { get; }
    public Task RunTask { get; private set; } = Task.CompletedTask;

    public int DurableDisposeCount => Volatile.Read(ref _durableDisposeCount);

    public string[] Events
    {
        get
        {
            lock (_eventGate) return [.. _events];
        }
    }

    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> RefreshEvents =>
        [.. Logger.Events.Where(entry => string.Equals(entry.Name, "adapter.refresh", StringComparison.Ordinal)).Select(entry => entry.Fields)];

    public static string? FieldValue(IReadOnlyList<KeyValuePair<string, object?>> fields, string key) =>
        fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal)).Value?.ToString();

    public void Start() => RunTask = Runner.RunAsync(Cancel.Token);

    /// <summary>
    /// Waits until generation <paramref name="index"/> has started <em>and</em> its capture scope has
    /// been installed. Started is latched at the top of the generation's run, before
    /// <c>InstallGenerationAsync</c> publishes the scope through the composed onScopeInstalled
    /// callback, so a caller that asserts on InstalledScopes or on the recorded event timeline needs
    /// the install as its synchronization point — not the start.
    /// </summary>
    public async Task WaitForGenerationStartedAsync(int index)
    {
        await AsyncTestExtensions.WaitForAsync(() => Generations.Generations.Count > index && Generations.Generations[index].Started.Task.IsCompleted).ConfigureAwait(false);
        await AsyncTestExtensions.WaitForAsync(() => Volatile.Read(ref _installedScopes) > index).ConfigureAwait(false);
    }

    public FakeCaptureGeneration Generation(int index) => Generations.Generations[index];

    /// <summary>Records into the ordered event timeline, so tests can interleave their own markers with generation lifecycle events.</summary>
    internal void AddEvent(string name)
    {
        lock (_eventGate) _events.Add(name);
    }

    public async ValueTask DisposeAsync()
    {
        await Cancel.CancelAsync();
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
