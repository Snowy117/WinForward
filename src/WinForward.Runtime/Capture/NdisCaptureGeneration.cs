using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime.Logging;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// Point-in-time adapter-pump counts for one generation (the heartbeat's pump summary,
/// task 09-17 R2.3): how many pumps are still running and how many exited through the
/// degraded path. A generation that reports the default value (0/0) simply has no pump
/// state to share.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct CapturePumpState(int Running, int Degraded);

/// <summary>
/// One capture generation (task 09-07-adapter-list-refresh, design §3.5): a full
/// <see cref="TransactionalCaptureRuntime"/> lifetime — mode snapshot, tunnel apply, the pump
/// run, and the cleanup/mode-restore tail — over the durable shared packet processor.
/// <see cref="RunAsync"/> completes when the generation ends: its (runner-linked) cancellation
/// token fired, the capture loop faulted (the fault propagates), or every pump exited on its own.
/// Disposing after completion is idempotent; disposing a still-running generation cancels it and
/// awaits the cleanup tail.
/// </summary>
public interface ICaptureGeneration : IAsyncDisposable
{
    /// <summary>Runs the generation until it ends; faults propagate as the fail-closed exit.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether this generation's start sequence reached its pump run (task 09-11): still false
    /// while the mode snapshot/apply phase is in flight. A fault observed on a completed
    /// generation whose latch is still false never reached the pumps, which distinguishes a
    /// recoverable startup stale-handle fault from an in-run fault.
    /// </summary>
    bool ReachedPumpRun { get; }

    /// <summary>
    /// This generation's live pump counts (task 09-17 R2.3, heartbeat source). Reading is
    /// observational: a benign stale value during a generation swap is acceptable, and a
    /// generation without pump telemetry reports the default.
    /// </summary>
    CapturePumpState Pumps { get; }
}

/// <summary>
/// Builds one <see cref="ICaptureGeneration"/> per adapter enumeration from the durable-layer
/// parts that generations share: the driver handle and the capture packet processor. Everything
/// generation-scoped (mode controller, pump set, runtime) is created here and released with the
/// generation.
/// </summary>
public interface ICaptureGenerationFactory
{
    /// <summary>Creates (but does not start) the generation for the resolved capture scope.</summary>
    ICaptureGeneration Create(IReadOnlyList<AdapterEnumerationItem> scope);
}

