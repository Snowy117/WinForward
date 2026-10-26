using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Userland.Device;
using WinForward.Userland.Relay;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>Why a host UDP datagram was dropped before it reached the coordinator.</summary>
internal enum UdpDatagramDropReason
{
    /// <summary>Not a drop.</summary>
    None = 0,

    /// <summary>The payload is larger than the host datagram cap.</summary>
    Oversized,

    /// <summary>The datagram carried no WFP redirect context, so it is not a redirected association.</summary>
    MissingContext,

    /// <summary>The context chain was malformed or its 36-byte payload did not decode.</summary>
    InvalidContext,

    /// <summary>The context's protocol is not UDP.</summary>
    NonUdpProtocol,

    /// <summary>The client endpoint's family still differs from the context's after normalization.</summary>
    FamilyMismatch,

    /// <summary>The rule index, the rule's action or its target cannot serve this datagram.</summary>
    TargetUnavailable,

    /// <summary>The shared engine refused the admission (capacity, setup ring or cooldown).</summary>
    AdmissionRefused,
}

/// <summary>One datagram admitted for the coordinator: the host flow key and the resolved target.</summary>
internal readonly record struct UdpDatagramPlan(FlowKey Flow, ProxyTarget Target, IPEndPoint Client);

/// <summary>
/// Turns one datagram received on the host listener into a coordinator admission, or names the
/// fail-closed reason it was refused.
/// </summary>
/// <remarks>
/// <para>
/// The original destination exists only in the association's redirect context, which arrives as a
/// control message on every datagram. A datagram without one did not come from the driver — the
/// listener port is locally reachable, so any process can send to it — and must never create a session.
/// </para>
/// <para>
/// The context family and the client endpoint must agree, because <see cref="FlowKey"/> refuses
/// endpoints of different families and the reply has to go back to the endpoint the datagram arrived
/// from. The driver normalizes any <c>::ffff:0:0/96</c> destination to pure IPv4 before building the
/// context, so a v4-mapped client source is mapped to IPv4 here when the context says family 4. The
/// cmsg level is not consulted: it names the layer that delivered the datagram, and a v4-mapped
/// connected association is delivered by the IPv6 layer while carrying a family-4 context.
/// </para>
/// <para>
/// The payload length is checked before admission on purpose. The transport's oversize skip is a
/// receive-side class only, and the shared engine's send path treats an over-cap payload as a genuine
/// transport failure, which removes the flow's live session and rethrows into the caller.
/// </para>
/// </remarks>
internal static class KernelHostUdpDatagram
{
    /// <summary>
    /// The host datagram payload cap in bytes: the shared engine's frame size for this composition.
    /// It covers DNS/EDNS0 answers and bounds each session's receive window.
    /// </summary>
    internal const int MaximumDatagramBytes = 4096;

    internal static bool TryPlan(
        IPEndPoint source,
        ReadOnlySpan<byte> control,
        int payloadLength,
        ProxyTarget?[] targetsByRuleIndex,
        out UdpDatagramPlan plan,
        out UdpDatagramDropReason reason,
        out string detail)
    {
        ArgumentNullException.ThrowIfNull(source);

        plan = default;
        if (payloadLength > MaximumDatagramBytes)
        {
            reason = UdpDatagramDropReason.Oversized;
            detail = string.Create(CultureInfo.InvariantCulture, $"{payloadLength} byte(s) exceed the {MaximumDatagramBytes}-byte host datagram cap");
            return false;
        }

        if (!WfpUdpContext.TryParseControl(control, out var contextPayload, out _))
        {
            reason = UdpDatagramDropReason.MissingContext;
            detail = "the datagram carries no WFP redirect context";
            return false;
        }

        if (!RedirectDestination.TryParse(contextPayload, out var destination, out var parseError) || destination is null)
        {
            reason = UdpDatagramDropReason.InvalidContext;
            detail = parseError;
            return false;
        }

        if (destination.Protocol != RedirectContext.ProtocolUdp)
        {
            reason = UdpDatagramDropReason.NonUdpProtocol;
            detail = $"the redirect context protocol is {destination.Protocol}, not UDP";
            return false;
        }

        var client = Normalize(source, destination.EndPoint.AddressFamily);
        if (client.AddressFamily != destination.EndPoint.AddressFamily)
        {
            reason = UdpDatagramDropReason.FamilyMismatch;
            detail = $"the client endpoint is {client.AddressFamily} but the redirect context destination is {destination.EndPoint.AddressFamily}";
            return false;
        }

        if (!KernelHostTargetResolver.TryResolve(targetsByRuleIndex, destination.RuleIndex, out var target, out var targetError))
        {
            reason = UdpDatagramDropReason.TargetUnavailable;
            detail = targetError;
            return false;
        }

        var flow = FlowKey.Create(
            Endpoint.From(client.Address, checked((ushort)client.Port)),
            Endpoint.From(destination.EndPoint.Address, checked((ushort)destination.EndPoint.Port)),
            TransportProtocol.Udp,
            FlowOriginKind.Host);
        plan = new UdpDatagramPlan(flow, target, client);
        reason = UdpDatagramDropReason.None;
        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// Maps a v4-mapped client endpoint to IPv4 when the context is family 4. A native endpoint of
    /// either family is returned unchanged; the caller drops the datagram if a family still differs.
    /// </summary>
    internal static IPEndPoint Normalize(IPEndPoint source, AddressFamily contextFamily)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (contextFamily != AddressFamily.InterNetwork || source.AddressFamily != AddressFamily.InterNetworkV6) return source;
        return source.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(source.Address.MapToIPv4(), source.Port) : source;
    }
}
