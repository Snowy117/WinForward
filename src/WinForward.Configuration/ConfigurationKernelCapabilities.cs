using System.Globalization;
using System.Text;
using WinForward.Core;

namespace WinForward.Configuration;

/// <summary>
/// The WinForward.Kernel provider's milestone-1 capability gate: it refuses the configuration shapes
/// the Kernel driver cannot execute and reports each refusal at the WinForward path of the offending
/// rule, so <c>validate</c> and <c>run</c> share one decision and one diagnostic vocabulary.
/// <para>
/// Process-path resolution is deliberately out of scope. The Kernel host-rule compiler resolves a path
/// selector to an existing <c>\device\...</c> path and owns that existence check; this gate measures the
/// configured selector against the blob's 511-byte limit, and the compiler re-checks the limit on the
/// resolved string, which is longer in general. Normalization, existence and the rule-index mapping
/// stay the compiler's.
/// </para>
/// </summary>
internal static class ConfigurationKernelCapabilities
{
    /// <summary>The host rule blob's process-selector limit: 1..511 UTF-8 bytes, NUL excluded.</summary>
    private const int MaximumProcessSelectorBytes = 511;

    private const string ForwardedPath = "WinForward.Forwarded";

    /// <summary>
    /// Validates a parsed configuration under the Kernel provider. Callers run it only against a fully
    /// parsed document: every diagnostic names its rule by configuration index, which stays the parsed
    /// index only when no rule was rejected by the ordinary rule validation.
    /// </summary>
    internal static void Validate(WinForwardConfigDto dto, IReadOnlyList<PolicyRule> hostRules, IReadOnlyList<PolicyRule> forwardedRules, FlowAction forwardedFallback, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        ValidateHostRules(hostRules, errors);
        ValidateForwardedDomain(forwardedRules, forwardedFallback, errors);
        WarnOnInertUdpSettings(dto, warnings);
    }

    /// <summary>
    /// Refuses the host selectors and actions the Kernel host layer cannot execute. An adapter
    /// selector is refused because ALE connect-redirect classifies without an interface index; a UDP
    /// proxy rule is refused because the host UDP proxy is not available yet — the listener reply
    /// cannot preserve the original destination as its reply source, so an unconnected client would
    /// observe the proxy listener until the driver-side reply injection lands — while pass and block
    /// rules stay valid for every protocol because the driver executes those in-kernel.
    /// </summary>
    private static void ValidateHostRules(IReadOnlyList<PolicyRule> hostRules, List<ConfigDiagnostic> errors)
    {
        for (var index = 0; index < hostRules.Count; index++)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"WinForward.Host.Rules[{index}]");
            var rule = hostRules[index];
            if (rule.Matcher.AdapterIds is not null) errors.Add(new($"{path}.AdapterId", $"The {BackendDriverConfiguration.KernelProviderName} provider cannot select host traffic by adapter: the ALE connect-redirect layer has no interface index. Remove the selector or use Provider = {BackendDriverConfiguration.WinpkFilterProviderName}."));
            if (rule.Matcher.AdapterNames is not null) errors.Add(new($"{path}.AdapterName", $"The {BackendDriverConfiguration.KernelProviderName} provider cannot select host traffic by adapter: the ALE connect-redirect layer has no interface index. Remove the selector or use Provider = {BackendDriverConfiguration.WinpkFilterProviderName}."));
            if (rule.Decision.Action == FlowAction.Proxy && (rule.Matcher.Protocols is null || rule.Matcher.Protocols.Contains(TransportProtocol.Udp)))
            {
                errors.Add(new($"{path}.Protocol", $"The {BackendDriverConfiguration.KernelProviderName} host UDP proxy is not available yet: the listener reply cannot preserve the original destination as its source, so an unconnected client would see the proxy listener, and the driver-side reply injection is pending. Add \"Protocol\": [\"tcp\"] or use Provider = {BackendDriverConfiguration.WinpkFilterProviderName} for UDP. Pass and block rules may match any protocol."));
            }

