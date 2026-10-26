using System.Globalization;
using WinForward.Configuration;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// Resolves a redirect context's <c>rule_index</c> through the compilation's rule-index target table.
/// </summary>
/// <remarks>
/// Every failure is fail-closed. An index the blob never produced (the fallback sentinel arrives as
/// -1), a pass or block rule, or a local target is refused rather than served by a default target: the
/// fallback would silently proxy traffic the policy passed or blocked, and a local target has no
/// SOCKS5 server for the TCP relay to dial.
/// </remarks>
internal static class KernelHostTargetResolver
{
    internal static bool TryResolve(ProxyTarget?[] targetsByRuleIndex, int ruleIndex, out ProxyTarget target, out string error)
    {
        ArgumentNullException.ThrowIfNull(targetsByRuleIndex);
        target = default;
        if (ruleIndex < 0 || ruleIndex >= targetsByRuleIndex.Length)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"rule index {ruleIndex} is outside the {targetsByRuleIndex.Length} compiled host rule(s), so the blob and the target table disagree");
            return false;
        }

        if (targetsByRuleIndex[ruleIndex] is not { } resolved)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"rule {ruleIndex} is not a proxy rule");
            return false;
        }

        if (resolved.Socks5 is null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"rule {ruleIndex} resolves to local target '{resolved.Name}', which this Kernel milestone does not serve; configure a SOCKS5 target");
            return false;
        }

        target = resolved;
        error = string.Empty;
        return true;
    }
}
