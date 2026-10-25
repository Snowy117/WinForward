using System.Buffers.Binary;
using System.Globalization;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Userland.Policy;
using HostPolicyRule = WinForward.Core.PolicyRule;
using KernelPolicyRule = WinForward.Userland.Policy.PolicyRule;
using KernelPolicyTable = WinForward.Userland.Policy.PolicyTable;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The Kernel host policy as the backend consumes it: the v2 host rule blob plus the target table a
/// redirect context's <c>rule_index</c> resolves through.
/// </summary>
internal sealed record KernelPolicyCompilation(byte[] HostBlob, ProxyTarget?[] TargetsByRuleIndex, int RuleCount);

/// <summary>
/// Compiles a validated WinForward host policy into the Kernel host rule blob and builds the
/// rule-index target table beside it.
/// </summary>
/// <remarks>
/// <para>
/// The rule-index contract is load-bearing: configured host rule <c>i</c> is blob rule <c>i</c>, and
/// <see cref="KernelPolicyCompilation.TargetsByRuleIndex"/>[i] is the target that rule redirects to
/// (null for pass and block). The Kernel driver reports the matched rule's index in the redirect
/// context, so the two orders must not drift.
/// </para>
/// <para>
/// Path selectors are not resolved here: <see cref="RuleBlobCompiler"/> owns the normalization,
/// existence check and 511-byte limit, and is given the caller's <see cref="IProcessPathNormalizer"/>.
/// This converter only maps WinForward's model onto the Kernel compiler's model and re-expresses the
/// compiler's failures as WinForward configuration diagnostics.
/// </para>
/// </remarks>
internal static class KernelPolicyCompiler
{
    /// <summary><c>rule_blob_header.rule_count</c> in <c>src/common/rule_blob.h</c>: a u32 at offset 12.</summary>
    private const int HeaderRuleCountOffset = 12;

    /// <summary>
    /// Why an adapter selector fails compilation. The host blob has no selector field that could
    /// carry one, and the ALE connect-redirect layer that reports the matching rule has no interface
    /// index to compare against, so a rule carrying either selector cannot be honoured.
    /// </summary>
    private const string AdapterSelectorRefusal =
        $"The {BackendDriverConfiguration.KernelProviderName} provider cannot select host traffic by adapter: the ALE connect-redirect layer has no interface index, and the host rule blob has no selector to carry it.";

    /// <summary>The prefix <see cref="RuleBlobCompiler"/> puts in front of a per-rule failure message.</summary>
    private const string RuleNumberPrefix = "规则 #";

    /// <summary>Terminates the rule number in a compiler message (<c>规则 #N：…</c>).</summary>
    private const char RuleNumberTerminator = '：';

    private const string HostPath = "WinForward.Host";

    internal static bool TryCompile(
        ValidatedConfiguration configuration,
        IProcessPathNormalizer processPathNormalizer,
        out KernelPolicyCompilation? compilation,
        out IReadOnlyList<ConfigDiagnostic> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(processPathNormalizer);

        var hostRules = configuration.Policy.HostRules;
        var rules = new List<KernelPolicyRule>(hostRules.Count);
        var targetsByRuleIndex = new ProxyTarget?[hostRules.Count];
        List<ConfigDiagnostic> diagnostics = [];
        for (var index = 0; index < hostRules.Count; index++)
        {
            var mapped = MapRule(
                hostRules[index], RulePath(index), configuration.Targets, targetsByRuleIndex, index, diagnostics);
            if (mapped is not null) rules.Add(mapped);
        }

        var fallback = MapFallback(configuration.Policy.HostFallbackAction, diagnostics);
        if (diagnostics.Count > 0) return Fail(diagnostics, out compilation, out errors);

        // The recorder tells a normalizer failure apart from any other compiler rejection, so the
        // diagnostic can name the process selector instead of only the rule.
        RecordingProcessPathNormalizer recorder = new(processPathNormalizer);
        byte[] blob;
        try
        {
            blob = RuleBlobCompiler.Compile(new KernelPolicyTable(rules, fallback), recorder);
        }
        catch (PolicyCompileException exception)
        {
            diagnostics.Add(DiagnoseCompilerFailure(exception.Message, recorder.FailedSelector, hostRules));
            return Fail(diagnostics, out compilation, out errors);
        }
        catch (Exception exception) when (recorder.FailedSelector is not null)
        {
            diagnostics.Add(DiagnoseNormalizerFailure(exception, recorder.FailedSelector, hostRules));
            return Fail(diagnostics, out compilation, out errors);
        }

        var ruleCount = ReadRuleCount(blob);
        if (ruleCount != hostRules.Count)
        {
            diagnostics.Add(new(HostPath, string.Create(
                CultureInfo.InvariantCulture,
                $"The Kernel host rule blob holds {ruleCount} rule(s) but {hostRules.Count} host rule(s) are configured; the rule-index target table would address the wrong rules.")));
            return Fail(diagnostics, out compilation, out errors);
        }

        compilation = new KernelPolicyCompilation(blob, targetsByRuleIndex, ruleCount);
        errors = [];
        return true;
    }