            ValidateProcessSelectors(rule.Matcher.Processes, path, errors);
        }
    }

    private static void ValidateProcessSelectors(IReadOnlySet<string>? processes, string path, List<ConfigDiagnostic> errors)
    {
        if (processes is null) return;
        foreach (var selector in processes)
        {
            var bytes = Encoding.UTF8.GetByteCount(selector);
            if (bytes <= MaximumProcessSelectorBytes) continue;
            errors.Add(new($"{path}.Process", string.Create(CultureInfo.InvariantCulture, $"A process selector must fit the Kernel host rule blob's {MaximumProcessSelectorBytes} UTF-8 bytes; '{selector}' is {bytes} bytes. The Kernel compiler also requires a path selector to resolve to an existing device path.")));
        }
    }

    /// <summary>
    /// Refuses the forwarded domain wholesale. The Kernel provider serves host traffic only in this
    /// milestone, and a forwarded rule or a non-default forwarded fallback would otherwise validate and
    /// then silently not apply.
    /// </summary>
    private static void ValidateForwardedDomain(IReadOnlyList<PolicyRule> forwardedRules, FlowAction forwardedFallback, List<ConfigDiagnostic> errors)
    {
        if (forwardedRules.Count > 0)
        {
            errors.Add(new(ForwardedPath, string.Create(CultureInfo.InvariantCulture, $"The {BackendDriverConfiguration.KernelProviderName} provider does not serve the forwarded domain in this milestone, and {forwardedRules.Count} forwarded rule(s) are configured; remove them or use Provider = {BackendDriverConfiguration.WinpkFilterProviderName}.")));
        }

        if (forwardedFallback != FlowAction.Pass)
        {
            errors.Add(new(ForwardedPath, $"The {BackendDriverConfiguration.KernelProviderName} provider does not serve the forwarded domain in this milestone, so Forwarded.FallbackAction must stay pass; remove the non-default fallback or use Provider = {BackendDriverConfiguration.WinpkFilterProviderName}."));
        }
    }

    /// <summary>
    /// The UDP budgets are accepted rather than refused, because a configuration written for
    /// WinpkFilter must stay loadable while it is being migrated to the Kernel provider; the warning is
    /// what keeps an operator from believing they apply. They cannot apply yet because the host UDP
    /// coordinator is only constructed, not started, while the host UDP proxy is disabled.
    /// </summary>
    private static void WarnOnInertUdpSettings(WinForwardConfigDto dto, List<ConfigDiagnostic> warnings)
    {
        if (dto.UdpSessionCapacity is { } sessionCapacity && sessionCapacity != ConfigurationLoader.DefaultUdpSessionCapacity)
        {
            warnings.Add(new("WinForward.UdpSessionCapacity", $"UdpSessionCapacity is inert under Provider = {BackendDriverConfiguration.KernelProviderName} because the host UDP coordinator is not started; it takes effect under Provider = {BackendDriverConfiguration.WinpkFilterProviderName}."));
        }

        if (dto.UdpRelayReceiveBufferKb is { } relayReceiveBufferKb && relayReceiveBufferKb != ConfigurationLoader.DefaultUdpRelayReceiveBufferKb)
        {
            warnings.Add(new("WinForward.UdpRelayReceiveBufferKb", $"UdpRelayReceiveBufferKb is inert under Provider = {BackendDriverConfiguration.KernelProviderName} because the host UDP coordinator is not started; it takes effect under Provider = {BackendDriverConfiguration.WinpkFilterProviderName}."));
        }

        if (dto.UdpSessionIdleSeconds is { } idleSeconds && idleSeconds != (int)ConfigurationLoader.DefaultUdpSessionIdleTimeout.TotalSeconds)
        {
            warnings.Add(new("WinForward.UdpSessionIdleSeconds", $"UdpSessionIdleSeconds is inert under Provider = {BackendDriverConfiguration.KernelProviderName} because the host UDP coordinator is not started; it takes effect under Provider = {BackendDriverConfiguration.WinpkFilterProviderName}."));
        }
    }
}
