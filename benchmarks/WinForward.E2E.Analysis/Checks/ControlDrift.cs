using System.Runtime.CompilerServices;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Checks;

/// <summary>One control metric's comparison across passes, as the drift check and §13 both read it.</summary>
/// <param name="Metric">The metric's label, as the reference spells it.</param>
/// <param name="Key">The metric's identifier.</param>
/// <param name="Kind">Ratio or diff, which decides the threshold band's shape.</param>
/// <param name="Unit">The unit the values were scaled to.</param>
/// <param name="Threshold">The pre-declared practical-significance threshold.</param>
/// <param name="Pre">The pre block's per-pass values, natural-key ordered.</param>
/// <param name="Post">The post block's per-pass values, natural-key ordered.</param>
/// <param name="Unavailable">Per pass, why a value is missing.</param>
/// <param name="Comparison">The bootstrap comparison, or null when it could not be drawn.</param>
/// <param name="Error">Why the comparison could not be drawn.</param>
/// <param name="Verdict">The practical-significance verdict.</param>
/// <param name="Reason">The verdict's own words.</param>
/// <param name="PassesUsed">How many passes both blocks contributed.</param>
/// <param name="Statement">The one-line statement the finding and the table both quote.</param>
internal sealed record ControlComparison(
    string Metric,
    string Key,
    string Kind,
    string Unit,
    double Threshold,
    IReadOnlyDictionary<string, double> Pre,
    IReadOnlyDictionary<string, double> Post,
    IReadOnlyDictionary<string, string> Unavailable,
    Comparison? Comparison,
    string? Error,
    string Verdict,
    string Reason,
    int PassesUsed,
    string Statement);

/// <summary>One pass's control-block presence and ordering.</summary>
/// <param name="Pass">The pass.</param>
/// <param name="PrePresent">Whether that pass ran the pre block.</param>
/// <param name="PostPresent">Whether that pass ran the post block.</param>
/// <param name="Ordering">Why the product block is not bracketed, or null when it is.</param>
internal sealed record ControlPass(string Pass, bool PrePresent, bool PostPresent, string? Ordering);

/// <summary>What the campaign's control blocks say: whether they are usable, what each pass ran, and the drift.</summary>
/// <param name="Available">Whether any pass ran both blocks.</param>
/// <param name="Comparisons">One entry per control metric.</param>
/// <param name="PerPass">One entry per pass.</param>
internal sealed record ControlDriftResult(
    bool Available,
    IReadOnlyList<ControlComparison> Comparisons,
    IReadOnlyList<ControlPass> PerPass);

/// <summary>
/// The control-block checks: whether the two blocks bracket the product block inside each pass, and
/// whether the block that runs after the products still measures the same path as the one that ran before.
/// </summary>
/// <remarks>
/// <para><b>The post block is the only instrument that can detect a product that left a driver filtering
/// after it exited.</b> It runs the same path-quality metrics as the pre block, so a difference between
/// them is a finding rather than noise to average away.</para>
/// <para><b>Five metrics, two threshold families.</b> Each comparison is a bootstrap over passes with a
/// seed derived from the metric's own key, so two runs of the analysis draw the same intervals.</para>
/// </remarks>
internal static class ControlDrift
{
    private const string DiffKind = "diff";

    private const string RatioKind = "ratio";

    private const double LossThreshold = 0.5;

    private const double LatencyThreshold = 0.05;

    private static readonly ConditionalWeakTable<CampaignModel, ControlDriftResult> s_cache = [];

    /// <summary>Whether the two blocks bracket the product block inside one pass, or why they do not.</summary>
    internal static string? Ordering(CampaignModel campaign, string passId)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var rows = campaign.InPass(passId);
        var pre = campaign.InPass(passId, RowProfiles.ControlPre);
        var post = campaign.InPass(passId, RowProfiles.ControlPost);
        if (pre is null || post is null)
        {
            return null;
        }

        var preStart = RunClocks.ParseUtc(pre.Document, Contracts.ArmKeys.Run.StartedUtc);
        var postStart = RunClocks.ParseUtc(post.Document, Contracts.ArmKeys.Run.StartedUtc);
        if (preStart is null || postStart is null)
        {
            return null;
        }

        var problems = new List<string>();
        foreach (var row in rows)
        {
            if (CampaignQueries.IsControl(row.RunId))
            {
                continue;
            }

            var start = RunClocks.ParseUtc(row.Document, Contracts.ArmKeys.Run.StartedUtc);
            if (start is null)
            {
                problems.Add($"{row.RunId} has no startedUtc");
                continue;
            }

            if (start < preStart)
            {
                problems.Add($"{row.RunId} started before {RowProfiles.ControlPre}");
            }

            if (start > postStart)
            {
                problems.Add($"{row.RunId} started after {RowProfiles.ControlPost}");
            }
        }

