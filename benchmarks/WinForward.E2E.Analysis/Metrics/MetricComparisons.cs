using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Metrics;

/// <summary>One candidate pair of rows for one metric: whether it can be claimed at all, and why not.</summary>
/// <param name="A">The first row, as the reference's own ordering names it.</param>
/// <param name="B">The second row.</param>
/// <param name="Comparable">Whether both sides carry a per-pass value and the same traffic path.</param>
/// <param name="Reason">Why the pair makes no claim, or null when it does.</param>
/// <param name="TestedIndex">This pair's position in the family of tested comparisons, or -1.</param>
internal sealed record MetricPair(string A, string B, bool Comparable, string? Reason, int TestedIndex);

/// <summary>
/// <c>verdict.json</c>'s <c>metrics</c> object: every metric's per-row readings, and the bootstrap
/// comparison of every pair of rows that can be compared at all.
/// </summary>
/// <remarks>
/// <para><b>Passes are the resampling unit and the seed is derived, not drawn.</b> A comparison's
/// generator is seeded from the metric's key and the two row ids, so the same campaign re-analysed
/// produces the same intervals (see <see cref="CpRandom.DeriveSeed"/>).</para>
/// <para><b>A pair that cannot be claimed is reported, not dropped.</b> A row that never ran the arm, a
/// product that does not carry UDP, and a port-53 arm measuring a different path all make no claim, and
/// the pair is listed as <c>n/a (not comparable)</c> outside the Holm family rather than silently
/// excluded — the family size is what the adjustment multiplies by, so dropping a row would change
/// every other pair's verdict.</para>
/// <para><b>The adjusted claim is the one the pair actually made.</b> A <c>different</c> claim is judged
/// by the no-difference p-value, a <c>same</c> claim by the TOST equivalence p-value, and anything else
/// makes no claim to adjust.</para>
/// </remarks>
internal static class MetricComparisons
{
    private const string EntryOrder = "ratio = a/b, difference = a - b (percentage points)";

    private const string NotComparable = "n/a (not comparable)";

    private const string NoValueReason = "one side has no per-pass value for this metric";

    private const string DegenerateNote = "degenerate bootstrap CI (zero variance across passes)";

    private const double ClaimAlpha = 0.05;

    /// <summary>The whole <c>metrics</c> object, at the nesting level its members sit at.</summary>
    /// <param name="campaign">The loaded campaign every number is read from.</param>
    /// <returns>The object's JSON text.</returns>
    internal static string Render(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var members = new (string Key, string Value)[MetricCatalogue.Specs.Count];
        for (var index = 0; index < MetricCatalogue.Specs.Count; index++)
        {
            var spec = MetricCatalogue.Specs[index];
            members[index] = (spec.Key, One(campaign, spec));
        }

        return VerbatimJson.Object(1, members);
    }

    /// <summary>One metric's entry: its declaration, its per-row readings and its pair comparisons.</summary>
    private static string One(CampaignModel campaign, MetricSpec spec)
    {
        var threshold = Thresholds.For(spec.Family);
        var cells = new Dictionary<string, MetricCell>(StringComparer.Ordinal);
        foreach (var rowId in campaign.RowIds)
        {
            cells[rowId] = MetricCatalogue.PerPassValues(campaign, spec, rowId);
        }

        var candidates = Candidates(campaign, spec, cells);
        return VerbatimJson.Object(
            2,
            ("label", VerbatimJson.String(spec.Label)),
            ("unit", VerbatimJson.String(spec.Unit)),
            ("family", VerbatimJson.String(spec.Family)),
            ("threshold", VerbatimJson.Object(
                3,
                ("kind", VerbatimJson.String(threshold.Kind)),
                ("value", Number(threshold.Value)),
                ("text", VerbatimJson.String(threshold.Text)))),
            ("definition", VerbatimJson.String(spec.Definition)),
            ("arm", Text(spec.Arm)),
            ("udp_path", Text(spec.UdpPath)),
            ("dns53", VerbatimJson.Boolean(spec.Dns53)),
            ("rows", Rows(campaign, spec, cells)),
            ("holm_family_size", VerbatimJson.Integer(candidates.Count(pair => pair.Comparable))),
            ("not_comparable_pairs", VerbatimJson.Integer(candidates.Count(pair => !pair.Comparable))),
            ("pairs", Pairs(campaign, spec, threshold, cells, candidates)));
    }

