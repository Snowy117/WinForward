using System.Net;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class PacketParsingTests
{
    // ---- Packet parser (IpTcpUdpPacket) ----

    [Fact]
    public void ParsesIpv4TcpAndUdpAndRejectsNonTcpUdp()
    {
        Assert.True(IpTcpUdpPacket.TryParse(FrameBuilders.CreateIpv4TcpFrame(), out var tcp));
        Assert.Equal(PacketTransport.Tcp, tcp.Transport);
        Assert.Equal((ushort)53000, tcp.SourcePort);
        Assert.Equal((ushort)443, tcp.DestinationPort);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), tcp.SourceAddress);
        Assert.Equal(IPAddress.Parse("192.0.2.53"), tcp.DestinationAddress);

        Assert.True(IpTcpUdpPacket.TryParse(FrameBuilders.CreateIpv4UdpFrame(), out var udp));
        Assert.Equal(PacketTransport.Udp, udp.Transport);
        Assert.Equal((ushort)53, udp.DestinationPort);

        var icmp = FrameBuilders.CreateIpv4UdpFrame();
        icmp[23] = 1; // ICMP
        Assert.False(IpTcpUdpPacket.TryParse(icmp, out _));
    }

    [Fact]
    public void ParsesIpv6TcpAndUdp()
    {
        Assert.True(IpTcpUdpPacket.TryParse(FrameBuilders.CreateIpv6TcpFrame(), out var tcp));
        Assert.Equal(PacketTransport.Tcp, tcp.Transport);
        Assert.Equal(IPAddress.Parse("2001:db8::10"), tcp.SourceAddress);
        Assert.Equal((ushort)443, tcp.DestinationPort);

        Assert.True(IpTcpUdpPacket.TryParse(FrameBuilders.CreateIpv6UdpFrame(), out var udp));
        Assert.Equal(PacketTransport.Udp, udp.Transport);
        Assert.Equal(IPAddress.Parse("2001:db8::53"), udp.DestinationAddress);
    }

    [Fact]
    public void RejectsIpv4FragmentsAndTruncatedFrames()
    {
        var frame = FrameBuilders.CreateIpv4TcpFrame();
        frame[20] = 0x20; // MF fragment flag
        Assert.False(IpTcpUdpPacket.TryParse(frame, out _));

        Assert.False(IpTcpUdpPacket.TryParse(frame.AsSpan(0, 14 + 12), out _));
    }

    [Fact]
    public void RejectsNonIpAndNonEthernetFrames()
    {
        var arp = new byte[14 + 28];
        arp[12] = 0x08;
        arp[13] = 0x06; // ARP
        Assert.False(IpTcpUdpPacket.TryParse(arp, out _));

        Assert.False(IpTcpUdpPacket.TryParse(new byte[10], out _));
    }

    // ---- Flow classifier ----

    [Fact]
    public void ClassifyFlowUsesDirectionForOriginAndAdapterForIdentity()
    {
        var adapter = new WindowsAdapter("id-a", "Ethernet", "internal-a", 1, 7);
        var view = new PacketView(PacketTransport.Tcp, IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.53"), 53000, 443, 20, 20);

        var host = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend: true);
        Assert.Equal(FlowOriginKind.Host, host.Key.Origin);
        Assert.Equal("id-a", host.AdapterId);
        Assert.Equal("Ethernet", host.AdapterName);
        Assert.Equal((ushort)443, host.RemotePort);
        Assert.Equal(adapter.Generation, host.Key.OriginAdapterGeneration);

        var forwarded = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend: false);
        Assert.Equal(FlowOriginKind.Forwarded, forwarded.Key.Origin);
    }

    [Fact]
    public void ClassifyFlowPreservesIpv6Endpoints()
    {
        var adapter = new WindowsAdapter("id-6", "vEthernet", "internal-6", 2, 1);
        var view = new PacketView(PacketTransport.Udp, IPAddress.Parse("2001:db8::10"), IPAddress.Parse("2001:db8::53"), 53000, 53, 40, 8);

        var context = PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend: false);

        Assert.Equal(AddressFamilyKind.IPv6, context.Key.AddressFamily);
        Assert.Equal(Endpoint.From(IPAddress.Parse("2001:db8::10"), 53000), context.Key.Local);
        Assert.Equal(Endpoint.From(IPAddress.Parse("2001:db8::53"), 53), context.Key.Remote);
    }
}