    private static bool Fail(
        List<ConfigDiagnostic> diagnostics,
        out KernelPolicyCompilation? compilation,
        out IReadOnlyList<ConfigDiagnostic> errors)
    {
        compilation = null;
        errors = diagnostics;
        return false;
    }

    private static KernelPolicyRule? MapRule(
        HostPolicyRule rule,
        string path,
        IReadOnlyDictionary<string, ProxyTarget> targets,
        ProxyTarget?[] targetsByRuleIndex,
        int index,
        List<ConfigDiagnostic> diagnostics)
    {
        var errorsBefore = diagnostics.Count;
        RejectAdapterSelectors(rule.Matcher, path, diagnostics);
        var action = MapAction(rule.Decision.Action, $"{path}.Action", diagnostics);
        var processes = MapProcesses(rule.Matcher.Processes, path, diagnostics);
        var networks = MapNetworks(rule.Matcher.RemoteNetworks, path, diagnostics);
        var ports = MapPorts(rule.Matcher.RemotePorts, path, diagnostics);
        var protocols = MapProtocols(rule.Matcher.Protocols, path, diagnostics);
        var families = MapFamilies(rule.Matcher.AddressFamilies, path, diagnostics);

        if (action == RuleAction.Redirect)
        {
            targetsByRuleIndex[index] = ResolveTarget(rule.Decision.TargetName, path, targets, diagnostics);
        }

        if (action is null || diagnostics.Count != errorsBefore) return null;

        return new KernelPolicyRule
        {
            Processes = processes,
            RemoteNetworks = networks,
            RemotePorts = ports,
            Protocols = protocols,
            Families = families,
            Action = action.Value,
        };
    }

    /// <summary>
    /// Defence in depth: the capability gate refuses an adapter selector under this provider before
    /// the compiler ever runs, so reaching this with a selector is a directly constructed
    /// configuration. Dropping the selector would widen the rule to every adapter — the one outcome a
    /// fail-closed policy cannot accept — so it is reported at the rule's selector path instead.
    /// </summary>
    private static void RejectAdapterSelectors(RuleMatcher matcher, string path, List<ConfigDiagnostic> diagnostics)
    {
        if (matcher.AdapterIds is not null) diagnostics.Add(new($"{path}.AdapterId", AdapterSelectorRefusal));
        if (matcher.AdapterNames is not null) diagnostics.Add(new($"{path}.AdapterName", AdapterSelectorRefusal));
    }

    private static RuleAction? MapAction(FlowAction action, string path, List<ConfigDiagnostic> diagnostics)
    {
        switch (action)
        {
            case FlowAction.Pass:
                return RuleAction.Permit;
            case FlowAction.Proxy:
                return RuleAction.Redirect;
            case FlowAction.Block:
                return RuleAction.Block;
            default:
                diagnostics.Add(new(path, $"Action '{action}' has no Kernel host rule encoding."));
                return null;
        }
    }

