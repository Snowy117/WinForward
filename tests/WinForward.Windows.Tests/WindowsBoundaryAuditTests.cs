using System.Net;
using Xunit;

namespace WinForward.Windows.Tests;

public sealed class WindowsBoundaryAuditTests
{
    [Fact]
    public void UdpOwnerPidTableClassIsSharedByIPv4AndIPv6()
    {
        Assert.Equal(1, IPHelperAbi.UdpTableOwnerPid);
    }

    [Fact]
    public void IPv6ProjectionPreservesHostOrderScopeId()
    {
        const uint scopeId = 7;

        var address = IPHelperAbi.DecodeIPv6Address(IPAddress.Parse("fe80::1").GetAddressBytes(), scopeId);

        Assert.Equal(7L, address.ScopeId);
    }

    [Fact]
    public void OwnerPortProjectionConvertsNetworkOrderLowWord()
    {
        var networkOrderPort = unchecked((uint)(ushort)IPAddress.HostToNetworkOrder((short)8080));

        Assert.Equal((ushort)8080, IPHelperAbi.DecodeNetworkPort(networkOrderPort));
    }
}
