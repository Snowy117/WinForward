using System.Net;
using WinForward.Core;
using WinForward.Runtime.Capture;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class AdapterScopeAndFlowTableTests
{
    // ---- Adapter scope resolution ----

    [Fact]
    public void AdapterScopeResolvesConstrainedAndUnconstrainedRules()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Pass, 0, null)),
            new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser.exe" }), new FlowDecision(FlowAction.Block, 1, null))
        ], FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var scope, out _));
        // Unconstrained process rule widens scope to every adapter.
        Assert.Equal(2, scope.Count);
    }

    [Fact]
    public void AdapterScopeFailsOnMissingAndAmbiguousSelectors()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-a2", "Ethernet", "a2", 3, 1),
            new WindowsAdapter("id-b", "vEthernet (Shared)", "b", 2, 1)
        };
        var missing = new PolicySnapshot([new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nope" }), new FlowDecision(FlowAction.Pass, 0, null))], FlowAction.Pass);

        Assert.False(CaptureAdapterScopeResolver.TryResolve(adapters, missing, out _, out var missingErrors));
        Assert.Contains(missingErrors, error => error.Contains("nope", StringComparison.Ordinal));

        var ambiguous = new PolicySnapshot(
            [new(new RuleMatcher(AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ethernet" }), new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);
        Assert.False(CaptureAdapterScopeResolver.TryResolve(adapters, ambiguous, out _, out var ambiguousErrors));
        Assert.Contains(ambiguousErrors, error => error.Contains("ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterScopeFailsWhenAdapterIdAndAdapterNameResolveToDifferentAdapters()
    {
        // Design §3: an ID/name selector must resolve to the same current adapter. A rule whose
        // adapterId and adapterName point at different adapters can never match (AND semantics) and
        // is a startup error, not a silent fallback.
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" },
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet 1" }),
                new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        Assert.False(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out _, out var errors));
        Assert.Contains(errors, error => error.Contains("different adapters", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterScopeAcceptsAdapterIdAndAdapterNameResolvingToTheSameAdapter()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-b" },
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet 1" }),
                new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var scope, out _));
        var single = Assert.Single(scope);
        Assert.Equal("id-b", single.StableId);
    }

    [Fact]
    public void AdapterScopeIncludesEveryAdapterForFallbackOnlyPolicy()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot([], FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var scope, out _));
        Assert.Equal(2, scope.Count);
    }

    [Fact]
    public void AdapterScopeConstrainedOnlyCapturesSelectedAdapters()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet 1" }), new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var scope, out _));
        var single = Assert.Single(scope);
        Assert.Equal("id-b", single.StableId);
    }

    // ---- Flow table cross-origin / cross-adapter reuse ----

    [Fact]
    public void FlowTableResolvesReversePacketAcrossOriginKindAndReusesDecision()
    {
        var table = new FlowTable();
        var hostKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var decisions = 0;

        Assert.True(table.TryClaimResolved(hostKey, () => { decisions++; return new FlowDecision(FlowAction.Pass, 0, null); }, out var claimed));
        Assert.NotNull(claimed);
        Assert.Equal(1, decisions);

        // Response packet is ON_RECEIVE -> Forwarded origin, reverse endpoints.
        var responseKey = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), TransportProtocol.Tcp, FlowOriginKind.Forwarded);

        Assert.True(table.TryResolve(responseKey, out var resolved));
        Assert.Same(claimed, resolved);
        Assert.Equal(FlowAction.Pass, resolved!.Decision.Action);

        // Re-resolving the response must not re-evaluate policy.
        Assert.True(table.TryResolve(responseKey, out _));
        Assert.Equal(1, decisions);
    }

    [Fact]
    public void FlowTableReusesDecisionAcrossAdapterBoundaries()
    {
        var table = new FlowTable();
        var onAdapterA = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host, new AdapterContext("a", "Ethernet", 1));
        var onAdapterB = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), TransportProtocol.Tcp, FlowOriginKind.Forwarded, new AdapterContext("b", "vEthernet", 2));

        Assert.True(table.TryClaimResolved(onAdapterA, () => new FlowDecision(FlowAction.Block, 0, null), out var claimed));
        Assert.True(table.TryResolve(onAdapterB, out var resolved));
        Assert.Same(claimed, resolved);
    }
}
