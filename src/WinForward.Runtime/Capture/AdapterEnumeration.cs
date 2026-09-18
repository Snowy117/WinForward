using System.ComponentModel;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// One correlated adapter plus the NDIS runtime link state that the enumeration handles carry
/// (design §3.5 of task 09-07-adapter-list-refresh): the kernel handle, the adapter MAC, the
/// MTU, and — since task 09-17-adapter-staleness-logging — the per-adapter unicast-address
/// fingerprint. A refresh diffs this state per stable ID to decide whether the adapter view went
/// stale: handles are fresh pointers after every bound-list rebuild, MAC/MTU changes matter to
/// the UDP reinjection targets, and the address fingerprint catches host link-state changes the
/// NDISRD list never signals (IPv6 temporary-address rotation, address add/remove), so any of
/// the four changing demands a new generation. An empty fingerprint means "unknown" and compares
/// equal only to another empty fingerprint.
/// </summary>
public sealed record AdapterEnumerationItem(WindowsAdapter Adapter, byte[] Mac, ushort Mtu, string AddressFingerprint = "")
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
/// handle and the unicast-address fingerprint resolved through
/// <see cref="UnicastAddressInventory"/> by interface GUID. The driver is owned by the caller
/// (the durable layer) for the whole run.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NdisAdapterEnumerationProvider : IAdapterEnumerationProvider
{
    private static readonly IReadOnlyDictionary<string, string> EmptyFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly NdisApiDriver _driver;
    private readonly IRuntimeLogger? _logger;
    private int _addressQueryFailureLogged;

    public NdisAdapterEnumerationProvider(NdisApiDriver driver, IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _driver = driver;
        _logger = logger;
    }

    public IReadOnlyList<AdapterEnumerationItem> Enumerate()
    {
        var ndisAdapters = _driver.GetAdapters();
        var inventory = new WindowsAdapterInventory(() => ndisAdapters
            .Select(adapter => (adapter.InternalName, adapter.RuntimeHandle, adapter.MacAddress, adapter.Mtu))
            .ToArray());
        var adapters = inventory.GetCurrentAdapters();
        var linkStateByHandle = ndisAdapters.ToDictionary(adapter => adapter.RuntimeHandle);
        var fingerprints = ReadAddressFingerprintsTolerantly();
        var items = new List<AdapterEnumerationItem>(adapters.Count);
        foreach (var adapter in adapters)
        {
            var linkState = linkStateByHandle[adapter.RuntimeHandle];
            items.Add(new AdapterEnumerationItem(
                adapter,
                linkState.MacAddress,
                linkState.Mtu,
                UnicastAddressInventory.ResolveFingerprint(adapter.StableId, fingerprints)));
        }
        return items;
    }

    /// <summary>
    /// A broken address query must never break capture (task 09-17 R1-A): failures degrade to
    /// empty fingerprints — enumeration still produces handles/MACs/MTUs, the refresh diff just
    /// stops seeing address changes — with a one-shot debug event leaving a forensic trace.
    /// </summary>
    private IReadOnlyDictionary<string, string> ReadAddressFingerprintsTolerantly()
    {
        try
        {
            return UnicastAddressInventory.ReadAddressFingerprints();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
        {
            if (Interlocked.Exchange(ref _addressQueryFailureLogged, 1) == 0 && _logger is { } logger)
            {
                logger.Event(RuntimeLogLevel.Debug, "adapter.addressQuery.failed",
                    new RuntimeLogField("error", exception.Message));
            }
            return EmptyFingerprints;
        }
    }
}