        return problems.Count > 0 ? "the control blocks do not bracket the product block: " + string.Join("; ", problems) : null;
    }

    /// <summary>Every control metric's comparison across the campaign's passes.</summary>
    internal static ControlDriftResult Compute(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return s_cache.GetValue(campaign, Build);
    }

    private static ControlDriftResult Build(CampaignModel campaign)
    {
        var perPass = campaign.PassIds
            .Select(passId => new ControlPass(
                passId,
                campaign.InPass(passId, RowProfiles.ControlPre) is not null,
                campaign.InPass(passId, RowProfiles.ControlPost) is not null,
                Ordering(campaign, passId)))
            .ToList();

        var available = perPass.Exists(entry => entry is { PrePresent: true, PostPresent: true });
        if (!available)
        {
            return new ControlDriftResult(Available: false, Comparisons: [], PerPass: perPass);
        }

        var comparisons = new List<ControlComparison>();
        foreach (var metric in Metrics())
        {
            comparisons.Add(Compare(campaign, metric));
        }

        return new ControlDriftResult(Available: true, Comparisons: comparisons, PerPass: perPass);
    }

    private static ControlComparison Compare(CampaignModel campaign, ControlMetric metric)
    {
        var threshold = string.Equals(metric.Kind, DiffKind, StringComparison.Ordinal) ? LossThreshold : LatencyThreshold;
        var pre = new Dictionary<string, double>(StringComparer.Ordinal);
        var post = new Dictionary<string, double>(StringComparer.Ordinal);
        var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var passId in campaign.PassIds)
        {
            var preRun = campaign.InPass(passId, RowProfiles.ControlPre);
            var postRun = campaign.InPass(passId, RowProfiles.ControlPost);
            if (preRun is null || postRun is null)
            {
                unavailable[passId] = "a control block is missing in this pass";
                continue;
            }

            var (preValue, preWhy) = metric.Extract(preRun);
            if (preValue is null)
            {
                unavailable[passId] = $"pre: {preWhy}";
            }
            else
            {
                pre[passId] = preValue.Value * metric.Scale;
            }

            var (postValue, postWhy) = metric.Extract(postRun);
            if (postValue is null)
            {
                unavailable[passId] = $"post: {postWhy}";
            }
            else
            {
                post[passId] = postValue.Value * metric.Scale;
            }
        }

        var seed = ComparisonSeed.DeriveSeed(campaign.Seed, $"control|{metric.Key}");
        var (comparison, error) = BootstrapPair.Draw(post, pre, metric.Kind, threshold, campaign.Resamples, seed);
        var passesUsed = pre.Keys.Count(post.ContainsKey);
        var (verdict, reason) = comparison is null
            ? ("inconclusive", error ?? "one side has no per-pass value for this metric")
            : BootstrapPair.Decide(metric.Kind, threshold, comparison, passesUsed, campaign.MinPasses);

        return new ControlComparison(
            metric.Label,
            metric.Key,
            metric.Kind,
            metric.Unit,
            threshold,
            NaturalOrder(pre),
            NaturalOrder(post),
            NaturalOrder(unavailable),
            comparison,
            error,
            verdict,
            reason,
            passesUsed,
            Statement(metric, comparison, error, verdict, reason));
    }

    private static string Statement(
        ControlMetric metric,
        Comparison? comparison,
        string? error,
        string verdict,
        string reason)
    {
        if (comparison is null)
        {
            return $"n/a ({error})";
        }

        var text = string.Equals(metric.Kind, RatioKind, StringComparison.Ordinal)
            ? $"post/pre = {VerbatimNumber.Cell(comparison.Estimate, 4)}"
            : $"post-pre = {VerbatimNumber.Cell(comparison.Estimate, 4)} pp";
        text += $" (95 % CI {VerbatimNumber.Cell(comparison.CiLow, 4)}–{VerbatimNumber.Cell(comparison.CiHigh, 4)}";
        text += string.Equals(metric.Kind, RatioKind, StringComparison.Ordinal) ? ")" : " pp)";
        var tail = string.Equals(verdict, "inconclusive", StringComparison.Ordinal)
            ? $"inconclusive ({reason})"
            : verdict;
        return $"{text} -> {tail}";
    }

    private static Dictionary<string, double> NaturalOrder(Dictionary<string, double> values)
    {
        var ordered = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var key in NaturalKey.Sort(values.Keys))
        {
            ordered[key] = values[key];
        }

        return ordered;
    }

    private static Dictionary<string, string> NaturalOrder(Dictionary<string, string> values)
    {
        var ordered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in values.Keys.Order(StringComparer.Ordinal))
        {
            ordered[key] = values[key];
        }

        return ordered;
    }

    private static List<ControlMetric> Metrics() =>
    [
        new("loss.lossRate", "BASE loss lossRate", DiffKind, "pp", 100.0,
            run => ArmAccess.Number(run, "BASE", "metrics/loss/lossRate")),
        new("lat.tcp_rtt.p50", "BASE latency tcp-rtt p50", RatioKind, "us", 1.0,
            run => ArmAccess.Latency(run, "BASE", "tcp-rtt", "p50Us")),
        new("lat.tcp_rtt.p99", "BASE latency tcp-rtt p99", RatioKind, "us", 1.0,
            run => ArmAccess.Latency(run, "BASE", "tcp-rtt", "p99Us")),
        new("lat.udp_rtt.p50", "BASE latency udp-rtt p50", RatioKind, "us", 1.0,
            run => ArmAccess.Latency(run, "BASE", "udp-rtt", "p50Us")),
        new("lat.udp_lossRate", "BASE latency udp lossRate", DiffKind, "pp", 100.0,
            run => ArmAccess.Number(run, "BASE", "metrics/latency/udp.lossRate")),
    ];

    private sealed record ControlMetric(
        string Key,
        string Label,
        string Kind,
        string Unit,
        double Scale,
        Func<ClientRun, Measured<double?>> Extract);
}
