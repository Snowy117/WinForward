using WinForward.E2E.Analysis.Json;
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
/// cannot be mistaken for one that renders as nothing (D20.2).</para>
/// <para><b>The identity string names the analysis, not this repository.</b> The reference used to
/// publish its own path here; both sides now write the neutral program name, so the two files can be
/// compared at all (D6.4).</para>
/// <para><b><c>bootstrap</c> and <c>thresholds</c> are the run's parameters, published.</b> They are
/// unconditional — the reference emits them for every campaign, including one whose comparisons all
/// come out <c>inconclusive</c> — and they state what the intervals below them were drawn with rather
/// than what any of them came out to, which is why they belong to the same batch as the generator
/// (D20.5). The thresholds text is the reference's own prose, down to its en dash and its asterisks:
/// it is compared as text, and its <c>'</c> characters are deliberately not escaped by
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

    /// <summary>Builds the keys the batches landed so far have rendered.</summary>
    internal static IReadOnlyDictionary<string, string> Build(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["generated_by"] = VerbatimJson.String(GeneratedBy),
            ["raw"] = VerbatimJson.String(campaign.Raw),
            ["flat_mode"] = VerbatimJson.Boolean(campaign.Flat),
            ["passes"] = VerbatimJson.StringArray(1, campaign.PassIds),
            ["rows"] = VerbatimJson.StringArray(1, campaign.RowIds),
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
        };
    }
}
