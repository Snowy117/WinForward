namespace WinForward.Core;

public sealed record RuleMatcher(
    IReadOnlySet<string>? Processes = null,
    IReadOnlySet<string>? AdapterIds = null,
    IReadOnlySet<string>? AdapterNames = null,
    IReadOnlySet<TransportProtocol>? Protocols = null,
    IReadOnlySet<AddressFamilyKind>? AddressFamilies = null,
    IReadOnlyList<IpPrefix>? RemoteNetworks = null,
    IReadOnlyList<(ushort Start, ushort End)>? RemotePorts = null)
{
    public bool IsAdapterQualified => AdapterIds is not null || AdapterNames is not null;

    public bool IsMatch(FlowContext context)
    {
        if (Processes is not null && !ProcessSelectorMatcher.IsMatch(Processes, context.ProcessName, context.ProcessPath)) return false;

        if (AdapterIds is not null && !AdapterIds.Contains(context.AdapterId ?? string.Empty)) return false;
        if (AdapterNames is not null && !AdapterNames.Contains(context.AdapterName ?? string.Empty)) return false;
        if (Protocols is not null && !Protocols.Contains(context.Key.Protocol)) return false;
        if (AddressFamilies is not null && !AddressFamilies.Contains(context.Key.AddressFamily)) return false;
        if (RemoteNetworks is not null && !RemoteNetworks.Any(network => network.Contains(context.Key.Remote.Address))) return false;
        if (RemotePorts is not null && !RemotePorts.Any(range => context.RemotePort >= range.Start && context.RemotePort <= range.End)) return false;
        return true;
    }
}

public sealed record PolicyRule(RuleMatcher Matcher, FlowDecision Decision);

public sealed class PolicySnapshot
{
    public PolicySnapshot(IReadOnlyList<PolicyRule> rules, FlowAction fallbackAction)
    {
        Rules = rules;
        FallbackAction = fallbackAction;
    }

    public IReadOnlyList<PolicyRule> Rules { get; }
    public FlowAction FallbackAction { get; }

    public FlowDecision Evaluate(FlowContext context)
    {
        for (var index = 0; index < Rules.Count; index++)
        {
            if (Rules[index].Matcher.IsMatch(context)) return Rules[index].Decision with { RuleIndex = index };
        }

        return FlowDecision.Fallback(FallbackAction);
    }

    public FlowDecision EvaluateForwarded(FlowContext context)
    {
        for (var index = 0; index < Rules.Count; index++)
        {
            var rule = Rules[index];
            if (rule.Matcher.IsAdapterQualified && rule.Matcher.IsMatch(context)) return rule.Decision with { RuleIndex = index };
        }

        return FlowDecision.Fallback(FlowAction.Pass);
    }
}
