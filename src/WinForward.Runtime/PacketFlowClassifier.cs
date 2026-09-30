using WinForward.Core;
using WinForward.Protocols;
using WinForward.Windows;

namespace WinForward.Runtime;

/// <summary>
/// Builds a <see cref="FlowContext"/> from a classified packet and the adapter it was observed on.
/// Pure and hardware-independent so flow classification can be unit-tested on any OS. The adapter
/// arrives as its interned slot (resolved once per adapter per capture generation) plus the
/// adapter's own generation, so no string enters the key and no lookup runs per packet: the
/// context's adapter identity is the slot table's own published metadata, read by index.
/// </summary>
public static class PacketFlowClassifier
{
    /// <summary>
    /// Classifies a parseable TCP/UDP packet. The flow origin follows the first-observation rule:
    /// an ON_SEND packet (MSTCP toward the adapter) establishes a host flow, while an ON_RECEIVE
    /// packet (adapter toward MSTCP) establishes a forwarded flow. Process identity is intentionally
    /// left null here; it is resolved by the <see cref="FlowDispatcher"/> for host flows when needed.
    /// </summary>
    public static FlowContext ClassifyFlow(PacketView view, WindowsAdapter adapter, bool isOnSend, ushort adapterSlot, AdapterSlotTable slots)
    {
        var protocol = view.Transport == PacketTransport.Tcp ? TransportProtocol.Tcp : TransportProtocol.Udp;
        var local = Endpoint.From(view.SourceAddress, view.SourcePort);
        var remote = Endpoint.From(view.DestinationAddress, view.DestinationPort);
        var origin = isOnSend ? FlowOriginKind.Host : FlowOriginKind.Forwarded;
        var key = FlowKey.Create(local, remote, protocol, origin, adapterSlot, adapter.Generation);
        return new FlowContext(key, ResolveAdapter(slots, adapterSlot), Process: null);
    }

    /// <summary>
    /// Builds a minimal context for a frame that cannot be classified into a TCP/UDP flow. Only the
    /// adapter identity is meaningful for policy matching; the degenerate endpoints represent the
    /// absence of a flow and are never used as a flow-table key.
    /// </summary>
    public static FlowContext ClassifyNonFlow(WindowsAdapter adapter, bool isOnSend, ushort adapterSlot, AdapterSlotTable slots)
    {
        var origin = isOnSend ? FlowOriginKind.Host : FlowOriginKind.Forwarded;
        var key = FlowKey.Create(Endpoint.From(IPAddressValue.IPv4Any, 0), Endpoint.From(IPAddressValue.IPv4Any, 0), TransportProtocol.Udp, origin, adapterSlot, adapter.Generation);
        return new FlowContext(key, ResolveAdapter(slots, adapterSlot), Process: null);
    }

    /// <summary>
    /// The slot's published metadata, one array index, no allocation. A slot the table does not know
    /// (an adapter-less key, or an adapter that was never interned) yields no identity, so an
    /// adapter-qualified policy rule sees a null adapter exactly as it does for an adapter-less key.
    /// </summary>
    private static AdapterMetadata? ResolveAdapter(AdapterSlotTable slots, ushort adapterSlot) =>
        slots.TryResolve(adapterSlot, out var metadata) ? metadata : null;
}
