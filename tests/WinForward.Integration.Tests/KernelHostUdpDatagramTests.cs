using System.Net;
using WinForward.Cli.Backends.Kernel;
using WinForward.Configuration;
using WinForward.Core;
using Xunit;

namespace WinForward.Integration.Tests;

/// <summary>
/// The per-datagram decision on the host listener: which redirect context produces an admitted host
/// flow, how the client endpoint is normalized against the context family, and which shapes must be
/// refused before the coordinator sees them.
/// </summary>
public sealed class KernelHostUdpDatagramTests
{
    private static readonly ProxyTarget s_server = ProxyTarget.FromServer(
        new Socks5Server("main", "127.0.0.1", 1080, Username: null, Password: null));

    [Fact]
    public void AValidUdpContextPlansAnAdapterlessHostFlow()
    {
        var source = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 51_000);

        Assert.True(TryPlan(source, Control(UdpRedirectTestData.Context(port: 40010, ruleIndex: 0)), targets: [s_server], out var plan, out var reason, out var detail), detail);

        Assert.Equal(UdpDatagramDropReason.None, reason);
        Assert.Equal(TransportProtocol.Udp, plan.Flow.Protocol);
        Assert.Equal(FlowOriginKind.Host, plan.Flow.Origin);
        Assert.Equal(AdapterSlotTable.NoSlot, plan.Flow.OriginAdapterSlot);
        Assert.Equal(source.ToString(), plan.Flow.Local.ToString());
        Assert.Equal("192.0.2.53:40010", plan.Flow.Remote.ToString());
        Assert.Equal(s_server, plan.Target);
    }

    [Fact]
    public void AZeroLengthPayloadWithAValidContextPlansAHostFlow()
    {
        var control = Control(UdpRedirectTestData.Context(port: 40010, ruleIndex: 0));

        Assert.True(TryPlan(Client(), control, [s_server], out var plan, out var reason, out var detail, payloadLength: 0), detail);

        Assert.Equal(UdpDatagramDropReason.None, reason);
        Assert.Equal(TransportProtocol.Udp, plan.Flow.Protocol);
        Assert.Equal(s_server, plan.Target);
    }

    /// <summary>
    /// A v4-mapped connected association arrives with a mapped source and a family-4 context. The flow
    /// key needs one family on both endpoints, and the reply has to be addressed to the pure IPv4
    /// client, so the mapped source is normalized.
    /// </summary>
    [Fact]
    public void AMappedClientSourceIsNormalizedToIpv4AgainstAFamily4Context()
    {
        var source = new IPEndPoint(IPAddress.Parse("192.0.2.10").MapToIPv6(), 51_000);

        Assert.True(TryPlan(source, Control(UdpRedirectTestData.Context(port: 40010)), [s_server], out var plan, out _, out _));

        Assert.Equal(AddressFamilyKind.IPv4, plan.Flow.Local.AddressFamily);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 51_000), plan.Client);
    }

    [Fact]
    public void ANativeIpv6ClientKeepsItsFamily()
    {
        var source = new IPEndPoint(IPAddress.Parse("2001:db8::10"), 51_000);

        Assert.True(TryPlan(source, Control(UdpRedirectTestData.Context(family: 6, address: "2001:db8::53", port: 40010)), [s_server], out var plan, out _, out _));

        Assert.Equal(AddressFamilyKind.IPv6, plan.Flow.Local.AddressFamily);
        Assert.Equal(AddressFamilyKind.IPv6, plan.Flow.Remote.AddressFamily);
    }

    [Fact]
    public void ANativeIpv4ClientAgainstAFamily4ContextIsUnchanged()
    {
        var source = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 51_000);

        Assert.Equal(source, KernelHostUdpDatagram.Normalize(source, System.Net.Sockets.AddressFamily.InterNetwork));
    }

    [Fact]
    public void AFamilyMismatchIsDropped()
    {
        var source = new IPEndPoint(IPAddress.Parse("2001:db8::10"), 51_000);

        Assert.False(TryPlan(source, Control(UdpRedirectTestData.Context(family: 4, address: "192.0.2.53")), [s_server], out _, out var reason, out _));

        Assert.Equal(UdpDatagramDropReason.FamilyMismatch, reason);
    }

    [Fact]
    public void ANonUdpProtocolIsDropped()
    {
        Assert.False(TryPlan(Client(), Control(UdpRedirectTestData.Context(protocol: 6)), [s_server], out _, out var reason, out _));

        Assert.Equal(UdpDatagramDropReason.NonUdpProtocol, reason);
    }

    [Fact]
    public void ADatagramWithoutARedirectContextIsDropped()
    {
        Assert.False(TryPlan(Client(), [], [s_server], out _, out var reason, out _));

        Assert.Equal(UdpDatagramDropReason.MissingContext, reason);
    }

    [Theory]
    [InlineData(0x31434658U, (ushort)1)]
    [InlineData(0x31434657U, (ushort)9)]
    public void AContextThatDoesNotDecodeIsDropped(uint magic, ushort version)
    {
        var control = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(magic: magic, version: version), UdpRedirectTestData.LevelIp);

        Assert.False(TryPlan(Client(), control, [s_server], out _, out var reason, out _));

        Assert.Equal(UdpDatagramDropReason.InvalidContext, reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void AnOutOfRangeRuleIndexIsDropped(int ruleIndex)
    {
        var control = UdpRedirectTestData.Cmsg(
            UdpRedirectTestData.Context(ruleIndex: unchecked((uint)ruleIndex)), UdpRedirectTestData.LevelIp);

        Assert.False(TryPlan(Client(), control, [s_server], out _, out var reason, out _));

        Assert.Equal(UdpDatagramDropReason.TargetUnavailable, reason);
    }

    [Fact]
    public void TheFallbackSentinelIsDropped()
    {
        var control = UdpRedirectTestData.Cmsg(
            UdpRedirectTestData.Context(ruleIndex: 0xFFFFFFFF), UdpRedirectTestData.LevelIp);

        Assert.False(TryPlan(Client(), control, [s_server], out _, out var reason, out _));

        Assert.Equal(UdpDatagramDropReason.TargetUnavailable, reason);
    }

    [Fact]
    public void APassRuleAtIndexIsDropped()
    {
        var control = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(ruleIndex: 0), UdpRedirectTestData.LevelIp);

        Assert.False(TryPlan(Client(), control, targets: [null], out _, out var reason, out _));

        Assert.Equal(UdpDatagramDropReason.TargetUnavailable, reason);
    }

    [Fact]
    public void ALocalTargetIsDroppedByThisMilestone()
    {
        var local = new ProxyTarget("Local", Socks5: null, new LocalTarget("Local", Endpoint.From(IPAddress.Loopback, 5353)));
        var control = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(ruleIndex: 0), UdpRedirectTestData.LevelIp);

        Assert.False(TryPlan(Client(), control, [local], out _, out var reason, out var detail));

        Assert.Equal(UdpDatagramDropReason.TargetUnavailable, reason);
        Assert.Contains("local target 'Local'", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(KernelHostUdpDatagram.MaximumDatagramBytes, true)]
    [InlineData(KernelHostUdpDatagram.MaximumDatagramBytes + 1, false)]
    public void ThePayloadCapIsCheckedBeforeAdmission(int payloadLength, bool admitted)
    {
        var control = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(), UdpRedirectTestData.LevelIp);

        Assert.Equal(admitted, TryPlan(Client(), control, [s_server], out _, out var reason, out _, payloadLength: payloadLength));
        if (!admitted) Assert.Equal(UdpDatagramDropReason.Oversized, reason);
    }

    /// <summary>
    /// The decision path must never throw: a datagram is arbitrary bytes from any local process, and an
    /// escaping exception would be the only thing in this path able to end the receive leg.
    /// </summary>
    [Fact]
    public void EveryTruncatedOrGarbledControlChainIsDroppedWithoutThrowing()
    {
        var valid = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(), UdpRedirectTestData.LevelIp);
        var completeCmsg = UdpRedirectTestData.HeaderSize + UdpRedirectTestData.ContextSize;
        for (var length = 0; length < valid.Length; length++)
        {
            // A prefix shorter than one header + one context cannot carry a decodable destination; the
            // longer prefixes are the aligned tail of the same cmsg and stay valid.
            var planned = TryPlan(Client(), valid[..length], [s_server], out _, out _, out _);
            if (length < completeCmsg) Assert.False(planned);
        }

        var garbled = new byte[valid.Length];
        for (var seed = 0; seed < 64; seed++)
        {
            for (var index = 0; index < garbled.Length; index++) garbled[index] = unchecked((byte)((seed * 31) + (index * 17)));
            Assert.False(TryPlan(Client(), garbled, [s_server], out _, out _, out _));
        }
    }

    private static IPEndPoint Client() => new(IPAddress.Parse("192.0.2.10"), 51_000);

    private static byte[] Control(byte[] context) => UdpRedirectTestData.Cmsg(context, UdpRedirectTestData.LevelIp);

    private static bool TryPlan(
        IPEndPoint source,
        byte[] control,
        ProxyTarget?[] targets,
        out UdpDatagramPlan plan,
        out UdpDatagramDropReason reason,
        out string detail,
        int payloadLength = 32) =>
        KernelHostUdpDatagram.TryPlan(source, control, payloadLength, targets, out plan, out reason, out detail);
}
