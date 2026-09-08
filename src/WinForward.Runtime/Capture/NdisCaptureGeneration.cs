using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

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
    private readonly IRuntimeLogger _logger;
    private readonly Func<WindowsAdapter, int, ValueTask>? _onAdapterDegraded;
    private readonly Action<WindowsAdapter, int, int>? _onAdapterTransientRetry;
    private readonly TimeSpan? _pollDelay;

    public NdisCaptureGenerationFactory(
        NdisApiDriver driver,
        CapturePacketProcessor processor,
        IRuntimeLogger logger,
        Func<WindowsAdapter, int, ValueTask>? onAdapterDegraded = null,
        Action<WindowsAdapter, int, int>? onAdapterTransientRetry = null,
        TimeSpan? pollDelay = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(logger);
        _driver = driver;
        _processor = processor;
        _logger = logger;
        _onAdapterDegraded = onAdapterDegraded;
        _onAdapterTransientRetry = onAdapterTransientRetry;
        _pollDelay = pollDelay;
    }

    public ICaptureGeneration Create(IReadOnlyList<AdapterEnumerationItem> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var adapters = scope.Select(item => item.Adapter).ToArray();
        TransactionalCaptureRuntime? runtime = null;
        var loop = new MultiAdapterCaptureLoop(_driver, adapters, _processor, _pollDelay,
            onAdapterDegraded: (adapter, nativeError) => HandleDegradedAsync(runtime!, adapter, nativeError),
            onAdapterTransientRetry: _onAdapterTransientRetry);
        runtime = new TransactionalCaptureRuntime(new NdisAdapterModeController(_driver, adapters), loop);
        return new RuntimeCaptureGeneration(runtime);
    }

    private async ValueTask HandleDegradedAsync(TransactionalCaptureRuntime runtime, WindowsAdapter adapter, int nativeError)
    {
        _logger.Event(RuntimeLogLevel.Error, "adapter.degraded",
            new RuntimeLogField("adapter", adapter.StableId),
            new RuntimeLogField("name", adapter.FriendlyName),
            new RuntimeLogField("nativeError", nativeError));
        await runtime.MarkAdapterDegradedAsync(adapter.StableId).ConfigureAwait(false);
        if (_onAdapterDegraded is { } notify)
        {
            await notify(adapter, nativeError).ConfigureAwait(false);
        }
    }
}

/// <summary>Adapts one <see cref="TransactionalCaptureRuntime"/> to the generation seam.</summary>
internal sealed class RuntimeCaptureGeneration : ICaptureGeneration
{
    private readonly TransactionalCaptureRuntime _runtime;

    public RuntimeCaptureGeneration(TransactionalCaptureRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