    /// <summary>Every pair of rows, in the reference's own ordering, with its comparability decided.</summary>
    private static List<MetricPair> Candidates(
        CampaignModel campaign,
        MetricSpec spec,
        Dictionary<string, MetricCell> cells)
    {
        var pairs = new List<MetricPair>();
        var tested = 0;
        for (var first = 0; first < campaign.RowIds.Count; first++)
        {
            for (var second = first + 1; second < campaign.RowIds.Count; second++)
            {
                var rowA = campaign.RowIds[first];
                var rowB = campaign.RowIds[second];
                var reason = Comparability(spec, cells[rowA], cells[rowB], rowA, rowB);
                pairs.Add(reason is null
                    ? new MetricPair(rowA, rowB, Comparable: true, Reason: null, TestedIndex: tested++)
                    : new MetricPair(rowA, rowB, Comparable: false, Reason: reason, TestedIndex: -1));
            }
        }

        return pairs;
    }

    /// <summary>Why one pair makes no claim about this metric, or null when it does.</summary>
    private static string? Comparability(MetricSpec spec, MetricCell cellA, MetricCell cellB, string rowA, string rowB)
    {
        foreach (var (rowId, cell) in new[] { (rowA, cellA), (rowB, cellB) })
        {
            if (cell.Status is MetricStatus.NotInPlan or MetricStatus.DeclaredAbsent)
            {
                return $"not measured in this row: {cell.StatusReason} ({rowId})";
            }

            if (string.Equals(cell.Status, MetricStatus.NotCarried, StringComparison.Ordinal))
            {
                return $"{rowId}: {RowProfiles.NotCarriedCell}";
            }
        }

        if (spec.Dns53)
        {
            var profileA = RowProfiles.Find(rowA);
            var profileB = RowProfiles.Find(rowB);
            if (profileA is not null && profileB is not null
                && !string.Equals(profileA.Udp53, profileB.Udp53, StringComparison.Ordinal))
            {
                return "the port-53 UDP carriage differs ("
                    + $"{RowProfiles.Udp53Label(profileA.Udp53)} vs {RowProfiles.Udp53Label(profileB.Udp53)}), "
                    + "so this arm is not comparable across the two rows; the DNSALT arm is";
            }
        }

        return cellA.Values.Count == 0 || cellB.Values.Count == 0 ? NoValueReason : null;
    }

    /// <summary>The per-row readings: the per-pass values, the medians, and the status behind them.</summary>
    private static string Rows(CampaignModel campaign, MetricSpec spec, Dictionary<string, MetricCell> cells)
    {
        var members = new (string Key, string Value)[campaign.RowIds.Count];
        for (var index = 0; index < campaign.RowIds.Count; index++)
        {
            var rowId = campaign.RowIds[index];
            var cell = cells[rowId];
            var values = cell.SortedValues();
            var defined = cell.Status is MetricStatus.Ok or MetricStatus.DnsCarriage;
            members[index] = (rowId, VerbatimJson.Object(
                4,
                ("unit", VerbatimJson.String(spec.Unit)),
                ("passes", VerbatimJson.Integer(values.Count)),
                ("per_pass", Numbers(cell)),
                ("unavailable_passes", Reasons(cell)),
                ("null_passes", VerbatimJson.Integer(cell.NullPasses)),
                ("median", Number(DescriptiveStats.Median(values))),
                ("iqr", VerbatimJson.Array(
                    5,
                    [Number(DescriptiveStats.Quantile(values, 0.25)), Number(DescriptiveStats.Quantile(values, 0.75))])),
                ("status", VerbatimJson.String(cell.Status)),
                ("status_reason", Text(cell.StatusReason)),
                ("comparable", VerbatimJson.Boolean(defined && values.Count > 0))));
        }

        return VerbatimJson.Object(3, members);
    }

    /// <summary>The per-pass values, in natural-key pass order.</summary>
    private static string Numbers(MetricCell cell)
    {
        var members = new List<(string Key, string Value)>();
        foreach (var passId in NaturalKey.Sort(cell.Values.Keys))
        {
            members.Add((passId, VerbatimJson.Number(cell.Values[passId])));
        }

        return VerbatimJson.Object(5, [.. members]);
    }

    /// <summary>The per-pass reasons, in natural-key pass order.</summary>
    private static string Reasons(MetricCell cell)
    {
        var members = new List<(string Key, string Value)>();
        foreach (var passId in NaturalKey.Sort(cell.Reasons.Keys))
        {
            members.Add((passId, VerbatimJson.String(cell.Reasons[passId])));
        }

        return VerbatimJson.Object(5, [.. members]);
    }

