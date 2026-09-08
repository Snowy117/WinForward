using System.Runtime.Versioning;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// One correlated adapter plus the NDIS runtime link state that the enumeration handles carry
/// (design §3.5 of task 09-07-adapter-list-refresh): the kernel handle, the adapter MAC, and the
/// MTU. A refresh diffs this triple per stable ID to decide whether the bound-adapter list was
/// actually rebuilt — handles are fresh pointers after every rebuild and MAC/MTU changes matter to
/// the UDP reinjection targets, so any of the three changing demands a new generation.
/// </summary>
public sealed record AdapterEnumerationItem(WindowsAdapter Adapter, byte[] Mac, ushort Mtu)
{
    public string StableId => Adapter.StableId;
}

/// <summary>
/// The source of fresh adapter enumerations for the capture runner. Enumerating joins the
/// NDISAPI bound-adapter list (runtime handles, MACs, MTUs) with the Windows IP Helper identity
/// correlation; callers get one item per MSTCP-bound adapter. Enumerations are cold-path control
/// operations (the driver's control gate serializes them) and are safe to invoke between
/// generations.
/// </summary>
public interface IAdapterEnumerationProvider
{
    /// <summary>Returns the current MSTCP-bound adapters with their runtime link state.</summary>
    IReadOnlyList<AdapterEnumerationItem> Enumerate();
}

/// <summary>
/// The <see cref="IAdapterEnumerationProvider"/> over the real NDISAPI driver: one
/// <see cref="NdisApiDriver.GetAdapters"/> snapshot correlated through
/// <see cref="WindowsAdapterInventory"/>, with the MAC/MTU link state joined back by enumeration
/// handle. The driver is owned by the caller (the durable layer) for the whole run.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NdisAdapterEnumerationProvider : IAdapterEnumerationProvider
{
    private readonly NdisApiDriver _driver;

    public NdisAdapterEnumerationProvider(NdisApiDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _driver = driver;
    }

    public IReadOnlyList<AdapterEnumerationItem> Enumerate()
    {
        var ndisAdapters = _driver.GetAdapters();
        var inventory = new WindowsAdapterInventory(() => ndisAdapters
            .Select(adapter => (adapter.InternalName, adapter.RuntimeHandle, adapter.MacAddress, adapter.Mtu))
            .ToArray());
        var adapters = inventory.GetCurrentAdapters();
        var linkStateByHandle = ndisAdapters.ToDictionary(adapter => adapter.RuntimeHandle);
        var items = new List<AdapterEnumerationItem>(adapters.Count);
        foreach (var adapter in adapters)
        {
            var linkState = linkStateByHandle[adapter.RuntimeHandle];
            items.Add(new AdapterEnumerationItem(adapter, linkState.MacAddress, linkState.Mtu));
        }
        return items;
    }
}
