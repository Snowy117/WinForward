using System.Net;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class WindowsBoundaryAuditTests
{
    [Fact]
    public void UdpOwnerPidTableClassIsSharedByIpv4AndIpv6()
    {
        Assert.Equal(1, IpHelperAbi.UdpTableOwnerPid);
    }

    [Fact]
    public void Ipv6ProjectionPreservesHostOrderScopeId()
    {
        const uint scopeId = 7;

        var address = IpHelperAbi.DecodeIpv6Address(IPAddress.Parse("fe80::1").GetAddressBytes(), scopeId);

        Assert.Equal(7L, address.ScopeId);
    }

    [Fact]
    public void OwnerPortProjectionConvertsNetworkOrderLowWord()
    {
        var networkOrderPort = unchecked((uint)(ushort)IPAddress.HostToNetworkOrder((short)8080));

        Assert.Equal((ushort)8080, IpHelperAbi.DecodeNetworkPort(networkOrderPort));
    }
}
