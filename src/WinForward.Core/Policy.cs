namespace WinForward.Core;

public sealed record RuleMatcher(
    IReadOnlySet<string>? Processes = null,
    IReadOnlySet<string>? AdapterIds = null,
    IReadOnlySet<string>? AdapterNames = null,
    IReadOnlySet<TransportProtocol>? Protocols = null,
    IReadOnlySet<AddressFamilyKind>? AddressFamilies = null,
    IReadOnlyList<IPPrefix>? RemoteNetworks = null,
    IReadOnlyList<(ushort Start, ushort End)>? RemotePorts = null)
{
    public bool IsMatch(FlowContext context)
    {
        if (Processes is not null && !ProcessSelectorMatcher.IsMatch(Processes, context.ProcessName, context.ProcessPath)) return false;

        if (AdapterIds is not null && !AdapterIds.Contains(context.AdapterId ?? string.Empty)) return false;
        if (AdapterNames is not null && !AdapterNames.Contains(context.AdapterName ?? string.Empty)) return false;
        if (Protocols is not null && !Protocols.Contains(context.Key.Protocol)) return false;
        if (AddressFamilies is not null && !AddressFamilies.Contains(context.Key.AddressFamily)) return false;
        if (RemoteNetworks is not null && !RemoteNetworks.Any(network => network.Contains(context.Key.Remote))) return false;
        return RemotePorts is null || RemotePorts.Any(range => context.RemotePort >= range.Start && context.RemotePort <= range.End);
    }
}

public sealed record PolicyRule(RuleMatcher Matcher, FlowDecision Decision);

/// <summary>
/// The immutable policy for one run: two ordered rule lists, one per flow origin. A rule's domain is
/// the list it lives in, so <c>adapterId</c>/<c>adapterName</c> narrow a rule inside its own domain
/// rather than deciding which domain it belongs to.
/// </summary>
public sealed class PolicySnapshot
{
    public PolicySnapshot(IReadOnlyList<PolicyRule> hostRules, FlowAction hostFallbackAction)
    {
        ArgumentNullException.ThrowIfNull(hostRules);
        HostRules = hostRules;
        HostFallbackAction = hostFallbackAction;
        // Host rules only: a forwarded flow has no host process owner, so a process selector in the
        // forwarded list cannot match (configuration validation refuses one there), and scanning it
        // would only open the attribution path for a rule that can never fire.
        RequiresProcessAttribution = hostRules.Any(static rule => rule.Matcher.Processes is { Count: > 0 });
    }

    public IReadOnlyList<PolicyRule> HostRules { get; }

    public IReadOnlyList<PolicyRule> ForwardedRules { get; init; } = [];

    private FlowAction HostFallbackAction { get; }

    public FlowAction ForwardedFallbackAction { get; init; } = FlowAction.Pass;

    public bool RequiresProcessAttribution { get; }

    public FlowDecision EvaluateHost(FlowContext context)
    {
        for (var index = 0; index < HostRules.Count; index++)
        {
            if (HostRules[index].Matcher.IsMatch(context)) return HostRules[index].Decision with { RuleIndex = index };
        }

        return FlowDecision.Fallback(HostFallbackAction);
    }

    /// <summary>
    /// An adapter selector narrows a rule here; it does not decide eligibility. Rule indices are
    /// relative to this domain, so a trace's <c>rule</c> field is readable only next to the same
    /// event's <c>origin</c> field.
    /// </summary>
    public FlowDecision EvaluateForwarded(FlowContext context)
    {
        for (var index = 0; index < ForwardedRules.Count; index++)
        {
            var rule = ForwardedRules[index];
            if (rule.Matcher.IsMatch(context)) return rule.Decision with { RuleIndex = index };
        }

        return FlowDecision.Fallback(ForwardedFallbackAction);
    }
}
