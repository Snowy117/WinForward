using System.Net;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Windows;

namespace WinForward.Runtime;

/// <summary>
/// Builds a <see cref="FlowContext"/> from a classified packet and the adapter it was observed on.
/// Pure and hardware-independent so flow classification can be unit-tested on any OS.
/// </summary>
public static class PacketFlowClassifier
{
    /// <summary>
    /// Classifies a parseable TCP/UDP packet. The flow origin follows the first-observation rule:
    /// an ON_SEND packet (MSTCP toward the adapter) establishes a host flow, while an ON_RECEIVE
    /// packet (adapter toward MSTCP) establishes a forwarded flow. Process identity is intentionally
    /// left null here; it is resolved by the <see cref="FlowDispatcher"/> for host flows when needed.
    /// </summary>
    public static FlowContext ClassifyFlow(PacketView view, WindowsAdapter adapter, bool isOnSend)
    {
        var adapterContext = new AdapterContext(adapter.StableId, adapter.FriendlyName, adapter.Generation);
        var protocol = view.Transport == PacketTransport.Tcp ? TransportProtocol.Tcp : TransportProtocol.Udp;
        var local = Endpoint.From(view.SourceAddress, view.SourcePort);
        var remote = Endpoint.From(view.DestinationAddress, view.DestinationPort);
        var origin = isOnSend ? FlowOriginKind.Host : FlowOriginKind.Forwarded;
        var key = FlowKey.Create(local, remote, protocol, origin, adapterContext);
        return new FlowContext(key, null, null, adapter.StableId, adapter.FriendlyName, remote.Port);
    }

    /// <summary>
    /// Builds a minimal context for a frame that cannot be classified into a TCP/UDP flow. Only the
    /// adapter identity is meaningful for policy matching; the degenerate endpoints represent the
    /// absence of a flow and are never used as a flow-table key.
    /// </summary>
    public static FlowContext ClassifyNonFlow(WindowsAdapter adapter, bool isOnSend)
    {
        var adapterContext = new AdapterContext(adapter.StableId, adapter.FriendlyName, adapter.Generation);
        var origin = isOnSend ? FlowOriginKind.Host : FlowOriginKind.Forwarded;
        var key = FlowKey.Create(Endpoint.From(IPAddress.Any, 0), Endpoint.From(IPAddress.Any, 0), TransportProtocol.Udp, origin, adapterContext);
        return new FlowContext(key, null, null, adapter.StableId, adapter.FriendlyName, 0);
    }
}