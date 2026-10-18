using System.Text.Json;
using WinForward.E2E.Analysis.Metrics;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// The metric-shaped half of the thresholds schema: the inferential family, the capability gates, the
/// exploratory set and the pre-registered equivalence families.
/// </summary>
/// <remarks>
/// <b>A metric is checked against the analysis's own catalogue, not a list kept here.</b> The keys the
/// parser accepts are exactly the keys <c>MetricCatalogue</c> publishes, so a metric the analysis does
/// not render cannot appear in a thresholds file, and a metric it does render does not need a second
/// declaration to become legal. The declared kind is then checked against the metric's unit: a
/// percentage-point rate declared a ratio would be judged against a ratio band, which is a different
/// decision rather than a wording difference.
/// </remarks>
internal static class ThresholdsMetrics
{
    private static readonly string[] s_primaryKeys = ["metric", "kind", "band", "business", "mdd"];

    private static readonly string[] s_capabilityKeys = ["kind", "upperBound", "nFrom"];

    private static readonly string[] s_equivalenceKeys = ["families"];

    private static readonly string[] s_familyKeys = ["metric", "margin"];

    /// <summary>The inferential family: one declaration per primary metric, in the file's order.</summary>
    internal static bool TryReadPrimary(
        Dictionary<string, JsonElement> root,
        out List<PrimaryMetric>? primary,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(root);

        primary = null;
        if (!ThresholdsJson.TryRequired(root, "primary", "primary", out var element, out error)
            || !ThresholdsJson.RequireArray(element, "primary", out error))
        {
            return false;
        }

        var declared = new List<PrimaryMetric>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            if (!TryReadPrimaryEntry(entry, ThresholdsJson.Indexed("primary", index), seen, out var metric, out error))
            {
                return false;
            }

            declared.Add(metric);
            index++;
        }

        if (declared.Count == 0)
        {
            error = $"{ThresholdsJson.Prefix}'primary' is empty";
            return false;
        }

