using WinForward.Core;
using WinForward.Windows;

namespace WinForward.Runtime.Capture;

/// <summary>
/// Resolves the set of adapters that must be placed in capture tunnel mode for a run. A rule that
/// constrains by <c>adapterId</c>/<c>adapterName</c> selects its resolved adapter(s); a rule with no
/// adapter constraint (for example a host-process rule) can match traffic on any adapter, so the
/// presence of any unconstrained rule widens capture scope to every MSTCP-bound adapter. At startup,
/// missing or ambiguous selectors fail with an actionable diagnostic rather than silently narrowing
/// or widening policy (<see cref="TryResolve"/>); on adapter-list refresh, selector failures narrow
/// scope with warnings instead of stopping the run (<see cref="ResolveForRefresh"/>).
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

            ResolveRuleScope(adapters, matcher, ruleIndex, scopeSet, errorList, fatal: true);
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

    /// <summary>
    /// Refresh-mode scope resolution for adapter-list changes (task 09-07-adapter-list-refresh,
    /// PRD R4 — the deliberate non-fatal counterpart to fail-closed startup resolution): adapters
    /// may have disappeared or appeared since the run started, so a selector that no longer
    /// resolves, resolves ambiguously, or whose <c>adapterId</c>/<c>adapterName</c> pair disagrees
    /// produces a warning and narrows scope instead of failing the run. A rule whose selector
    /// matched nothing, or whose id/name pair disagrees, contributes nothing; an ambiguous selector
    /// skips only its own addition. Widening to every MSTCP-bound adapter still applies for any
    /// unconstrained rule, but a scope emptied by selector warnings never widens: a policy whose
    /// constrained adapters all disappeared captures nothing rather than everything.
    /// </summary>
    public static IReadOnlyList<WindowsAdapter> ResolveForRefresh(IReadOnlyList<WindowsAdapter> adapters, PolicySnapshot policy, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(policy);

        var warningList = new List<string>();
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

            ResolveRuleScope(adapters, matcher, ruleIndex, scopeSet, warningList, fatal: false);
        }

        if (anyUnconstrainedRule || (scopeSet.Count == 0 && warningList.Count == 0))
        {
            foreach (var adapter in adapters) scopeSet.Add(adapter);
        }

        warnings = warningList;
        return scopeSet.OrderBy(adapter => adapter.StableId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ResolveRuleScope(IReadOnlyList<WindowsAdapter> adapters, Core.RuleMatcher matcher, int ruleIndex, HashSet<WindowsAdapter> scope, List<string> diagnostics, bool fatal)
    {
        var idMatches = new List<WindowsAdapter>();
        var nameMatches = new List<WindowsAdapter>();
        var ruleDiagnosticStart = diagnostics.Count;
        var ruleContributesNothing = false;
        if (matcher.AdapterIds is not null)
        {
            ruleContributesNothing |= ResolveSelectorSet(adapters, matcher.AdapterIds, adapter => adapter.StableId, ruleIndex, idMatches, diagnostics, fatal);
        }
        if (matcher.AdapterNames is not null)
        {
            ruleContributesNothing |= ResolveSelectorSet(adapters, matcher.AdapterNames, adapter => adapter.FriendlyName, ruleIndex, nameMatches, diagnostics, fatal);
        }

        // Both fields use AND semantics and must resolve to the same adapter; a rule that can
        // never match is a configuration error rather than a silent fallback. The consistency
        // check needs both sides fully resolved: fatal mode skips it once any diagnostic exists
        // (the run fails anyway); refresh mode skips it after any warning or skipped ambiguous
        // addition in this rule.
        var andCheckEligible = fatal ? diagnostics.Count == 0 : diagnostics.Count == ruleDiagnosticStart;
        if (matcher.AdapterIds is not null && matcher.AdapterNames is not null && andCheckEligible)
        {
            var byId = idMatches.Select(adapter => adapter.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byName = nameMatches.Select(adapter => adapter.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!byId.SetEquals(byName))
            {
                diagnostics.Add($"rules[{ruleIndex}]: adapterId and adapterName resolve to different adapters; both fields must select the same adapter.");
                if (!fatal) ruleContributesNothing = true;
            }
        }

        if (!fatal && ruleContributesNothing) return;

        foreach (var adapter in idMatches) scope.Add(adapter);
        foreach (var adapter in nameMatches) scope.Add(adapter);
    }

    private static bool ResolveSelectorSet(
        IReadOnlyList<WindowsAdapter> adapters,
        IReadOnlySet<string>? selectors,
        Func<WindowsAdapter, string> valueOf,
        int ruleIndex,
        List<WindowsAdapter> matches,
        List<string> diagnostics,
        bool fatal)
    {
        if (selectors is null) return false;
        var ruleContributesNothing = false;
        foreach (var selector in selectors)
        {
            var found = adapters.Where(adapter => string.Equals(valueOf(adapter), selector, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 0)
            {
                diagnostics.Add($"rules[{ruleIndex}]: configured adapter selector '{selector}' matches no current adapter.");
                if (!fatal) ruleContributesNothing = true;
            }
            else if (found.Length > 1)
            {
                var conflicts = string.Join(", ", found.Select(adapter => adapter.StableId + " (" + adapter.FriendlyName + ")"));
                diagnostics.Add($"rules[{ruleIndex}]: configured adapter selector '{selector}' is ambiguous; matching adapters: {conflicts}.");
            }
            else
            {
                matches.Add(found[0]);
            }
        }
        return ruleContributesNothing;
    }
}
