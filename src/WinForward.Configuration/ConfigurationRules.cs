using System.Globalization;
using WinForward.Core;

namespace WinForward.Configuration;

/// <summary>
/// The rule-domain half of configuration validation: parses one domain's default action and its
/// ordered rules, and carries the domain-qualified path of every diagnostic it raises.
/// </summary>
internal static class ConfigurationRules
{
    /// <summary>
    /// Parses one domain. The host domain is required and admits process selectors; the forwarded
    /// domain may be omitted entirely and its default action falls back to
    /// <see cref="FlowAction.Pass"/>, which is the posture every configuration had before the
    /// default became addressable.
    /// </summary>
    internal static FlowAction? ParseDomain(RuleDomainDto? domain, FlowOriginKind origin, Dictionary<string, ProxyTarget> targets, List<PolicyRule> parsed, List<ConfigDiagnostic> errors)
    {
        var path = DomainPath(origin);
        if (domain is null)
        {
            if (origin == FlowOriginKind.Host) errors.Add(new(path, "Field is required."));
            return null;
        }

        var fallback = domain.FallbackAction is null && origin == FlowOriginKind.Forwarded
            ? FlowAction.Pass
            : ParseAction(domain.FallbackAction, $"{path}.fallbackAction", errors, allowProxy: false);

        ValidateRules(domain.Rules, path, origin, targets, parsed, errors);
        return fallback;
    }

    /// <summary>Whether any rule logs full process paths, which is the disclosure switch the runtime reads.</summary>
    internal static bool AnyProcessSelectorIsAPath(List<PolicyRule> rules) => rules.Exists(static rule => rule.Matcher.Processes?.Any(IsPathSelector) == true);

    private static string DomainPath(FlowOriginKind origin) => origin == FlowOriginKind.Host ? "host" : "forwarded";

    private static void ValidateRules(IReadOnlyList<RuleDto?>? dtos, string domainPath, FlowOriginKind origin, Dictionary<string, ProxyTarget> targets, List<PolicyRule> parsed, List<ConfigDiagnostic> errors)
    {
        if (dtos is null) return;
        for (var index = 0; index < dtos.Count; index++)
        {
            var rule = ParseRule(dtos[index], index, string.Create(CultureInfo.InvariantCulture, $"{domainPath}.rules[{index}]"), origin, targets, errors);
            if (rule is not null) parsed.Add(rule);
        }
    }

    private static PolicyRule? ParseRule(RuleDto? dto, int index, string path, FlowOriginKind origin, Dictionary<string, ProxyTarget> targets, List<ConfigDiagnostic> errors)
    {
        if (dto is null)
        {
            errors.Add(new(path, "Rule entry must be an object."));
            return null;
        }

        ValidateNonEmpty(dto.Process, $"{path}.process", errors);

        // A forwarded flow has no host process owner, so the matcher could never fire. Rejecting it
        // here is what keeps "configured but inert" out of the forwarded domain.
        if (origin == FlowOriginKind.Forwarded && dto.Process is not null)
        {
            errors.Add(new($"{path}.process", "A forwarded flow has no host process owner, so a process selector cannot match; process rules belong in host.rules."));
        }

        ValidateNonEmpty(dto.AdapterId, $"{path}.adapterId", errors);
        ValidateNonEmpty(dto.AdapterName, $"{path}.adapterName", errors);
        ValidateNonEmpty(dto.Protocol, $"{path}.protocol", errors);
        ValidateNonEmpty(dto.AddressFamily, $"{path}.addressFamily", errors);
        ValidateNonEmpty(dto.RemoteCidr, $"{path}.remoteCidr", errors);
        ValidateNonEmpty(dto.RemotePort, $"{path}.remotePort", errors);

        var action = ParseAction(dto.Action, $"{path}.action", errors, allowProxy: true);
        if (action is null) return null;
        var target = dto.Target?.Trim();
        var resolved = action == FlowAction.Proxy && target is not null && targets.TryGetValue(target, out var candidate) ? candidate : (ProxyTarget?)null;
        if (action == FlowAction.Proxy && resolved is null)
        {
            errors.Add(new($"{path}.target", "Proxy action requires a configured target."));
        }
        if (action != FlowAction.Proxy && dto.Target is not null) errors.Add(new($"{path}.target", "Only proxy rules may specify target."));

        var protocols = ParseSet(dto.Protocol, ParseProtocol, $"{path}.protocol", errors);
        var families = ParseSet(dto.AddressFamily, ParseFamily, $"{path}.addressFamily", errors);
        var networks = ParseNetworks(dto.RemoteCidr, $"{path}.remoteCidr", errors);
        var ports = ParsePorts(dto.RemotePort, $"{path}.remotePort", errors);
        var udpOnly = protocols is { Count: 1 } && protocols.Contains(TransportProtocol.Udp);
        if (resolved is { IsLocal: true } && !udpOnly)
        {
            errors.Add(new($"{path}.target", "A local target requires a protocol selector of exactly udp; a rule without one matches every protocol, TCP included."));
        }
        if (errors.Exists(error => error.Path.Equals(path, StringComparison.Ordinal) || error.Path.StartsWith(path + ".", StringComparison.Ordinal))) return null;

        return new PolicyRule(new RuleMatcher(
            NormalizeSet(dto.Process), NormalizeSet(dto.AdapterId), NormalizeSet(dto.AdapterName), protocols, families, networks, ports),
            new FlowDecision(action.Value, index, action == FlowAction.Proxy ? target : null));
    }