    private static RuleAction MapFallback(FlowAction fallback, List<ConfigDiagnostic> diagnostics)
    {
        var mapped = MapAction(fallback, $"{HostPath}.FallbackAction", diagnostics);
        if (mapped != RuleAction.Redirect) return mapped ?? RuleAction.Block;

        // A redirect fallback would need a target, which a fallback decision cannot name; the
        // configuration gate keeps it out and the blob header has no encoding for it.
        diagnostics.Add(new(
            $"{HostPath}.FallbackAction",
            $"The Kernel host rule blob's fallback action must be pass or block; '{fallback}' has no encoding."));
        return RuleAction.Block;
    }

    private static List<ProcessSelector>? MapProcesses(
        IReadOnlySet<string>? processes,
        string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (processes is null) return null;
        if (processes.Count == 0)
        {
            diagnostics.Add(new($"{path}.Process", "A process selector array must not be empty; omit the field to match any process."));
            return null;
        }

        List<ProcessSelector> selectors = new(processes.Count);
        foreach (var text in processes)
        {
            // Syntax only: the compiler resolves a path selector through the normalizer it is given,
            // because that is where the \device\... form and the existence check live.
            try
            {
                selectors.Add(ProcessSelector.Parse(text));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new(
                    $"{path}.Process",
                    $"Process selector '{text}' cannot enter the Kernel host rule blob: {exception.Message}"));
            }
        }

