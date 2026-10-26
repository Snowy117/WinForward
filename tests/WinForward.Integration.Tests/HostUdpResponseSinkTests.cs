using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Cli.Backends.Kernel;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;
using Xunit;

namespace WinForward.Integration.Tests;

/// <summary>
/// The host reply leg: the endpoint a reply is addressed to and the fact that it leaves the socket the
/// datagram arrived on.
/// </summary>
public sealed class HostUdpResponseSinkTests
{
    /// <summary>
    /// The multi-homed listener is AF_INET6, while a family-4 association's client endpoint is
    /// normalized to IPv4 for the flow key. The reply must use the mapped form the socket received the
    /// datagram with, not a bare IPv4 endpoint.
    /// </summary>
    [Fact]
    public void AnIpv4FlowLocalIsAddressedAsItsMappedIpv6Form()
    {
        var flowLocal = Endpoint.From(IPAddress.Parse("192.0.2.10"), 51_000);

        var destination = HostUdpResponseSink.ToListenerEndpoint(flowLocal);

        Assert.Equal(AddressFamily.InterNetworkV6, destination.AddressFamily);
        Assert.True(destination.Address.IsIPv4MappedToIPv6);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), destination.Address.MapToIPv4());
        Assert.Equal(51_000, destination.Port);
    }

    [Fact]
    public void ANativeIpv6FlowLocalIsAddressedUnchanged()
    {
        var flowLocal = Endpoint.From(IPAddress.Parse("2001:db8::10"), 51_000);

        var destination = HostUdpResponseSink.ToListenerEndpoint(flowLocal);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8::10"), 51_000), destination);
    }

    /// <summary>
    /// End to end on loopback: a dual-mode listener sends a reply to the mapped client endpoint and the
    /// IPv4 client socket receives the payload from it.
    /// </summary>
    [Fact]
    public async Task ThePayloadLeavesTheListenerSocketForTheClientEndpoint()
    {
        using var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var clientPort = checked((ushort)((IPEndPoint)client.LocalEndPoint!).Port);
        var flow = FlowKey.Create(
            Endpoint.From(IPAddress.Loopback, clientPort),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 40010),
            TransportProtocol.Udp,
            FlowOriginKind.Host);
        var sink = new HostUdpResponseSink(listener, NullLogger.Instance);

        await sink.InjectAsync(flow, flow.Remote, new byte[] { 1, 2, 3, 4 }, MacAddress.Invalid, CancellationToken.None);

        Assert.True(client.Poll(5_000_000, SelectMode.SelectRead), "The client did not receive the relayed reply.");
        var buffer = new byte[64];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var received = client.ReceiveFrom(buffer, SocketFlags.None, ref remote);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer[..received]);
    }

    /// <summary>A listener that is already closed must count and swallow the failure, never throw into the session.</summary>
    [Fact]
    public async Task ASendOnAClosedListenerIsSwallowed()
    {
        var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        var sink = new HostUdpResponseSink(listener, NullLogger.Instance);
        listener.Dispose();

        var flow = FlowKey.Create(
            Endpoint.From(IPAddress.Loopback, 51_000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 40010),
            TransportProtocol.Udp,
            FlowOriginKind.Host);

        await sink.InjectAsync(flow, flow.Remote, new byte[] { 1 }, MacAddress.Invalid, CancellationToken.None);
    }
}