        primary = declared;
        return true;
    }

    /// <summary>The capability gates the release reports as bounds rather than decisions.</summary>
    internal static bool TryReadCapability(
        Dictionary<string, JsonElement> root,
        out Dictionary<string, CapabilityRule>? capability,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(root);

        error = null;
        var declared = new Dictionary<string, CapabilityRule>(StringComparer.Ordinal);
        if (!root.TryGetValue("capability", out var element))
        {
            capability = declared;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            capability = null;
            error = $"{ThresholdsJson.Prefix}'capability' is not a JSON object";
            return false;
        }

        foreach (var member in element.EnumerateObject())
        {
            if (!TryReadCapabilityEntry(member, declared, out var rule, out error))
            {
                capability = null;
                return false;
            }

            declared[member.Name] = rule;
        }

        capability = declared;
        return true;
    }

    /// <summary>
    /// The exploratory set: the file's own declaration, or every published metric the primary family
    /// and the capability gates do not claim.
    /// </summary>
    internal static bool TryReadExploratory(
        Dictionary<string, JsonElement> root,
        List<PrimaryMetric> primary,
        Dictionary<string, CapabilityRule> capability,
        out List<string>? exploratory,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(capability);

        exploratory = null;
        error = null;
        var primaryKeys = primary.Select(entry => entry.Metric).ToHashSet(StringComparer.Ordinal);
        if (!root.TryGetValue("exploratory", out var element))
        {
            exploratory = UnclaimedMetrics(primaryKeys, capability);
            return true;
        }

        if (!ThresholdsJson.RequireArray(element, "exploratory", out error))
        {
            return false;
        }

        var declared = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            var path = ThresholdsJson.Indexed("exploratory", index);
            if (!TryReadExploratoryEntry(entry, path, out var metric, out error))
            {
                return false;
            }

            if (primaryKeys.Contains(metric))
            {
                error = $"{ThresholdsJson.Prefix}'{path}' names '{metric}', which is a primary metric";
                return false;
            }

            if (capability.ContainsKey(metric))
            {
                error = $"{ThresholdsJson.Prefix}'{path}' names '{metric}', which is a capability gate";
                return false;
            }

            if (!seen.Add(metric))
            {
                error = $"{ThresholdsJson.Prefix}'{path}' repeats '{metric}'";
                return false;
            }

            declared.Add(metric);
            index++;
        }

        exploratory = declared;
        return true;
    }

    /// <summary>The pre-registered TOST families, empty unless the owner declared one.</summary>
    internal static bool TryReadEquivalence(
        Dictionary<string, JsonElement> root,
        List<PrimaryMetric> primary,
        out List<EquivalenceFamily>? families,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(primary);

        error = null;
        families = [];
        if (!root.TryGetValue("equivalence", out var element))
        {
            return true;
        }

        if (!ThresholdsJson.TryMembers(element, "equivalence", s_equivalenceKeys, out var equivalence, out error)
            || equivalence is null
            || !ThresholdsJson.TryRequired(equivalence, "families", "equivalence.families", out var familiesElement, out error)
            || !ThresholdsJson.RequireArray(familiesElement, "equivalence.families", out error))
        {
            families = null;
            return false;
        }

        var primaryKeys = primary.Select(entry => entry.Metric).ToHashSet(StringComparer.Ordinal);
        var declared = new List<EquivalenceFamily>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in familiesElement.EnumerateArray())
        {
            if (!TryReadEquivalenceEntry(
                    entry,
                    ThresholdsJson.Indexed("equivalence.families", index),
                    primaryKeys,
                    seen,
                    out var family,
                    out error))
            {
                families = null;
                return false;
            }

            declared.Add(family);
            index++;
        }

        families = declared;
        return true;
    }

    /// <summary>The metric's own declaration, or null when the analysis does not publish it.</summary>
    private static MetricSpec? FindMetric(string key) =>
        MetricCatalogue.Specs.FirstOrDefault(spec => string.Equals(spec.Key, key, StringComparison.Ordinal));

    private static List<string> UnclaimedMetrics(
        HashSet<string> primaryKeys,
        Dictionary<string, CapabilityRule> capability) =>
    [
        .. MetricCatalogue.Specs
            .Select(spec => spec.Key)
            .Where(key => !primaryKeys.Contains(key) && !capability.ContainsKey(key)),
    ];

    private static bool TryReadPrimaryEntry(
        JsonElement entry,
        string path,
        HashSet<string> seen,
        out PrimaryMetric metric,
        out string? error)
    {
        metric = null!;
        if (!ThresholdsJson.TryMembers(entry, path, s_primaryKeys, out var members, out error)
            || members is null
            || !ThresholdsJson.TryReadRequiredString(members, "metric", path, out var key, out error))
        {
            return false;
        }

        var keyPath = ThresholdsJson.Join(path, "metric");
        var spec = FindMetric(key);
        if (spec is null)
        {
            error = $"{ThresholdsJson.Prefix}'{keyPath}' names an unknown metric '{key}'";
            return false;
        }

        if (!seen.Add(key))
        {
            error = $"{ThresholdsJson.Prefix}'{keyPath}' repeats metric '{key}'";
            return false;
        }

        if (!ThresholdsJson.TryReadRequiredString(members, "kind", path, out var kind, out error)
            || !TryCheckKind(kind, ThresholdsJson.Join(path, "kind"), spec, key, out error)
            || !ThresholdsJson.TryReadPositiveNumber(members, "band", path, out var band, out error)
            || !ThresholdsJson.TryReadOptionalCount(members, "business", ThresholdsJson.Join(path, "business"), out var business, out error)
            || !ThresholdsJson.TryReadOptionalCount(members, "mdd", ThresholdsJson.Join(path, "mdd"), out var mdd, out error))
        {
            return false;
        }

        metric = new PrimaryMetric(key, kind, band, business, mdd);
        return true;
    }

    private static bool TryReadCapabilityEntry(
        JsonProperty member,
        Dictionary<string, CapabilityRule> declared,
        out CapabilityRule rule,
        out string? error)
    {
        rule = null!;
        var path = "capability." + member.Name;
        var spec = FindMetric(member.Name);
        if (spec is null)
        {
            error = $"{ThresholdsJson.Prefix}'{path}' names an unknown metric '{member.Name}'";
            return false;
        }

        if (declared.ContainsKey(member.Name))
        {
            error = $"{ThresholdsJson.Prefix}duplicate key '{path}'";
            return false;
        }

        if (!ThresholdsJson.TryMembers(member.Value, path, s_capabilityKeys, out var members, out error)
            || members is null
            || !ThresholdsJson.TryReadRequiredString(members, "kind", path, out var kind, out error)
            || !TryCheckKind(kind, ThresholdsJson.Join(path, "kind"), spec, member.Name, out error)
            || !ThresholdsJson.TryReadPositiveNumber(members, "upperBound", path, out var upperBound, out error))
        {
            return false;
        }

        string? nFrom = null;
        if (members.TryGetValue("nFrom", out var nFromElement))
        {
            if (!ThresholdsJson.TryString(nFromElement, ThresholdsJson.Join(path, "nFrom"), out var text, out error))
            {
                return false;
            }

            if (text.Length == 0)
            {
                error = $"{ThresholdsJson.Prefix}'{ThresholdsJson.Join(path, "nFrom")}' is empty";
                return false;
            }

            nFrom = text;
        }

        rule = new CapabilityRule(member.Name, kind, upperBound, nFrom);
        return true;
    }

    private static bool TryReadExploratoryEntry(
        JsonElement entry,
        string path,
        out string metric,
        out string? error)
    {
        metric = string.Empty;
        if (entry.ValueKind != JsonValueKind.String)
        {
            error = $"{ThresholdsJson.Prefix}'{path}' is not a string";
            return false;
        }

        metric = entry.GetString() ?? string.Empty;
        if (FindMetric(metric) is null)
        {
            error = $"{ThresholdsJson.Prefix}'{path}' names an unknown metric '{metric}'";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadEquivalenceEntry(
        JsonElement entry,
        string path,
        HashSet<string> primaryKeys,
        HashSet<string> seen,
        out EquivalenceFamily family,
        out string? error)
    {
        family = null!;
        if (!ThresholdsJson.TryMembers(entry, path, s_familyKeys, out var members, out error)
            || members is null
            || !ThresholdsJson.TryReadRequiredString(members, "metric", path, out var metric, out error))
        {
            return false;
        }

        var metricPath = ThresholdsJson.Join(path, "metric");
        if (!primaryKeys.Contains(metric))
        {
            error = $"{ThresholdsJson.Prefix}'{metricPath}' is not a primary metric ('{metric}')";
            return false;
        }

        if (!seen.Add(metric))
        {
            error = $"{ThresholdsJson.Prefix}'{metricPath}' repeats '{metric}'";
            return false;
        }

        if (!ThresholdsJson.TryReadPositiveNumber(members, "margin", path, out var margin, out error))
        {
            return false;
        }

        family = new EquivalenceFamily(metric, margin);
        return true;
    }

    private static bool TryCheckKind(string kind, string path, MetricSpec spec, string metric, out string? error)
    {
        if (!ThresholdKind.IsDeclared(kind))
        {
            error = $"{ThresholdsJson.Prefix}'{path}' is '{kind}', not '{ThresholdKind.Ratio}' or '{ThresholdKind.PercentagePoints}'";
            return false;
        }

        var isPercentagePoints = string.Equals(spec.Unit, "pp", StringComparison.Ordinal);
        if (isPercentagePoints == string.Equals(kind, ThresholdKind.PercentagePoints, StringComparison.Ordinal))
        {
            error = null;
            return true;
        }

        error = $"{ThresholdsJson.Prefix}'{path}' is '{kind}', but '{metric}' is measured in {spec.Unit}";
        return false;
    }
}
