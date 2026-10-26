using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Logging;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The provisional host UDP response leg: it sends a relayed reply from the listener socket that
/// received the datagram, back to the client endpoint the host flow recorded as its local side. It is
/// scaffolding, not the product path.
/// </summary>
/// <remarks>
/// <para>
/// Measured against a real WFP redirect, a reply sent from the receiving listener socket exposes the
/// listener endpoint as the client-visible source, not the flow's original destination: an unconnected
/// client sees the proxy listener, and a connected client's accepted peer is the listener. The host UDP
/// proxy therefore stays disabled until the driver injects the relay reply with the original
/// destination as its source; that reply-source injection replaces this sink, and the
/// <see cref="IUdpResponseSink"/> seam stays the composition point. See the host UDP section of the
/// Kernel WFP architecture spec.
/// </para>
/// <para>
/// The listener is a dual-mode AF_INET6 socket while the host flow's client endpoint is normalized to
/// IPv4 when the redirect context says family 4 (the same normalization the receive path applied), so
/// an IPv4 destination is mapped back to <c>::ffff:a.b.c.d</c> before the send. That mapped form is the
/// endpoint shape <c>WSARecvMsg</c> returned for a native IPv4 datagram on this socket. A native IPv6
/// endpoint is sent unchanged.
/// </para>
/// <para>
/// A send failure is counted and logged, never thrown: it means the client's port is gone or the
/// listener is closing, and neither is evidence that the shared session or its transport failed.
/// </para>
/// </remarks>
internal sealed class HostUdpResponseSink(Socket udpSocket, ILogger logger) : IUdpResponseSink
{
    /// <summary>Reply sends that failed because the client endpoint or the listener was gone.</summary>
    private const string ResponseFailedCounter = "kernel.udp.responseFailed";

    private static readonly TimeSpan s_failureLogInterval = TimeSpan.FromSeconds(5);

    private readonly RuntimeLogThrottle _failureLog = new(s_failureLogInterval);
    private readonly RuntimeCounters _counters = RuntimeCounters.Shared;

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
    {
        var destination = ToListenerEndpoint(originalFlow.Local);
        try
        {
            _ = udpSocket.SendTo(payload.Span, SocketFlags.None, destination);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            _ = _counters.Increment(ResponseFailedCounter);
            if (_failureLog.ShouldEmit())
            {
                UdpProxyLog.UdpInjectionFailed(logger, exception.GetType().Name, exception.Message);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The send destination for a host flow's local endpoint: an IPv4 endpoint is mapped to the
    /// <c>::ffff:</c> form the dual-mode listener receives and sends with, an IPv6 endpoint is used
    /// as-is.
    /// </summary>
    internal static IPEndPoint ToListenerEndpoint(Endpoint local)
    {
        var address = local.Address.ToIPAddress();
        if (address.AddressFamily == AddressFamily.InterNetwork) address = address.MapToIPv6();
        return new IPEndPoint(address, local.Port);
    }
}