        return selectors;
    }

    private static List<RemoteNetwork>? MapNetworks(
        IReadOnlyList<IPPrefix>? networks,
        string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (networks is null) return null;
        if (networks.Count == 0)
        {
            diagnostics.Add(new($"{path}.RemoteCidr", "A remote network array must not be empty; omit the field to match any network."));
            return null;
        }

        List<RemoteNetwork> mapped = new(networks.Count);
        foreach (var prefix in networks)
        {
            var cidr = prefix.ToString();
            try
            {
                mapped.Add(RemoteNetwork.Parse(cidr));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new(
                    $"{path}.RemoteCidr",
                    $"Remote network '{cidr}' cannot enter the Kernel host rule blob: {exception.Message}"));
            }
        }

        return mapped;
    }

    private static List<PortRange>? MapPorts(
        IReadOnlyList<(ushort Start, ushort End)>? ports,
        string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (ports is null) return null;
        if (ports.Count == 0)
        {
            diagnostics.Add(new($"{path}.RemotePort", "A remote port array must not be empty; omit the field to match any port."));
            return null;
        }

        List<PortRange> mapped = new(ports.Count);
        foreach (var (start, end) in ports)
        {
            if (start == 0 || end < start)
            {
                diagnostics.Add(new(
                    $"{path}.RemotePort",
                    $"Port range {start}-{end} is not a valid Kernel host rule interval."));
                continue;
            }

            mapped.Add(new PortRange(start, end));
        }

        return mapped;
    }

    private static IReadOnlyList<RuleProtocol>? MapProtocols(
        IReadOnlySet<TransportProtocol>? protocols,
        string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (protocols is null) return null;

        // The blob holds a mask, not a protocol list: selecting both protocols is the same encoding
        // as selecting neither, which is the wildcard.
        switch (protocols.Count)
        {
            case 0:
                diagnostics.Add(new($"{path}.Protocol", "A protocol array must not be empty; omit the field to match both protocols."));
                return null;
            case 1:
                return [protocols.Contains(TransportProtocol.Tcp) ? RuleProtocol.Tcp : RuleProtocol.Udp];
            default:
                return [RuleProtocol.Any];
        }
    }

    private static IReadOnlyList<RuleAddressFamily>? MapFamilies(
        IReadOnlySet<AddressFamilyKind>? families,
        string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (families is null) return null;

        switch (families.Count)
        {
            case 0:
                diagnostics.Add(new($"{path}.AddressFamily", "An address family array must not be empty; omit the field to match both families."));
                return null;
            case 1:
                return [families.Contains(AddressFamilyKind.IPv4) ? RuleAddressFamily.Ipv4 : RuleAddressFamily.Ipv6];
            default:
                return [RuleAddressFamily.Any];
        }
    }

    private static ProxyTarget? ResolveTarget(
        string? targetName,
        string path,
        IReadOnlyDictionary<string, ProxyTarget> targets,
        List<ConfigDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            diagnostics.Add(new($"{path}.Target", "Proxy action requires a configured target."));
            return null;
        }

        if (!targets.TryGetValue(targetName, out var target))
        {
            diagnostics.Add(new($"{path}.Target", $"Target '{targetName}' is not configured."));
            return null;
        }

        // Defence in depth: the capability gate refuses a local target under this provider (a local
        // target needs host UDP, which the Kernel backend does not serve), so reaching this is a
        // converter bug rather than a supported configuration.
        if (target.IsLocal)
        {
            diagnostics.Add(new(
                $"{path}.Target",
                $"The {BackendDriverConfiguration.KernelProviderName} provider has no host UDP relay, so local target '{target.Name}' cannot serve this rule; use a SOCKS5 target."));
            return null;
        }

        return target;
    }

    /// <summary>
    /// Attributes a <see cref="RuleBlobCompiler"/> rejection to the rule that caused it. The compiler
    /// names the rule as <c>规则 #N</c>; a process-path failure additionally carries the selector the
    /// recorder saw fail, which moves the diagnostic from the rule to its <c>Process</c> field.
    /// </summary>
    private static ConfigDiagnostic DiagnoseCompilerFailure(
        string message,
        string? failedSelector,
        IReadOnlyList<HostPolicyRule> hostRules)
    {
        if (TryReadRuleIndex(message, out var ruleIndex) && ruleIndex < hostRules.Count)
        {
            var path = RulePath(ruleIndex);
            return failedSelector is null ? new(path, message) : new($"{path}.Process", message);
        }

        // No rule number: the compiler rejected the table as a whole (the blob size limit).
        return new(HostPath, message);
    }

    private static ConfigDiagnostic DiagnoseNormalizerFailure(
        Exception exception,
        string failedSelector,
        IReadOnlyList<HostPolicyRule> hostRules)
    {
        var message = $"Process selector '{failedSelector}' cannot be resolved for the Kernel host rule blob: {exception.Message}";
        return TryFindRuleWithSelector(hostRules, failedSelector, out var index)
            ? new($"{RulePath(index)}.Process", message)
            : new(HostPath, message);
    }

    private static bool TryReadRuleIndex(string message, out int ruleIndex)
    {
        ruleIndex = -1;
        if (!message.StartsWith(RuleNumberPrefix, StringComparison.Ordinal)) return false;

        var start = RuleNumberPrefix.Length;
        var end = message.IndexOf(RuleNumberTerminator, start);
        return end > start
            && int.TryParse(message.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out ruleIndex);
    }

    /// <summary>
    /// Finds the rule that owns a selector the normalizer failed on. The compiler walks the rules in
    /// order and stops at the first failure, so the first rule carrying the selector is the one that
    /// threw.
    /// </summary>
    private static bool TryFindRuleWithSelector(
        IReadOnlyList<HostPolicyRule> hostRules,
        string selector,
        out int ruleIndex)
    {
        for (ruleIndex = 0; ruleIndex < hostRules.Count; ruleIndex++)
        {
            if (RuleCarriesSelector(hostRules[ruleIndex], selector)) return true;
        }

        return false;
    }

    private static bool RuleCarriesSelector(HostPolicyRule hostRule, string selector) =>
        hostRule.Matcher.Processes?.Any(stored =>
            string.Equals(ProcessSelector.Parse(stored).Value, selector, StringComparison.OrdinalIgnoreCase)) == true;

    private static int ReadRuleCount(byte[] blob) =>
        (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(HeaderRuleCountOffset, sizeof(uint)));

    private static string RulePath(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"WinForward.Host.Rules[{index}]");

    /// <summary>
    /// Delegates to the configured normalizer and remembers the selector it answered null or threw
    /// for, so a compile failure can be reported against the rule that owns it.
    /// </summary>
    private sealed class RecordingProcessPathNormalizer(IProcessPathNormalizer inner) : IProcessPathNormalizer
    {
        internal string? FailedSelector { get; private set; }

        public ProcessPathResolution? Normalize(string path)
        {
            try
            {
                var resolution = inner.Normalize(path);
                if (resolution is null) FailedSelector = path;
                return resolution;
            }
            catch (Exception)
            {
                FailedSelector = path;
                throw;
            }
        }
    }
}
