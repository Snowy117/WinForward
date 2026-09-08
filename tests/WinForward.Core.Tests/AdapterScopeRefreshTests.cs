using WinForward.Core;
using WinForward.Runtime.Capture;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class AdapterScopeRefreshTests
{
    [Fact]
    public void RefreshDropsDisappearedAdapterAndContinuesOnRemainingAdapters()
    {
        // Generation 0 saw id-a and id-b; the fresh enumeration lacks id-b. Rule 1's selector
        // matches nothing, so that rule contributes nothing with a warning while rule 0 keeps
        // id-a in scope.
        var freshEnumeration = new[] { new WindowsAdapter("id-a", "Ethernet", "a", 1, 1) };
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Pass, 0, null)),
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-b" }), new FlowDecision(FlowAction.Pass, 1, null))
        ], FlowAction.Pass);

        var scope = CaptureAdapterScopeResolver.ResolveForRefresh(freshEnumeration, policy, out var warnings);

        var single = Assert.Single(scope);
        Assert.Equal("id-a", single.StableId);
        Assert.Contains(warnings, warning => warning.Contains("id-b", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshRuleWithDisappearedSelectorContributesNothing()
    {
        // A selector that matches nothing invalidates the whole rule's contribution: the surviving
        // sibling selector does not keep the rule's remaining target in scope.
        var freshEnumeration = new[] { new WindowsAdapter("id-a", "Ethernet", "a", 1, 1) };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a", "id-b" }), new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        var scope = CaptureAdapterScopeResolver.ResolveForRefresh(freshEnumeration, policy, out var warnings);

        Assert.Empty(scope);
        Assert.Contains(warnings, warning => warning.Contains("id-b", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshAdoptsNewAdapterWhenPolicyHasUnconstrainedRule()
    {
        var freshEnumeration = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-new", "Wi-Fi", "n", 2, 1)
        };
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Pass, 0, null)),
            new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser.exe" }), new FlowDecision(FlowAction.Block, 1, null))
        ], FlowAction.Pass);

        var scope = CaptureAdapterScopeResolver.ResolveForRefresh(freshEnumeration, policy, out var warnings);

        Assert.Equal(["id-a", "id-new"], scope.Select(adapter => adapter.StableId).ToArray());
        Assert.Empty(warnings);
    }

    [Fact]
    public void RefreshDoesNotAdoptNewAdapterForFullyConstrainedPolicy()
    {
        var freshEnumeration = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-new", "Wi-Fi", "n", 2, 1)
        };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        var scope = CaptureAdapterScopeResolver.ResolveForRefresh(freshEnumeration, policy, out var warnings);

        var single = Assert.Single(scope);
        Assert.Equal("id-a", single.StableId);
        Assert.Empty(warnings);
    }

    [Fact]
    public void RefreshAmbiguousNameSelectorWarnsAndSkipsAdditionNonFatally()
    {
        var freshEnumeration = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-a2", "Ethernet", "a2", 3, 1),
            new WindowsAdapter("id-b", "vEthernet (Shared)", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ethernet" }), new FlowDecision(FlowAction.Pass, 0, null))],
            FlowAction.Pass);

        var scope = CaptureAdapterScopeResolver.ResolveForRefresh(freshEnumeration, policy, out var warnings);

        Assert.Empty(scope);
        Assert.Contains(warnings, warning => warning.Contains("ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshAmbiguousSelectorStillWidensForUnconstrainedRules()
    {
        var freshEnumeration = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-a2", "Ethernet", "a2", 3, 1),
            new WindowsAdapter("id-b", "vEthernet (Shared)", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ethernet" }), new FlowDecision(FlowAction.Pass, 0, null)),
            new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser.exe" }), new FlowDecision(FlowAction.Block, 1, null))
        ], FlowAction.Pass);

        var scope = CaptureAdapterScopeResolver.ResolveForRefresh(freshEnumeration, policy, out var warnings);

        Assert.Equal(3, scope.Count);
        Assert.Contains(warnings, warning => warning.Contains("ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshSkipsRuleWhenAdapterIdAndNameDisagree()
    {
        var freshEnumeration = new[]
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

        var scope = CaptureAdapterScopeResolver.ResolveForRefresh(freshEnumeration, policy, out var warnings);

        Assert.Empty(scope);
        Assert.Contains(warnings, warning => warning.Contains("different adapters", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshOrdersScopeByStableIdCaseInsensitively()
    {
        var freshEnumeration = new[]
        {
            new WindowsAdapter("id-c", "C", "c", 3, 1),
            new WindowsAdapter("ID-B", "B", "b", 2, 1),
            new WindowsAdapter("id-a", "A", "a", 1, 1)
        };
        var policy = new PolicySnapshot([], FlowAction.Pass);

        var scope = CaptureAdapterScopeResolver.ResolveForRefresh(freshEnumeration, policy, out _);

        Assert.Equal(["id-a", "ID-B", "id-c"], scope.Select(adapter => adapter.StableId).ToArray());
    }

    [Fact]
    public void RefreshWithoutWarningsMatchesStartupScopeForIdenticalInputs()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "Ethernet", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-b" },
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet 1" }),
                new FlowDecision(FlowAction.Pass, 0, null)),
            new(new RuleMatcher(AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ethernet" }), new FlowDecision(FlowAction.Pass, 1, null)),
            new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser.exe" }), new FlowDecision(FlowAction.Block, 2, null))
        ], FlowAction.Pass);

        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, policy, out var startupScope, out var errors));
        Assert.Empty(errors);
        var refreshScope = CaptureAdapterScopeResolver.ResolveForRefresh(adapters, policy, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(startupScope, refreshScope);

        var fallbackOnly = new PolicySnapshot([], FlowAction.Pass);
        Assert.True(CaptureAdapterScopeResolver.TryResolve(adapters, fallbackOnly, out var startupAll, out _));
        Assert.Equal(startupAll, CaptureAdapterScopeResolver.ResolveForRefresh(adapters, fallbackOnly, out var fallbackWarnings));
        Assert.Empty(fallbackWarnings);
    }
}
