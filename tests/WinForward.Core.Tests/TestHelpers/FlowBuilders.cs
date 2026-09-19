using System.Net;
using WinForward.Core;

namespace WinForward.Core.Tests;

/// <summary>
/// Flow-key builders shared by the UDP coordinator suites: the canonical host-to-DNS flow
/// shape (client 192.0.2.10:53000, remote :53) the setup-queue, admission, and budget tests
/// build their flows from.
/// </summary>
internal static class FlowBuilders
{
    public static FlowKey CreateFlow(string remoteAddress) =>
        FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse(remoteAddress), 53), TransportProtocol.Udp, FlowOriginKind.Host);
}