    /// <summary>The pair comparisons: each tested pair's interval, p-values and adjusted verdict.</summary>
    private static string Pairs(
        CampaignModel campaign,
        MetricSpec spec,
        ThresholdSpec threshold,
        Dictionary<string, MetricCell> cells,
        List<MetricPair> candidates)
    {
        var family = candidates.Count(pair => pair.Comparable);
        var comparisons = new Comparison?[family];
        var errors = new string?[family];
        foreach (var pair in candidates)
        {
            if (!pair.Comparable)
            {
                continue;
            }

            var (comparison, error) = BootstrapPair.Draw(
                cells[pair.A].Values,
                cells[pair.B].Values,
                threshold.Kind,
                threshold.Value,
                campaign.Resamples,
                CpRandom.DeriveSeed(campaign.Seed, $"{spec.Key}|{pair.A}|{pair.B}"));
            comparisons[pair.TestedIndex] = comparison;
            errors[pair.TestedIndex] = error;
        }

        var difference = DescriptiveStats.HolmAdjust(
            [.. comparisons.Select(comparison => comparison?.PValue ?? 1.0)]);
        var equivalence = DescriptiveStats.HolmAdjust(
            [.. comparisons.Select(comparison => comparison?.PEquivalence ?? 1.0)]);

        var rendered = new List<string>(candidates.Count);
        foreach (var pair in candidates)
        {
            rendered.Add(pair.Comparable
                ? Compared(campaign, threshold, cells, pair, comparisons, errors, difference, equivalence)
                : VerbatimJson.Object(
                    4,
                    ("a", VerbatimJson.String(pair.A)),
                    ("b", VerbatimJson.String(pair.B)),
                    ("comparable", VerbatimJson.Boolean(pair.Comparable)),
                    ("reason", VerbatimJson.String(pair.Reason!)),
                    ("verdict", VerbatimJson.String(NotComparable)),
                    ("in_holm_family", VerbatimJson.Boolean(pair.Comparable))));
        }

        return VerbatimJson.Array(3, rendered);
    }

    /// <summary>One tested pair: the interval as drawn, and the verdict both raw and adjusted.</summary>
    private static string Compared(
        CampaignModel campaign,
        ThresholdSpec threshold,
        Dictionary<string, MetricCell> cells,
        MetricPair pair,
        Comparison?[] comparisons,
        string?[] errors,
        List<double> difference,
        List<double> equivalence)
    {
        var index = pair.TestedIndex;
        var comparison = comparisons[index];
        var passesUsed = cells[pair.A].Values.Keys.Count(cells[pair.B].Values.ContainsKey);
        var (rawVerdict, rawReason) = comparison is null
            ? ("inconclusive", errors[index] ?? NoValueReason)
            : BootstrapPair.Decide(threshold.Kind, threshold.Value, comparison, passesUsed, campaign.MinPasses);
        var claim = string.Equals(rawVerdict, "same", StringComparison.Ordinal) ? equivalence[index] : difference[index];
        var (adjustedVerdict, adjustedReason) = (rawVerdict, rawReason);
        if (rawVerdict is "different" or "same" && claim >= ClaimAlpha)
        {
            adjustedVerdict = "inconclusive";
            adjustedReason = $"Holm-adjusted p={VerbatimNumber.Fixed(claim, 4)} >= 0.05 ({rawReason})";
        }

        var members = new List<(string Key, string Value)>
        {
            ("a", VerbatimJson.String(pair.A)),
            ("b", VerbatimJson.String(pair.B)),
            ("comparable", VerbatimJson.Boolean(pair.Comparable)),
            ("in_holm_family", VerbatimJson.Boolean(pair.Comparable)),
            ("order", VerbatimJson.String(EntryOrder)),
            ("passes_used", VerbatimJson.Integer(passesUsed)),
            ("passes_a", VerbatimJson.Integer(cells[pair.A].Values.Count)),
            ("passes_b", VerbatimJson.Integer(cells[pair.B].Values.Count)),
            ("resampling", Text(comparison?.Mode)),
            ("estimate", Number(comparison?.Estimate)),
            ("ci95", VerbatimJson.Array(5, [Number(comparison?.CiLow), Number(comparison?.CiHigh)])),
            ("p_value", Number(comparison?.PValue)),
            ("holm_p_value", VerbatimJson.Number(difference[index])),
            ("p_equivalence", Number(comparison?.PEquivalence)),
            ("holm_p_equivalence", VerbatimJson.Number(equivalence[index])),
            ("raw_verdict", VerbatimJson.String(rawVerdict)),
            ("raw_reason", VerbatimJson.String(rawReason)),
            ("holm_verdict", VerbatimJson.String(adjustedVerdict)),
            ("holm_reason", VerbatimJson.String(adjustedReason)),
        };

        if (errors[index] is { } error)
        {
            members.Add(("note", VerbatimJson.String(error)));
        }
        else if (comparison is { Degenerate: true })
        {
            members.Add(("note", VerbatimJson.String(DegenerateNote)));
        }

        return VerbatimJson.Object(4, [.. members]);
    }

    /// <summary>A number, or the null literal when the reading has no value.</summary>
    private static string Number(double? value) => value is { } number ? VerbatimJson.Number(number) : VerbatimJson.Null;

    /// <summary>A string, or the null literal where the reference publishes a design absence as null.</summary>
    private static string Text(string? value) => value is null ? VerbatimJson.Null : VerbatimJson.String(value);
}
