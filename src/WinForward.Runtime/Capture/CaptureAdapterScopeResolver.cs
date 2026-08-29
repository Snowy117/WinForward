using WinForward.Core;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// Resolves the set of adapters that must be placed in capture tunnel mode for a run. A rule that
/// constrains by <c>adapterId</c>/<c>adapterName</c> selects its resolved adapter(s); a rule with no
/// adapter constraint (for example a host-process rule) can match traffic on any adapter, so the
/// presence of any unconstrained rule widens capture scope to every MSTCP-bound adapter. Missing or
/// ambiguous selectors fail startup with an actionable diagnostic rather than silently narrowing or
/// widening policy.
/// </summary>
public static class CaptureAdapterScopeResolver
{
    public static bool TryResolve(IReadOnlyList<WindowsAdapter> adapters, PolicySnapshot policy, out IReadOnlyList<WindowsAdapter> scope, out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(policy);

        var errorList = new List<string>();
        var scopeSet = new HashSet<WindowsAdapter>();
        var anyUnconstrainedRule = false;

        for (var ruleIndex = 0; ruleIndex < policy.Rules.Count; ruleIndex++)
        {
            var matcher = policy.Rules[ruleIndex].Matcher;
            if (matcher.AdapterIds is null && matcher.AdapterNames is null)
            {
                anyUnconstrainedRule = true;
                continue;
            }

            ResolveRuleScope(adapters, matcher, ruleIndex, scopeSet, errorList);
        }

        if (errorList.Count > 0)
        {
            scope = [];
            errors = errorList;
            return false;
        }

        // A fallback-only policy (no rules, or only unconstrained rules) can apply to traffic on any
        // adapter, so capture scope widens to every MSTCP-bound adapter.
        if (anyUnconstrainedRule || scopeSet.Count == 0)
        {
            foreach (var adapter in adapters) scopeSet.Add(adapter);
        }

        scope = scopeSet.OrderBy(adapter => adapter.StableId, StringComparer.OrdinalIgnoreCase).ToArray();
        errors = [];
        return true;
    }

    private static void ResolveRuleScope(IReadOnlyList<WindowsAdapter> adapters, Core.RuleMatcher matcher, int ruleIndex, HashSet<WindowsAdapter> scope, List<string> errors)
    {
        var idMatches = new List<WindowsAdapter>();
        var nameMatches = new List<WindowsAdapter>();
        if (matcher.AdapterIds is not null)
        {
            ResolveSelectorSet(adapters, matcher.AdapterIds, adapter => adapter.StableId, ruleIndex, idMatches, errors);
        }
        if (matcher.AdapterNames is not null)
        {
            ResolveSelectorSet(adapters, matcher.AdapterNames, adapter => adapter.FriendlyName, ruleIndex, nameMatches, errors);
        }

        // Both fields use AND semantics and must resolve to the same adapter; a rule that can
        // never match is a configuration error rather than a silent fallback.
        if (matcher.AdapterIds is not null && matcher.AdapterNames is not null && errors.Count == 0)
        {
            var byId = idMatches.Select(adapter => adapter.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byName = nameMatches.Select(adapter => adapter.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!byId.SetEquals(byName))
            {
                errors.Add($"rules[{ruleIndex}]: adapterId and adapterName resolve to different adapters; both fields must select the same adapter.");
            }
        }

        foreach (var adapter in idMatches) scope.Add(adapter);
        foreach (var adapter in nameMatches) scope.Add(adapter);
    }

    private static void ResolveSelectorSet(
        IReadOnlyList<WindowsAdapter> adapters,
        IReadOnlySet<string>? selectors,
        Func<WindowsAdapter, string> valueOf,
        int ruleIndex,
        List<WindowsAdapter> matches,
        List<string> errors)
    {
        if (selectors is null) return;
        foreach (var selector in selectors)
        {
            var found = adapters.Where(adapter => string.Equals(valueOf(adapter), selector, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 0)
            {
                errors.Add($"rules[{ruleIndex}]: configured adapter selector '{selector}' matches no current adapter.");
            }
            else if (found.Length > 1)
            {
                var conflicts = string.Join(", ", found.Select(adapter => adapter.StableId + " (" + adapter.FriendlyName + ")"));
                errors.Add($"rules[{ruleIndex}]: configured adapter selector '{selector}' is ambiguous; matching adapters: {conflicts}.");
            }
            else
            {
                matches.Add(found[0]);
            }
        }
    }
}