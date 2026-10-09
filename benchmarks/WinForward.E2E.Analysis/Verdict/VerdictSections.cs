using WinForward.E2E.Analysis.Checks;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Verdict;

/// <summary>
/// The top-level keys of <c>verdict.json</c> a batch has rendered, as JSON text, in the reference's
/// declaration order.
/// </summary>
/// <remarks>
/// <para><b>A key that is not here is left out of the document.</b> The differ slices the file by
/// top-level key and reports a key the batch owns but the analysis did not write as a *missing slice*
/// (exit code 2), which is what makes the batch boundary enforceable: a key nobody has implemented yet
/// cannot be mistaken for one that renders as nothing.</para>
/// <para><b>The identity string names the analysis, not this repository.</b> The reference used to
/// publish its own path here; both sides now write the neutral program name, so the two files can be
/// compared at all.</para>
/// <para><b><c>bootstrap</c> and <c>thresholds</c> are the run's parameters, published.</b> They are
/// unconditional — the reference emits them for every campaign, including one whose comparisons all
/// come out <c>inconclusive</c> — and they state what the intervals below them were drawn with rather
/// than what any of them came out to, which is why they belong to the same batch as the generator.
/// The thresholds text is the reference's own prose, down to its en dash and its asterisks: it is
/// compared as text, and its <c>'</c> characters are deliberately not escaped by
/// <see cref="VerbatimJson"/>.</para>
/// </remarks>
internal static class VerdictSections
{
    /// <summary>The program name both implementations publish as their identity.</summary>
    private const string GeneratedBy = "e2e-analysis v1";

    /// <summary>What the bootstrap does, in the reference's words.</summary>
    private const string ResamplingUnit = "passes (never samples)";

    /// <summary>How the interval is drawn, in the reference's words.</summary>
    private const string BootstrapMethod = "percentile bootstrap over per-pass values, 95 % CI";

    /// <summary>The declared thresholds and how a pair is judged against them, in the reference's words.</summary>
    private const string ThresholdsNote =
        "A pair is 'different' only when the bootstrap CI excludes both zero and the threshold band, and 'same' "
        + "only when the CI lies wholly inside the band; anything else is 'inconclusive'. Holm–Bonferroni is "
        + "applied across each metric's family of *comparable* pairwise comparisons: a pair whose other side never "
        + "ran the arm, or does not carry UDP, or measured a different port-53 path, makes no claim and is reported "
        + "as 'n/a (not comparable)' outside the family. The adjusted verdict requires the adjusted p-value of the "
        + "claim actually made to be below 0.05 (the no-difference p-value for 'different', the bootstrap TOST "
        + "equivalence p-value for 'same'). A metric with no pre-declared threshold (throughput, event counts) "
        + "reports its CI as 'no-threshold-declared' instead of guessing.";

