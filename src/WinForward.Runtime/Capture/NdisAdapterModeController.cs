using System.Runtime.Versioning;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// An <see cref="IAdapterModeController"/> that drives the NDISAPI adapter modes for the adapters in
/// capture scope. Applying capture mode preserves any pre-existing flag bits and only ORs in the
/// tunnel flags; restoring writes back the exact previously snapshotted flags. The driver is owned
/// by the caller (the capture pumps keep it open for the whole run), so <see cref="DisposeAsync"/>
/// is a no-op here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NdisAdapterModeController : IAdapterModeController
{
    private readonly NdisApiDriver _driver;
    private readonly IReadOnlyDictionary<string, WindowsAdapter> _byStableId;

    public NdisAdapterModeController(NdisApiDriver driver, IReadOnlyList<WindowsAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(adapters);
        _driver = driver;
        _byStableId = adapters.ToDictionary(adapter => adapter.StableId, StringComparer.OrdinalIgnoreCase);
    }

    public ValueTask<IReadOnlyList<AdapterModeSnapshot>> SnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<AdapterModeSnapshot>(_byStableId.Count);
        foreach (var adapter in _byStableId.Values)
        {
            snapshots.Add(new AdapterModeSnapshot(adapter.StableId, _driver.GetAdapterMode(adapter.RuntimeHandle)));
        }
        return ValueTask.FromResult<IReadOnlyList<AdapterModeSnapshot>>(snapshots);
    }

    public ValueTask ApplyCaptureModeAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken)
    {
        var windowsAdapter = _byStableId[adapter.AdapterId];
        _driver.SetAdapterMode(windowsAdapter.RuntimeHandle, adapter.Flags | NdisApiAbi.SentTunnel | NdisApiAbi.ReceiveTunnel);
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreAsync(AdapterModeSnapshot adapter, CancellationToken cancellationToken)
    {
        var windowsAdapter = _byStableId[adapter.AdapterId];
        _driver.SetAdapterMode(windowsAdapter.RuntimeHandle, adapter.Flags);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}