using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.NdisApi.Tests;

/// <summary>
/// The static-filter ABI mirrors and the table encoder. Every record's size and field offset is pinned
/// against the native declaration, and the encoder is checked byte for byte against an independent
/// oracle (the test project builds the expected table from the documented offsets only), so an offset
/// typo or a swapped direction cannot pass by agreeing with itself.
/// </summary>
public sealed class NdisStaticFilterAbiTests
{
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_server = IPAddress.Parse("198.51.100.7");
    private static readonly IPAddress s_clientV6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_serverV6 = IPAddress.Parse("2001:db8::7");

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EveryMirroredRecordMatchesThePinnedNativeLayout() => NdisStaticFilterAbi.AssertManagedLayout();

    [Fact]
    [SupportedOSPlatform("windows")]
    public void MirrorSizesMatchTheNativeRecords()
    {
        Assert.Equal(184, Unsafe.SizeOf<StaticFilter>());
        Assert.Equal(8, Unsafe.SizeOf<StaticFilterTable>());
        Assert.Equal(24, Unsafe.SizeOf<DataLinkLayerFilter>());
        Assert.Equal(20, Unsafe.SizeOf<Eth8023Filter>());
        Assert.Equal(84, Unsafe.SizeOf<NetworkLayerFilter>());
        Assert.Equal(32, Unsafe.SizeOf<IPv4Filter>());
        Assert.Equal(80, Unsafe.SizeOf<IPv6Filter>());
        Assert.Equal(12, Unsafe.SizeOf<IPv4Address>());
        Assert.Equal(8, Unsafe.SizeOf<IPv4Subnet>());
        Assert.Equal(36, Unsafe.SizeOf<IPv6Address>());
        Assert.Equal(32, Unsafe.SizeOf<IPv6Subnet>());
        Assert.Equal(20, Unsafe.SizeOf<TransportLayerFilter>());
        Assert.Equal(16, Unsafe.SizeOf<TcpUdpFilter>());
        Assert.Equal(8, Unsafe.SizeOf<IcmpFilter>());
        Assert.Equal(2, Unsafe.SizeOf<ByteRange>());
        Assert.Equal(4, Unsafe.SizeOf<PortRange>());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TableLengthIsTheHeaderPlusTwoRecordsPerRule()
    {
        Assert.Equal(8, NdisStaticFilterAbi.EncodedSize(0));
        Assert.Equal(8 + 368, NdisStaticFilterAbi.EncodedSize(1));
        Assert.Equal(8 + (184 * 2 * 128), NdisStaticFilterAbi.EncodedSize(128));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnIpv4RuleEncodesTwiceWithSwappedEndpoints()
    {
        var source = s_client.GetAddressBytes();
        var destination = s_server.GetAddressBytes();
        var rules = new[] { new NdisTcpFlowRule(s_client, 53000, s_server, 443) };
        var table = new byte[NdisStaticFilterAbi.EncodedSize(rules.Length)];

        Assert.True(NdisStaticFilterAbi.TryWriteTable(table, rules, out var written));

        var expected = StaticFilterTableOracle.Table(
            StaticFilterTableOracle.Oriented(from: source, fromPort: 53000, to: destination, toPort: 443),
            StaticFilterTableOracle.Oriented(from: destination, fromPort: 443, to: source, toPort: 53000));
        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, table);
        // The header counts records, not rules.
        Assert.Equal(2u, BitConverter.ToUInt32(table, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(table, 4));
        // The native IPv4 DWORD is the little-endian reading of the four network-order bytes.
        Assert.Equal(BitConverter.ToUInt32(source, 0), BitConverter.ToUInt32(table, 8 + 92));
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(table, 8 + 96));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnIpv6RuleEncodesSixteenByteAddressesAndAllOnesMasks()
    {
        var source = s_clientV6.GetAddressBytes();
        var destination = s_serverV6.GetAddressBytes();
        var rules = new[] { new NdisTcpFlowRule(s_clientV6, 53001, s_serverV6, 8443) };
        var table = new byte[NdisStaticFilterAbi.EncodedSize(rules.Length)];

        Assert.True(NdisStaticFilterAbi.TryWriteTable(table, rules, out var written));

        var expected = StaticFilterTableOracle.Table(
            StaticFilterTableOracle.Oriented(from: source, fromPort: 53001, to: destination, toPort: 8443),
            StaticFilterTableOracle.Oriented(from: destination, fromPort: 8443, to: source, toPort: 53001));
        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, table);
        // The IPv6 network union selector, read at the pinned offset rather than through the mirror.
        Assert.Equal(2u, BitConverter.ToUInt32(table, 8 + 80));
        for (var offset = 8 + 108; offset < 8 + 124; offset++) Assert.Equal(0xFF, table[offset]);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EveryEntryCarriesAdapterZeroBothDirectionsAndPassOnly()
    {
        var rules = new[] { new NdisTcpFlowRule(s_client, 53000, s_server, 443) };
        var table = new byte[NdisStaticFilterAbi.EncodedSize(rules.Length)];
        Assert.True(NdisStaticFilterAbi.TryWriteTable(table, rules, out _));

        for (var entry = 0; entry < 2; entry++)
        {
            var (_, _, _, _, action, adapter, directionFlags) = StaticFilterTableOracle.ReadEntry(table, entry);
            Assert.Equal(StaticFilterTableOracle.FilterPacketPass, action);
            Assert.Equal(0UL, adapter);
            Assert.Equal(StaticFilterTableOracle.BothDirections, directionFlags);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ZeroRulesAreNeverEncoded()
    {
        var table = new byte[NdisStaticFilterAbi.EncodedSize(1)];
        table.AsSpan().Fill(0xA5);

        Assert.False(NdisStaticFilterAbi.TryWriteTable(table, [], out var written));

        Assert.Equal(0, written);
        Assert.All(table, value => Assert.Equal(0xA5, value));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AMixedFamilyRuleIsRefused()
    {
        var table = new byte[NdisStaticFilterAbi.EncodedSize(1)];
        var rules = new[] { new NdisTcpFlowRule(s_client, 1000, s_serverV6, 2000) };

        Assert.False(NdisStaticFilterAbi.TryWriteTable(table, rules, out var written));

        Assert.Equal(0, written);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ADestinationTooShortForTheTableIsRefused()
    {
        var rules = new[] { new NdisTcpFlowRule(s_client, 53000, s_server, 443) };
        var table = new byte[NdisStaticFilterAbi.EncodedSize(rules.Length) - 1];

        Assert.False(NdisStaticFilterAbi.TryWriteTable(table, rules, out _));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EncodeabilityRefusesScopedLinkLocalMixedFamilyAndZeroPortTuples()
    {
        var request = new NdisTcpFlowRule(s_client, 53000, s_server, 443);
        Assert.True(request.IsEncodable);

        var globalV6 = new NdisTcpFlowRule(s_clientV6, 53000, s_serverV6, 443);
        Assert.True(globalV6.IsEncodable);

        var linkLocal = new NdisTcpFlowRule(IPAddress.Parse("fe80::1"), 53000, s_serverV6, 443);
        Assert.False(linkLocal.IsEncodable);
        var scopedAddress = IPAddressValue.FromIPv6(s_clientV6.GetAddressBytes(), scopeId: 7);
        Assert.False(new NdisTcpFlowRule(scopedAddress, 53000, s_serverV6, 443).IsEncodable);
        var zeroPort = new NdisTcpFlowRule(s_client, 0, s_server, 443);
        Assert.False(zeroPort.IsEncodable);
        var mixedFamily = new NdisTcpFlowRule(s_client, 53000, s_serverV6, 443);
        Assert.False(mixedFamily.IsEncodable);
    }
}