    /// <summary>Builds every key the analysis renders, in the reference's own declaration order.</summary>
    internal static IReadOnlyDictionary<string, string> Build(CampaignModel campaign, IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(findings);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["generated_by"] = VerbatimJson.String(GeneratedBy),
            ["raw"] = VerbatimJson.String(campaign.Raw),
            ["flat_mode"] = VerbatimJson.Boolean(campaign.Flat),
            ["passes"] = VerbatimJson.StringArray(1, campaign.PassIds),
            ["rows"] = VerbatimJson.StringArray(1, campaign.RowIds),
            ["row_profiles"] = RowProfiles(),
            ["bootstrap"] = VerbatimJson.Object(
                1,
                ("resamples", VerbatimJson.Integer(campaign.Resamples)),
                ("seed", VerbatimJson.Integer(campaign.Seed)),
                ("resampling_unit", VerbatimJson.String(ResamplingUnit)),
                ("method", VerbatimJson.String(BootstrapMethod)),
                ("min_passes", VerbatimJson.Integer(campaign.MinPasses))),
            ["thresholds"] = VerbatimJson.Object(
                1,
                ("latency", VerbatimJson.String("5 %")),
                ("cpu", VerbatimJson.String("10 %")),
                ("memory", VerbatimJson.String("10 %")),
                ("udp-loss", VerbatimJson.String("0.5 percentage points")),
                ("tcp-unexpected", VerbatimJson.String("0.1 percentage points")),
                ("note", VerbatimJson.String(ThresholdsNote))),
            ["findings"] = VerbatimJson.Array(
                1,
                [.. findings.Select(finding => VerbatimJson.Object(
                    2,
                    ("severity", VerbatimJson.String(finding.Severity)),
                    ("kind", VerbatimJson.String(finding.Kind)),
                    ("scope", VerbatimJson.String(finding.Scope)),
                    ("detail", VerbatimJson.String(finding.Detail))))]),
            ["findings_by_severity"] = FindingsBySeverity(findings),
            ["control_blocks"] = ControlBlocks(campaign),
            ["dual_phase"] = DualPhase(campaign),
            ["ledger"] = Ledger(campaign),
            ["metrics"] = MetricComparisons.Render(campaign),
        };
    }

    /// <summary>The two control blocks' comparisons, and what each pass held.</summary>
    private static string ControlBlocks(CampaignModel campaign)
    {
        var drift = ControlDrift.Compute(campaign);
        return VerbatimJson.Object(
            1,
            ("available", VerbatimJson.Boolean(drift.Available)),
            ("comparisons", VerbatimJson.Array(
                2,
                [.. drift.Comparisons.Select(entry => VerbatimJson.Object(
                    3,
                    ("metric", VerbatimJson.String(entry.Metric)),
                    ("key", VerbatimJson.String(entry.Key)),
                    ("kind", VerbatimJson.String(entry.Kind)),
                    ("unit", VerbatimJson.String(entry.Unit)),
                    ("threshold", VerbatimJson.Number(entry.Threshold)),
                    ("pre", Numbers(entry.Pre)),
                    ("post", Numbers(entry.Post)),
                    ("unavailable", Reasons(entry.Unavailable)),
                    ("estimate", Number(entry.Comparison?.Estimate)),
                    ("ci95", VerbatimJson.Array(4, [Number(entry.Comparison?.CiLow), Number(entry.Comparison?.CiHigh)])),
                    ("statement", VerbatimJson.String(entry.Statement)),
                    ("verdict", VerbatimJson.String(entry.Verdict)),
                    ("reason", VerbatimJson.String(entry.Reason))))])),
            ("per_pass", VerbatimJson.Array(
                2,
                [.. drift.PerPass.Select(entry => VerbatimJson.Object(
                    3,
                    ("pass", VerbatimJson.String(entry.Pass)),
                    ("pre_present", VerbatimJson.Boolean(entry.PrePresent)),
                    ("post_present", VerbatimJson.Boolean(entry.PostPresent)),
                    ("ordering", Text(entry.Ordering))))])));
    }

    /// <summary>Every dual phase the campaign ran, one entry per pass and row.</summary>
    private static string DualPhase(CampaignModel campaign)
    {
        var records = DualFindings.Records(campaign);
        return VerbatimJson.Object(
            1,
            ("rows", VerbatimJson.Array(
                2,
                [.. records.Select(record => VerbatimJson.Object(
                    3,
                    ("pass", VerbatimJson.String(record.Pass)),
                    ("row", VerbatimJson.String(record.Row)),
                    ("direct_leak", Number(record.Leak)),
                    ("proxied_latency_p50_us", Number(record.ProxiedLatencyP50)),
                    ("direct_latency_p50_us", Number(record.DirectLatencyP50)),
                    ("proxied_loss_rate_pp", Number(record.ProxiedLossRate)),
                    ("direct_loss_rate_pp", Number(record.DirectLossRate)),
                    ("shape_notes", VerbatimJson.StringArray(4, record.ShapeNotes))))])));
    }

    /// <summary>The ledgers the campaign read, and the DNS port totals they and the client published.</summary>
    private static string Ledger(CampaignModel campaign)
    {
        var views = LedgerViewsBuilder.For(campaign);
        var passes = new List<(string Key, string Value)>();
        foreach (var (passId, entry) in views.Passes)
        {
            passes.Add((passId, VerbatimJson.Object(
                3,
                ("paths", VerbatimJson.StringArray(4, entry.Paths)),
                ("records", VerbatimJson.Integer(entry.Records)),
                ("types", VerbatimJson.Object(
                    4,
                    [.. entry.Types.Select(pair => (pair.Key, VerbatimJson.Integer(pair.Value)))])),
                ("labels", VerbatimJson.Object(
                    4,
                    [.. entry.Labels.Select(pair => (pair.Key, VerbatimJson.Integer(pair.Value)))])),
                ("bad_lines", VerbatimJson.Integer(entry.BadLines)),
                ("attribution", Text(views.Attribution.GetValueOrDefault(passId))))));
        }

        var firstPass = campaign.PassIds.Count > 0 ? campaign.PassIds[0] : string.Empty;
        var ports = new List<(string Key, string Value)>();
        foreach (var (port, totals) in LedgerFindings.DnsTotals(campaign, firstPass))
        {
            var members = new List<(string Key, string Value)>
            {
                ("ledger_udp", VerbatimJson.Number(totals.LedgerUdp)),
                ("ledger_tcp", VerbatimJson.Number(totals.LedgerTcp)),
                ("client_udp", VerbatimJson.Number(totals.ClientUdp)),
                ("client_tcp", VerbatimJson.Number(totals.ClientTcp)),
                ("duration_seconds", VerbatimJson.Number(totals.DurationSeconds)),
            };
            if (totals.Summaries > 0)
            {
                members.Add(("summaries", VerbatimJson.Integer(totals.Summaries)));
                members.Add(("ledger_paths", VerbatimJson.StringArray(4, totals.LedgerPaths)));
            }

            ports.Add((port, VerbatimJson.Object(3, [.. members])));
        }

        return VerbatimJson.Object(
            1,
            ("available", VerbatimJson.Boolean(views.Available)),
            ("passes", VerbatimJson.Object(2, [.. passes])),
            ("dns_ports", VerbatimJson.Object(2, [.. ports])));
    }

    /// <summary>A map of per-pass numbers, natural-key ordered.</summary>
    private static string Numbers(IReadOnlyDictionary<string, double> values)
    {
        var members = new List<(string Key, string Value)>();
        foreach (var passId in NaturalKey.Sort(values.Keys))
        {
            members.Add((passId, VerbatimJson.Number(values[passId])));
        }

        return VerbatimJson.Object(4, [.. members]);
    }

    /// <summary>A map of per-pass reasons, in the order the comparison recorded them.</summary>
    private static string Reasons(IReadOnlyDictionary<string, string> values)
    {
        var members = new List<(string Key, string Value)>();
        foreach (var (passId, reason) in values)
        {
            members.Add((passId, VerbatimJson.String(reason)));
        }

        return VerbatimJson.Object(4, [.. members]);
    }

    /// <summary>A number, or the null literal when the reading has no value.</summary>
    private static string Number(double? value) => value is { } number ? VerbatimJson.Number(number) : VerbatimJson.Null;

    /// <summary>A string, or the null literal where the reference publishes a design absence as null.</summary>
    private static string Text(string? value) => value is null ? VerbatimJson.Null : VerbatimJson.String(value);

    /// <summary>The design table, in the declaration order the reference publishes it in.</summary>
    private static string RowProfiles()
    {
        var rows = new List<(string Key, string Value)>();
        foreach (var (rowId, profile) in Model.RowProfiles.Declared)
        {
            rows.Add((rowId, VerbatimJson.Object(
                2,
                ("product", VerbatimJson.String(profile.Product)),
                ("configuration", VerbatimJson.String(profile.Config)),
                ("plan", VerbatimJson.String(profile.Plan)),
                ("plan_arms", VerbatimJson.StringArray(3, Model.RowProfiles.PlanArms[profile.Plan])),
                ("dual_phase", VerbatimJson.Boolean(profile.Dual)),
                ("udp53_carriage", VerbatimJson.String(profile.Udp53)),
                ("udp53_label", VerbatimJson.String(Model.RowProfiles.Udp53Label(profile.Udp53))),
                ("udp_carriage", VerbatimJson.String(profile.Udp)),
                ("udp_label", VerbatimJson.String(Model.RowProfiles.UdpLabel(profile.Udp))),
                ("udp_capable", VerbatimJson.Boolean(!string.Equals(profile.Udp, Model.RowProfiles.UdpNotCarried, StringComparison.Ordinal))))));
        }

        return VerbatimJson.Object(1, [.. rows]);
    }

    /// <summary>The finding counts per severity, in the order the severities are printed.</summary>
    private static string FindingsBySeverity(IReadOnlyList<Finding> findings)
    {
        var bySeverity = FindingsCollector.BySeverity(findings);
        return VerbatimJson.Object(
            1,
            [.. Severity.Order.Select(severity => (
                severity,
                VerbatimJson.Integer(bySeverity[severity].Count)))]);
    }
}
