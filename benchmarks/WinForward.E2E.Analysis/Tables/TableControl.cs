using WinForward.E2E.Analysis.Checks;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// The two <c>BASE</c> blocks' path-quality metrics, bootstrapped across passes, and each pass's
/// statement of whether the blocks bracketed the product block.
/// </summary>
/// <remarks>
/// <para><b>The post block is the only instrument that can detect a product that left a driver filtering
/// after it exited</b>, so a difference between the blocks is reported as a finding rather than averaged
/// away, and the table prints the same statement the finding quotes.</para>
/// <para><b>Both blocks are needed in at least one pass.</b> A tree that ran neither says so and stops:
/// the per-pass table would be the only readable thing left, and it is not a comparison.</para>
/// </remarks>
internal static class TableControl
{
    private const string Bracketed = "control-pre precedes and control-post follows every product row";

    private const string Present = "present";

    private const string Missing = "MISSING";

    private const string PreHeld = "control-pre present";

    private const string PreAbsent = "no control-pre";

    private const string PostHeld = "control-post present";

    private const string PostAbsent = "no control-post";

    private static readonly string[] s_headers =
    [
        "metric (BASE arm)",
        "pre",
        "post",
        "pre median",
        "post median",
        "post vs pre",
        "verdict",
    ];

    private static readonly string[] s_orderHeaders = ["pass", "control-pre", "control-post", "bracketing"];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign both blocks are read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var drift = ControlDrift.Compute(campaign);
        if (!drift.Available)
        {
            return string.Join('\n', Unavailable(drift.PerPass), string.Empty);
        }

        var lines = new List<string>
        {
            "The two control blocks bracket the product block inside each pass and run `BASE` only. Because the "
            + "post block runs after every product has exited, it is the only instrument in the campaign that can "
            + "detect a product that left a driver filtering after it exited: a difference between the blocks is "
            + "reported as a finding. The comparison bootstraps **passes** with the same pre-declared thresholds as "
            + "the rest of the analysis (5 % for latency ratios, 0.5 pp for loss differences).",
            string.Empty,
            MarkdownTable.Render(s_headers, [.. drift.Comparisons.Select(Line)]),
            string.Empty,
            "### Per-pass control blocks and bracketing",
            string.Empty,
            MarkdownTable.Render(s_orderHeaders, [.. drift.PerPass.Select(OrderLine)]),
            string.Empty,
        };

        return string.Join('\n', lines);
    }

    /// <summary>One metric's row: both blocks' readings, their medians, and the comparison's statement.</summary>
    private static List<string> Line(ControlComparison entry)
    {
        var unit = string.Equals(entry.Unit, "pp", StringComparison.Ordinal) ? string.Empty : " " + entry.Unit;
        var pre = Values(entry.Pre);
        var post = Values(entry.Post);
        return
        [
            entry.Metric,
            Stat(pre, unit),
            Stat(post, unit),
            Median(pre, unit),
            Median(post, unit),
            entry.Statement,
            entry.Verdict,
        ];
    }

    /// <summary>One pass's row: whether each block ran, and whether the product block was bracketed.</summary>
    private static List<string> OrderLine(ControlPass entry) =>
    [
        entry.Pass,
        entry.PrePresent ? Present : Missing,
        entry.PostPresent ? Present : Missing,
        entry.Ordering ?? Bracketed,
    ];

    /// <summary>The one sentence a tree with no usable control pair gets, naming what each pass held.</summary>
    private static string Unavailable(IReadOnlyList<ControlPass> perPass)
    {
        var held = perPass
            .Select(entry => $"{entry.Pass}: "
                + $"{(entry.PrePresent ? PreHeld : PreAbsent)}, "
                + $"{(entry.PostPresent ? PostHeld : PostAbsent)}")
            .ToList();
        return "n/a (both control blocks are needed in at least one pass; the tree holds "
            + (held.Count > 0 ? string.Join(", ", held) : "no passes") + ")";
    }

    private static string Stat(List<double> values, string unit) =>
        values.Count > 0 ? DescriptiveStats.FmtStat(values, digits: 4, unit: unit) : "n/a";

    private static string Median(List<double> values, string unit) =>
        DescriptiveStats.Median(values) is { } median ? Json.VerbatimNumber.Cell(median, 4, unit) : "n/a";

    private static List<double> Values(IReadOnlyDictionary<string, double> readings) => [.. readings.Values];
}