    private static FlowAction? ParseAction(string? raw, string path, List<ConfigDiagnostic> errors, bool allowProxy)
    {
        if (string.IsNullOrWhiteSpace(raw)) { errors.Add(new(path, "Action is required.")); return null; }
        var action = raw.Trim().ToLowerInvariant() switch
        {
            "proxy" when allowProxy => FlowAction.Proxy,
            "pass" => FlowAction.Pass,
            "block" => FlowAction.Block,
            _ => (FlowAction?)null,
        };
        if (action is null)
        {
            errors.Add(new(path, allowProxy ? "Action must be proxy, pass, or block." : "Action must be pass or block."));
            return null;
        }
        return action.Value;
    }

    private static void ValidateNonEmpty(string?[]? values, string path, List<ConfigDiagnostic> errors)
    {
        if (values is null) return;
        if (values.Length == 0)
        {
            errors.Add(new(path, "Array must not be empty."));
            return;
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(values[index])) errors.Add(new(string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), "Value must not be empty."));
        }
    }

    private static HashSet<string>? NormalizeSet(string?[]? values) => values?.Select(static value => value!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<IPPrefix>? ParseNetworks(string?[]? values, string path, List<ConfigDiagnostic> errors)
    {
        if (values is null) return null;
        var result = new List<IPPrefix>();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!IPPrefix.TryParse(value, out var prefix)) errors.Add(new(string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), $"Invalid CIDR '{value}'.")); else result.Add(prefix);
        }
        return result;
    }

    private static HashSet<T>? ParseSet<T>(string?[]? values, Func<string, T?> parser, string path, List<ConfigDiagnostic> errors) where T : struct
    {
        if (values is null) return null;
        var result = new HashSet<T>();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (string.IsNullOrWhiteSpace(value)) continue;
            var parsed = parser(value);
            if (parsed is null) errors.Add(new(string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), $"Unsupported value '{value}'.")); else result.Add(parsed.Value);
        }
        return result;
    }

    private static List<(ushort Start, ushort End)>? ParsePorts(string?[]? values, string path, List<ConfigDiagnostic> errors)
    {
        if (values is null) return null;
        var result = new List<(ushort Start, ushort End)>();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (string.IsNullOrWhiteSpace(value)) continue;
            var parts = value.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 || !ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start == 0)
            {
                errors.Add(new(string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), $"Invalid port or range '{value}'."));
                continue;
            }

            var end = start;
            if (parts.Length == 2 && (!ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end == 0 || end < start))
            {
                errors.Add(new(string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), $"Invalid port or range '{value}'."));
                continue;
            }

            result.Add((start, end));
        }

        result.Sort(static (left, right) => left.Start != right.Start
            ? left.Start.CompareTo(right.Start)
            : left.End.CompareTo(right.End));
        var merged = new List<(ushort Start, ushort End)>(result.Count);
        foreach (var range in result)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            var (start, end) = merged[^1];
            if (range.Start > end + 1U)
            {
                merged.Add(range);
                continue;
            }

            if (range.End > end) merged[^1] = (start, range.End);
        }

        return merged;
    }

    private static TransportProtocol? ParseProtocol(string value)
    {
        if (value.Equals("tcp", StringComparison.OrdinalIgnoreCase)) return TransportProtocol.Tcp;
        if (value.Equals("udp", StringComparison.OrdinalIgnoreCase)) return TransportProtocol.Udp;
        return null;
    }

    private static AddressFamilyKind? ParseFamily(string value)
    {
        if (value.Equals("IPv4", StringComparison.OrdinalIgnoreCase) || value.Equals("InterNetwork", StringComparison.OrdinalIgnoreCase)) return AddressFamilyKind.IPv4;
        if (value.Equals("IPv6", StringComparison.OrdinalIgnoreCase) || value.Equals("InterNetworkV6", StringComparison.OrdinalIgnoreCase)) return AddressFamilyKind.IPv6;
        return null;
    }

    private static bool IsPathSelector(string selector) => selector.IndexOfAny(['/', '\\']) >= 0;
}
