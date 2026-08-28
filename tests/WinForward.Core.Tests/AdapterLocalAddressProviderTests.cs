using System.Net;
using System.Runtime.Versioning;
using WinForward.Core;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class AdapterLocalAddressProviderTests
{
    private const string AdapterGuid = "{E14A2A2E-F7E2-4428-942E-6D04D1C6D797}";

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SelectsIpv4AddressOnCorrelatedAdapter()
    {
        var provider = new WindowsAdapterLocalAddressProvider(() =>
        [
            new IPAdapterUnicastInfo(AdapterGuid,
            [
                new IPAdapterUnicastAddress(IPAddress.Parse("192.168.77.1"), IPAddress.Parse("255.255.255.0")),
                new IPAdapterUnicastAddress(IPAddress.Parse("192.168.100.1"), IPAddress.Parse("255.255.255.0"))
            ])
        ]);

        var selected = provider.SelectLocalAddress(AdapterGuid, AddressFamilyKind.IPv4, IPAddress.Parse("192.168.100.6"));

        Assert.Equal(IPAddress.Parse("192.168.100.1"), selected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void LoopbackAddressesAreNeverCandidates()
    {
        var provider = new WindowsAdapterLocalAddressProvider(() =>
        [
            new IPAdapterUnicastInfo(AdapterGuid,
            [
                new IPAdapterUnicastAddress(IPAddress.Parse("127.0.0.1"), IPAddress.Parse("255.0.0.0")),
                new IPAdapterUnicastAddress(IPAddress.Parse("192.168.77.1"), IPAddress.Parse("255.255.255.0"))
            ])
        ]);

        var selected = provider.SelectLocalAddress(AdapterGuid, AddressFamilyKind.IPv4, IPAddress.Parse("192.168.77.6"));

        Assert.Equal(IPAddress.Parse("192.168.77.1"), selected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CorrelatesBraceAndCaseInsensitiveGuid()
    {
        var provider = new WindowsAdapterLocalAddressProvider(() =>
        [
            new IPAdapterUnicastInfo("{e14a2a2e-f7e2-4428-942e-6d04d1c6d797}",
            [
                new IPAdapterUnicastAddress(IPAddress.Parse("192.168.77.1"), IPAddress.Parse("255.255.255.0"))
            ])
        ]);

        var selected = provider.SelectLocalAddress(AdapterGuid, AddressFamilyKind.IPv4, IPAddress.Parse("192.168.77.6"));

        Assert.Equal(IPAddress.Parse("192.168.77.1"), selected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void FallsBackToFirstCandidateWhenSubnetDoesNotMatch()
    {
        var provider = new WindowsAdapterLocalAddressProvider(() =>
        [
            new IPAdapterUnicastInfo(AdapterGuid,
            [
                new IPAdapterUnicastAddress(IPAddress.Parse("192.168.77.1"), IPAddress.Parse("255.255.255.0"))
            ])
        ]);

        var selected = provider.SelectLocalAddress(AdapterGuid, AddressFamilyKind.IPv4, IPAddress.Parse("10.1.2.3"));

        Assert.Equal(IPAddress.Parse("192.168.77.1"), selected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReturnsNullWhenAdapterIsUnknownOrFamilyMissing()
    {
        var provider = new WindowsAdapterLocalAddressProvider(() =>
        [
            new IPAdapterUnicastInfo(AdapterGuid,
            [
                new IPAdapterUnicastAddress(IPAddress.Parse("192.168.77.1"), IPAddress.Parse("255.255.255.0"))
            ])
        ]);

        Assert.Null(provider.SelectLocalAddress("{11111111-2222-3333-4444-555555555555}", AddressFamilyKind.IPv4, IPAddress.Parse("192.168.77.6")));
        Assert.Null(provider.SelectLocalAddress(AdapterGuid, AddressFamilyKind.IPv6, IPAddress.Parse("fd00:1234:5678:1::6")));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ExcludesIpv6LinkLocalAndPrefersClientPrefix()
    {
        var provider = new WindowsAdapterLocalAddressProvider(() =>
        [
            new IPAdapterUnicastInfo(AdapterGuid,
            [
                new IPAdapterUnicastAddress(IPAddress.Parse("fe80::abcd"), null),
                new IPAdapterUnicastAddress(IPAddress.Parse("fd00:1234:5678:2::1"), null),
                new IPAdapterUnicastAddress(IPAddress.Parse("fd00:1234:5678:1::1"), null)
            ])
        ]);

        var selected = provider.SelectLocalAddress(AdapterGuid, AddressFamilyKind.IPv6, IPAddress.Parse("fd00:1234:5678:1::6"));

        Assert.Equal(IPAddress.Parse("fd00:1234:5678:1::1"), selected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ZeroMaskIsTreatedAsUnknownAndDoesNotMatchSubnet()
    {
        var provider = new WindowsAdapterLocalAddressProvider(() =>
        [
            new IPAdapterUnicastInfo(AdapterGuid,
            [
                new IPAdapterUnicastAddress(IPAddress.Parse("192.168.77.1"), IPAddress.Parse("0.0.0.0")),
                new IPAdapterUnicastAddress(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.0.0.0"))
            ])
        ]);

        var selected = provider.SelectLocalAddress(AdapterGuid, AddressFamilyKind.IPv4, IPAddress.Parse("10.9.8.7"));

        Assert.Equal(IPAddress.Parse("10.0.0.1"), selected);
    }
}
