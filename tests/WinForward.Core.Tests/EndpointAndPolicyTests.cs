using System.Net;
using WinForward.TestSupport;
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
        var context = FlowBuilders.Context(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            "dns.exe");
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null)),
            new(new RuleMatcher(RemotePorts: [(53, 53)]), new FlowDecision(FlowAction.Proxy, 1, "dns")),
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
            FlowBuilders.SlotOf("id-b", 1), 1);
        var context = FlowBuilders.Context(key, adapterId: "id-b", adapterName: "vEthernet B");
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(), new FlowDecision(FlowAction.Proxy, 0, "primary")),
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Proxy, 1, "primary")),
        ], FlowAction.Block);

        var decision = policy.EvaluateForwarded(context);

        Assert.Equal(FlowAction.Pass, decision.Action);
        Assert.Null(decision.RuleIndex);
        Assert.Equal(FlowAction.Proxy, policy.Evaluate(context).Action);
    }

    [Fact]
    public void ForwardedPolicyPreservesQualifiedRuleOrderAndMatcherConditions()
    {
        Assert.True(IPPrefix.TryParse("192.0.2.0/24", out var network));
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
            TransportProtocol.Udp,
            FlowOriginKind.Forwarded,
            FlowBuilders.SlotOf("id-a", 1), 1);
        var context = FlowBuilders.Context(key, adapterId: "id-a", adapterName: "vEthernet A");
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null)),
            new(new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" },
                Protocols: new HashSet<TransportProtocol> { TransportProtocol.Tcp }),
                new FlowDecision(FlowAction.Block, 1, ProxyServerName: null)),
            new(new RuleMatcher(
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet A" },
                Protocols: new HashSet<TransportProtocol> { TransportProtocol.Udp },
                AddressFamilies: new HashSet<AddressFamilyKind> { AddressFamilyKind.IPv4 },
                RemoteNetworks: [network],
                RemotePorts: [(443, 443)]),
                new FlowDecision(FlowAction.Proxy, 2, "primary")),
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Pass, 3, ProxyServerName: null)),
        ], FlowAction.Block);

        var decision = policy.EvaluateForwarded(context);

        Assert.Equal(FlowAction.Proxy, decision.Action);
        Assert.Equal(2, decision.RuleIndex);
    }

    [Fact]
    public void ProcessSelectorMatchesFilenameOrNormalizedFullPathExactly()
    {
        var filenameContext = FlowBuilders.Context(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            processPath: @"C:\Windows\System32\DNS.EXE");
        var filenamePolicy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Block, filenamePolicy.Evaluate(filenameContext).Action);

        var pathContext = FlowBuilders.Context(filenameContext.Key, "dns.exe", @"C:\Program Files\WinForward\dns.exe");
        var pathPolicy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "c:/program files/winforward/dns.exe" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Block, pathPolicy.Evaluate(pathContext).Action);
        Assert.Equal(FlowAction.Pass, new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass).Evaluate(pathContext).Action);
    }

    [Fact]
    public void ProcessSelectorDirectoryMatchesProgramsInDirectoryAndSubdirectories()
    {
        var context = FlowBuilders.Context(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            "tool.exe", @"C:\Program Files\MyApp\Bin\Sub\TOOL.EXE");
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"c:\program files\myapp" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Block, policy.Evaluate(context).Action);
    }

    [Fact]
    public void ProcessSelectorDirectoryMatchesDirectChildAndTrailingSeparatorForm()
    {
        var context = FlowBuilders.Context(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            "app.exe", @"C:\Program Files\MyApp\App.EXE");
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "c:/program files/myapp/" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Block, policy.Evaluate(context).Action);
    }

    [Fact]
    public void ProcessSelectorDirectoryDoesNotMatchSiblingPrefix()
    {
        var context = FlowBuilders.Context(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            "app.exe", @"C:\ToolsFoo\App.EXE");
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"c:\tools" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Pass, policy.Evaluate(context).Action);
    }

    [Fact]
    public void ProcessSelectorDirectoryDoesNotMatchParentOrUnrelatedPaths()
    {
        var context = FlowBuilders.Context(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            "app.exe", @"C:\Other\App.EXE");
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Tools" }), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Pass, policy.Evaluate(context).Action);
    }

    [Fact]
    public void PolicyMatchesRemoteCidr()
    {
        Assert.True(IPPrefix.TryParse("192.0.2.0/24", out var network));
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 50000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = FlowBuilders.Context(key);
        var policy = new PolicySnapshot([new(new RuleMatcher(RemoteNetworks: [network]), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))], FlowAction.Pass);

        Assert.Equal(FlowAction.Block, policy.Evaluate(context).Action);
    }

    [Fact]
    public void IPv6PrefixesNormalizeAndMatchOnlyTheirPrefix()
    {
        Assert.True(IPPrefix.TryParse("2001:db8:1::1234/64", out var prefix));

        Assert.Equal(IPAddress.Parse("2001:db8:1::"), prefix.Network);
        Assert.True(prefix.Contains(IPAddress.Parse("2001:db8:1::ffff")));
        Assert.False(prefix.Contains(IPAddress.Parse("2001:db8:2::1")));
        Assert.False(prefix.Contains(IPAddress.Parse("192.0.2.1")));
    }

    [Fact]
    public void IPv4PrefixesMatchOnlyTheirPrefix()
    {
        // Regression lock: IPv4 bits live in the low 32 bits of the raw address, so the mask
        // must be low-bits-aligned; a left-aligned mask made every IPv4 contains check pass.
        Assert.True(IPPrefix.TryParse("192.168.1.0/24", out var prefix));

        Assert.Equal(IPAddress.Parse("192.168.1.0"), prefix.Network);
        Assert.True(prefix.Contains(IPAddress.Parse("192.168.1.77")));
        Assert.False(prefix.Contains(IPAddress.Parse("8.8.8.8")));
        Assert.False(prefix.Contains(IPAddress.Parse("192.168.2.1")));
        Assert.False(prefix.Contains(IPAddress.Parse("2001:db8::1")));

        Assert.True(IPPrefix.TryParse("10.0.0.5/32", out var host));
        Assert.True(host.Contains(IPAddress.Parse("10.0.0.5")));
        Assert.False(host.Contains(IPAddress.Parse("10.0.0.6")));

        Assert.True(IPPrefix.TryParse("0.0.0.0/0", out var everything));
        Assert.True(everything.Contains(IPAddress.Parse("203.0.113.9")));
        Assert.False(everything.Contains(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void IPPrefixRejectsNullInputsWithoutThrowing()
    {
        Assert.False(IPPrefix.TryParse(null, out _));
        Assert.False(IPPrefix.TryParse("   ", out _));
        Assert.False(IPPrefix.TryParse("192.0.2.0/33", out _));
    }

    [Fact]
    public void PolicyAndAcrossFieldsRequiresEveryPopulatedField()
    {
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 80), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = FlowBuilders.Context(key, "dns.exe");
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }, RemotePorts: [(53, 53)]), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass);

        // Process matches but the remote port does not -> the AND rule must not match.
        Assert.Equal(FlowAction.Pass, policy.Evaluate(context).Action);

        // The remote port is the key's, so the matching shape is a key for the matching port.
        var matchingContext = FlowBuilders.Context(FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host), "dns.exe");
        Assert.Equal(FlowAction.Block, policy.Evaluate(matchingContext).Action);
    }

    [Fact]
    public void PolicyAlternativesWithinFieldUseOrSemantics()
    {
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = FlowBuilders.Context(key, "dns.exe");
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other.exe", "dns.exe" }, RemotePorts: [(80, 80), (443, 443)]), new FlowDecision(FlowAction.Block, 0, ProxyServerName: null))],
            FlowAction.Pass);

        // Matches the second process alternative AND the second port range.
        Assert.Equal(FlowAction.Block, policy.Evaluate(context).Action);
    }
}
