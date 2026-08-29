using System.Runtime.Versioning;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// An <see cref="IPacketCaptureLoop"/> that runs one <see cref="NdisCapturePump"/> per in-scope
/// adapter concurrently. Graceful cancellation (the linked token) lets every pump exit normally. An
/// unexpected failure in any pump cancels the shared token so sibling pumps stop promptly, then the
/// exception propagates for the runtime to restore adapter modes (fail-closed). The driver stays
/// owned by the caller for the whole run, so <see cref="DisposeAsync"/> only stops the pumps.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiAdapterCaptureLoop : IPacketCaptureLoop
{
    private readonly NdisCapturePump[] _pumps;

    public MultiAdapterCaptureLoop(NdisApiDriver driver, IReadOnlyList<WindowsAdapter> adapters, CapturePacketProcessor processor, TimeSpan? pollDelay = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(processor);
        _pumps = adapters
            .Select(adapter => new NdisCapturePump(driver, adapter.RuntimeHandle, (packet, cancellationToken) => processor.ProcessAsync(packet, adapter, cancellationToken), pollDelay))
            .ToArray();
    }

    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = _pumps.Select(pump => RunPumpAsync(pump, linked)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pump in _pumps)
        {
            await pump.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunPumpAsync(NdisCapturePump pump, CancellationTokenSource linked)
    {
        try
        {
            await pump.RunAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Graceful shutdown path: the pump is stopping because the run is being cancelled.
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }
}