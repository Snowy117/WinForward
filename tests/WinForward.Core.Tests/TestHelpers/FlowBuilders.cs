using System.Net;

namespace WinForward.Core.Tests;

/// <summary>
/// Flow-key builders shared by the UDP coordinator suites: the canonical host-to-DNS flow
/// shape (client 192.0.2.10:53000, remote :53) the setup-queue, admission, and budget tests
/// build their flows from.
/// </summary>
internal static class FlowBuilders
{
    /// <summary>
    /// The test-wide adapter interning table. A stable ID keeps one slot for the whole test process,
    /// so keys built for the same adapter compare equal across a test, exactly as capture-scope keys
    /// do (slots are never reused), while keys for different IDs never do.
    /// </summary>
    internal static AdapterSlotTable Slots { get; } = new();

    /// <summary>
    /// Interns (or re-resolves) a test adapter's stable ID and returns its slot; the friendly name
    /// defaults to the ID, and passing the adapter's own name mirrors the generation build's interning.
    /// </summary>
    internal static ushort SlotOf(string stableId, long generation = 0, string? friendlyName = null)
    {
        Slots.TryIntern(stableId, generation, friendlyName ?? stableId, out var slot);
        return slot;
    }

    public static FlowKey CreateFlow(string remoteAddress) =>
        FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse(remoteAddress), 53), TransportProtocol.Udp, FlowOriginKind.Host);

    /// <summary>
    /// A context for tests that do not exercise interning: the identity strings are wrapped in the
    /// same interned metadata shapes the production edges publish, so the AdapterId/AdapterName/
    /// ProcessName/ProcessPath surface behaves exactly as it does on a real packet. The adapter
    /// metadata carries the key's own slot, and the remote port is the key's, as in production.
    /// </summary>
    internal static FlowContext Context(FlowKey key, string? processName = null, string? processPath = null, string? adapterId = null, string? adapterName = null) =>
        new(key, Adapter(key.OriginAdapterSlot, adapterId, adapterName), Process(processName, processPath));

    /// <summary>An interned adapter identity stand-in; null when the test names no adapter.</summary>
    private static AdapterMetadata? Adapter(ushort slot, string? stableId, string? friendlyName = null) =>
        stableId is null ? null : new AdapterMetadata(slot, stableId, friendlyName ?? stableId, 0);

    /// <summary>An attributed process identity stand-in; null when the test names no process.</summary>
    private static ProcessMetadata? Process(string? processName, string? processPath = null) =>
        processName is null && processPath is null ? null : new ProcessMetadata(processName, processPath);
}
