using System.Net;
using WinForward.Core;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class EndpointAndPolicyTests
{
    [Fact]
    public void EndpointEqualityUsesAddressValueRatherThanIpAddressObjectIdentity()
    {
        var first = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var second = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void EndpointFactoryRejectsNullAddressWithArgumentException()
    {
        Assert.Throws<ArgumentNullException>(() => Endpoint.From(null!, 53));
    }

    [Fact]
    public void PolicyUsesFirstMatchingRule()
    {
        var context = new FlowContext(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            "dns.exe", null, null, null, 53);
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), new FlowDecision(FlowAction.Block, 0, null)),
            new(new RuleMatcher(RemotePorts: [(53, 53)]), new FlowDecision(FlowAction.Proxy, 1, "dns"))
        ], FlowAction.Pass);

        var decision = policy.Evaluate(context);

        Assert.Equal(FlowAction.Block, decision.Action);
        Assert.Equal(0, decision.RuleIndex);
    }

    [Fact]
    public void ForwardedPolicySkipsUnqualifiedRulesAndConfiguredFallback()
    {
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
            TransportProtocol.Tcp,
            FlowOriginKind.Forwarded,
            new AdapterContext("id-b", "vEthernet B", 1));
        var context = new FlowContext(key, null, null, "id-b", "vEthernet B", 443);
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(), new FlowDecision(FlowAction.Proxy, 0, "primary")),
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Proxy, 1, "primary"))
        ], FlowAction.Block);

        var decision = policy.EvaluateForwarded(context);

        Assert.Equal(FlowAction.Pass, decision.Action);
        Assert.Null(decision.RuleIndex);
        Assert.Equal(FlowAction.Proxy, policy.Evaluate(context).Action);
    }

    [Fact]
    public void ForwardedPolicyPreservesQualifiedRuleOrderAndMatcherConditions()
    {
        Assert.True(IpPrefix.TryParse("192.0.2.0/24", out var network));
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
            TransportProtocol.Udp,
            FlowOriginKind.Forwarded,
            new AdapterContext("id-a", "vEthernet A", 1));
        var context = new FlowContext(key, null, null, "id-a", "vEthernet A", 443);
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(), new FlowDecision(FlowAction.Block, 0, null)),
            new(new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" },
                Protocols: new HashSet<TransportProtocol> { TransportProtocol.Tcp }),
                new FlowDecision(FlowAction.Block, 1, null)),
            new(new RuleMatcher(
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet A" },
                Protocols: new HashSet<TransportProtocol> { TransportProtocol.Udp },
                AddressFamilies: new HashSet<AddressFamilyKind> { AddressFamilyKind.IPv4 },
                RemoteNetworks: [network],
                RemotePorts: [(443, 443)]),
                new FlowDecision(FlowAction.Proxy, 2, "primary")),
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Pass, 3, null))
        ], FlowAction.Block);

        var decision = policy.EvaluateForwarded(context);

        Assert.Equal(FlowAction.Proxy, decision.Action);
        Assert.Equal(2, decision.RuleIndex);
    }

    [Fact]
    public void ProcessSelectorMatchesFilenameOrNormalizedFullPathExactly()
    {
        var filenameContext = new FlowContext(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            null, @"C:\Windows\System32\DNS.EXE", null, null, 53);
        var filenamePolicy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Block, filenamePolicy.Evaluate(filenameContext).Action);

        var pathContext = filenameContext with { ProcessName = "dns.exe", ProcessPath = @"C:\Program Files\WinForward\dns.exe" };
        var pathPolicy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "c:/program files/winforward/dns.exe" }), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Block, pathPolicy.Evaluate(pathContext).Action);
        Assert.Equal(FlowAction.Pass, new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns" }), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass).Evaluate(pathContext).Action);
    }

    [Fact]
    public void PolicyMatchesRemoteCidr()
    {
        Assert.True(IpPrefix.TryParse("192.0.2.0/24", out var network));
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 50000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = new FlowContext(key, null, null, null, null, 53);
        var policy = new PolicySnapshot([new(new RuleMatcher(RemoteNetworks: [network]), new FlowDecision(FlowAction.Block, 0, null))], FlowAction.Pass);

        Assert.Equal(FlowAction.Block, policy.Evaluate(context).Action);
    }

    [Fact]
    public void IPv6PrefixesNormalizeAndMatchOnlyTheirPrefix()
    {
        Assert.True(IpPrefix.TryParse("2001:db8:1::1234/64", out var prefix));

        Assert.Equal(IPAddress.Parse("2001:db8:1::"), prefix.Network);
        Assert.True(prefix.Contains(IPAddress.Parse("2001:db8:1::ffff")));
        Assert.False(prefix.Contains(IPAddress.Parse("2001:db8:2::1")));
        Assert.False(prefix.Contains(IPAddress.Parse("192.0.2.1")));
    }

    [Fact]
    public void IpPrefixRejectsNullInputsWithoutThrowing()
    {
        Assert.False(IpPrefix.TryParse(null, out var prefix));
        Assert.False(prefix.Contains(null));
    }

    [Fact]
    public void PolicyAndAcrossFieldsRequiresEveryPopulatedField()
    {
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 80), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = new FlowContext(key, "dns.exe", null, null, null, 80);
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }, RemotePorts: [(53, 53)]), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass);

        // Process matches but the remote port does not -> the AND rule must not match.
        Assert.Equal(FlowAction.Pass, policy.Evaluate(context).Action);

        var matchingContext = context with { RemotePort = 53 };
        Assert.Equal(FlowAction.Block, policy.Evaluate(matchingContext).Action);
    }

    [Fact]
    public void PolicyAlternativesWithinFieldUseOrSemantics()
    {
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = new FlowContext(key, "dns.exe", null, null, null, 443);
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other.exe", "dns.exe" }, RemotePorts: [(80, 80), (443, 443)]), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass);

        // Matches the second process alternative AND the second port range.
        Assert.Equal(FlowAction.Block, policy.Evaluate(context).Action);
    }
}