/// <summary>
/// The Windows composition of one capture generation, mirroring the wiring
/// <c>Program.cs</c> performs today: a <see cref="NdisAdapterModeController"/> and a
/// <see cref="MultiAdapterCaptureLoop"/> over the durable driver and processor, wrapped in one
/// <see cref="TransactionalCaptureRuntime"/>. The degraded-pump callback logs
/// <c>adapter.degraded</c> and restores that adapter's mode through the owning runtime, then
/// forwards to the optional <c>onAdapterDegraded</c> notification so the runner can feed
/// error 87 into its refresh channel (R3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NdisCaptureGenerationFactory : ICaptureGenerationFactory
{
    private readonly NdisApiDriver _driver;
    private readonly CapturePacketProcessor _processor;
    private readonly AdapterSlotTable _slots;
    private readonly ILogger _logger;
    private readonly Func<WindowsAdapter, int, ValueTask>? _onAdapterDegraded;
    private readonly Action<WindowsAdapter, int, int>? _onAdapterTransientRetry;
    private readonly TimeSpan? _pollDelay;
    private readonly RuntimeLogThrottle _slotExhaustedWarn = new(TimeSpan.FromMinutes(1));
    private readonly RuntimeLogThrottle _packetEventUnavailableWarn = new(TimeSpan.FromMinutes(1));
    // One throttle per adapter handle, so every adapter's single arming can be logged: a shared
    // window would suppress each sibling's line, which is the only evidence the Windows experiment
    // reads.
    private readonly ConcurrentDictionary<nint, RuntimeLogThrottle> _readShapeMismatchWarns = new();

    public NdisCaptureGenerationFactory(
        NdisApiDriver driver,
        CapturePacketProcessor processor,
        AdapterSlotTable slots,
        ILogger logger,
        Func<WindowsAdapter, int, ValueTask>? onAdapterDegraded = null,
        Action<WindowsAdapter, int, int>? onAdapterTransientRetry = null,
        TimeSpan? pollDelay = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(logger);
        _driver = driver;
        _processor = processor;
        _slots = slots;
        _logger = logger;
        _onAdapterDegraded = onAdapterDegraded;
        _onAdapterTransientRetry = onAdapterTransientRetry;
        _pollDelay = pollDelay;
    }

    /// <summary>
    /// The pipeline-owned wake registry, installed by the composition that created the pipeline
    /// before the first generation is built. Null keeps every pump on the bare driver signal, which
    /// costs delivery latency only — the pump re-reads on every timeout regardless.
    /// </summary>
    internal FlowAttributionWakeRegistry? WakeRegistry { get; init; }

    public ICaptureGeneration Create(IReadOnlyList<AdapterEnumerationItem> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        // Interning happens once per adapter per generation. An adapter this process cannot name is
        // refused here, before it enters the scope, rather than keyed with NoSlot: two adapters whose
        // keys collapsed onto NoSlot would compare equal on a shared 5-tuple and serve each other's
        // state, which is the multi-VM case the adapter identity exists for.
        var bindings = new List<AdapterCaptureBinding>(scope.Count);
        for (var index = 0; index < scope.Count; index++)
        {
            var adapter = scope[index].Adapter;
            if (_slots.TryIntern(adapter.StableId, adapter.Generation, adapter.FriendlyName, out var slot))
            {
                bindings.Add(new AdapterCaptureBinding(adapter, slot));
                continue;
            }

            if (_slotExhaustedWarn.ShouldEmit())
            {
                CaptureLog.AdapterSlotExhausted(_logger, adapter.StableId, adapter.FriendlyName, _slots.CountForDiagnostics);
            }
        }

        var adapters = bindings.Select(binding => binding.Adapter).ToArray();
        // The read-shape guard's only evidence channel: the driver cannot log (ILogger lives
        // above it in the dependency order), so the composition resolves the handle back to the
        // adapter identity of the generation that registered it.
        _driver.ReadShapeMismatchSink = mismatch => ReportReadShapeMismatch(bindings, mismatch);
        var arrivalSignals = RegisterArrivalSignals(bindings);
        TransactionalCaptureRuntime? runtime = null;
        MultiAdapterCaptureLoop loop;
        try
        {
            // ReSharper disable once AccessToModifiedClosure // runtime is assigned on the statement after this construction and the callback fires only from a running pump, which cannot exist before the runtime created below starts this loop.
            loop = new MultiAdapterCaptureLoop(_driver, bindings, _processor, _pollDelay,
                onAdapterDegraded: (adapter, nativeError) => HandleDegradedAsync(runtime!, adapter, nativeError),
                onAdapterTransientRetry: _onAdapterTransientRetry,
                arrivalSignals: arrivalSignals);
        }
        catch
        {
            foreach (var signal in arrivalSignals) signal?.Dispose();
            throw;
        }

        runtime = new TransactionalCaptureRuntime(new NdisAdapterModeController(_driver, adapters), loop);
        return new RuntimeCaptureGeneration(runtime, () => loop.PumpState);
    }

    /// <summary>
    /// One packet-arrival registration per in-scope adapter, positionally paired with
    /// <paramref name="bindings"/>. A refused registration is a warn and a null entry, never a
    /// failure: degraded performance must not abort a capture run, so that adapter keeps the pump's
    /// sleep-poll shape and every sibling keeps its signal.
    /// </summary>
    private List<INdisPacketArrivalSignal?> RegisterArrivalSignals(List<AdapterCaptureBinding> bindings)
    {
        var signals = new List<INdisPacketArrivalSignal?>(bindings.Count);
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            var registered = _driver.TryRegisterPacketEvent(binding.Adapter.RuntimeHandle, out var signal, out var nativeError);
            // A registered driver signal is composed with the pipeline's own wake event, so a
            // decided attribution wakes this adapter's pump instead of waiting out the idle bound.
            // A refused registration has no signal to compose and simply gets no wake.
            signals.Add(registered && signal is NdisPacketArrivalSignal driverSignal && WakeRegistry is not null
                ? WakeRegistry.Register(binding.Adapter.RuntimeHandle, driverSignal)
                : signal);
            if (registered) continue;
            if (_packetEventUnavailableWarn.ShouldEmit())
            {
                CaptureLog.CapturePacketEventUnavailable(_logger, binding.Adapter.StableId, binding.Adapter.FriendlyName, nativeError);
            }
        }

        return signals;
    }

    /// <summary>
    /// One rate-limited warn per arming of the read-shape guard, carrying everything the Windows
    /// experiment needs: which adapter healed, what it asked for, what the queue held and the native
    /// error of the failed read. No line means the driver conforms to the full-capacity request.
    /// </summary>
    private void ReportReadShapeMismatch(IReadOnlyList<AdapterCaptureBinding> bindings, NdisReadShapeMismatch mismatch)
    {
        if (!_readShapeMismatchWarns.GetOrAdd(mismatch.AdapterHandle, static _ => new RuntimeLogThrottle(TimeSpan.FromMinutes(1))).ShouldEmit()) return;
        var adapter = FindAdapterByHandle(bindings, mismatch.AdapterHandle);
        CaptureLog.AdapterReadShapeMismatch(
            _logger,
            adapter?.StableId ?? "unknown",
            adapter?.FriendlyName ?? "unknown",
            mismatch.RequestedCount,
            mismatch.QueuedPacketCount,
            mismatch.NativeError);
    }

    private static WindowsAdapter? FindAdapterByHandle(IReadOnlyList<AdapterCaptureBinding> bindings, nint adapterHandle)
    {
        for (var index = 0; index < bindings.Count; index++)
        {
            if (bindings[index].Adapter.RuntimeHandle == adapterHandle) return bindings[index].Adapter;
        }

        return null;
    }

    private async ValueTask HandleDegradedAsync(TransactionalCaptureRuntime runtime, WindowsAdapter adapter, int nativeError)
    {
        CaptureLog.AdapterDegraded(_logger, adapter.StableId, adapter.FriendlyName, nativeError);
        await runtime.MarkAdapterDegradedAsync(adapter.StableId).ConfigureAwait(false);
        if (_onAdapterDegraded is not null)
        {
            await _onAdapterDegraded(adapter, nativeError).ConfigureAwait(false);
        }
    }
}

/// <summary>Adapts one <see cref="TransactionalCaptureRuntime"/> to the generation seam.</summary>
internal sealed class RuntimeCaptureGeneration : ICaptureGeneration
{
    private readonly TransactionalCaptureRuntime _runtime;
    private readonly Func<CapturePumpState>? _pumps;

    public RuntimeCaptureGeneration(TransactionalCaptureRuntime runtime, Func<CapturePumpState>? pumps = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _pumps = pumps;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool ReachedPumpRun => _runtime.ReachedPumpRun;

    public CapturePumpState Pumps => _pumps?.Invoke() ?? default;

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
