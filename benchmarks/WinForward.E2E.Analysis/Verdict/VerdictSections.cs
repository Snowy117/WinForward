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
/// </remarks>
internal static class VerdictSections
{
    /// <summary>The program name both implementations publish as their identity.</summary>
    private const string GeneratedBy = "e2e-analysis v1";

    /// <summary>Builds the keys the batches landed so far have rendered.</summary>
    internal static IReadOnlyDictionary<string, string> Build(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["generated_by"] = VerbatimJson.String(GeneratedBy),
            ["raw"] = VerbatimJson.String(campaign.Raw),
            ["flat_mode"] = campaign.Flat ? "true" : "false",
            ["passes"] = VerbatimJson.StringArray(campaign.PassIds),
            ["rows"] = VerbatimJson.StringArray(campaign.RowIds),
        };
    }
}
