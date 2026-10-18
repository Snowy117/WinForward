using System.Globalization;
using WinForward.E2E.Analysis.Cli;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// Reads the legacy metric cells for one release run and keeps only the passes the inputs measured.
/// </summary>
/// <remarks>
/// <b>The extraction is the catalogue's, not a copy.</b> Every metric comes from
/// <see cref="MetricCatalogue.PerPassValues"/>, so a release comparison cannot drift from the numbers
/// <c>tables.md</c> and <c>verdict.json</c> publish. The reader's only selection is the pass filter: the
/// discarded warm-up pass is removed before a value is stored, which is the one difference between the
/// release analysis unit and the legacy one.
/// </remarks>
internal static class ReleaseDatasetReader
{
    private const string ReliabilityMetric = "rel.unexpectedEofRate";

    private const string ReliabilityArm = "REL";

    private const string UnexpectedEofPath = "metrics/unexpectedEof";

    private const string ConnectAttemptsPath = "metrics/connectAttempts";

    /// <summary>Loads the campaign tree and reads the measured passes into a dataset.</summary>
    internal static bool TryRead(
        string campaignRoot,
        ReleaseInputs inputs,
        int resamples,
        int seed,
        out ReleaseDataset? dataset,
        out string? error)
    {
        dataset = null;
        var options = AnalysisOptions.ForReleaseDataset(campaignRoot, inputs.WarmupSeconds, resamples, seed);
        if (!CampaignLoader.TryLoad(options, out var campaign, out error) || campaign is null)
        {
            error = $"release: {error ?? "the campaign tree could not be loaded"}";
            return false;
        }

        return TryBuild(campaign, inputs, out dataset, out error);
    }

    /// <summary>Reads one already-loaded campaign into a dataset.</summary>
    internal static bool TryBuild(
        CampaignModel campaign,
        ReleaseInputs inputs,
        out ReleaseDataset? dataset,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(inputs);

        dataset = null;
        if (!TryMeasuredPasses(campaign, inputs, out var passIds, out error))
        {
            return false;
        }

        var rows = new List<ReleaseRowReadings>(campaign.RowIds.Count);
        foreach (var rowId in campaign.RowIds)
        {
            var metrics = new Dictionary<string, ReleaseMetricReadings>(StringComparer.Ordinal);
            foreach (var spec in MetricCatalogue.Specs)
            {
                var cell = MetricCatalogue.PerPassValues(campaign, spec, rowId);
                var readings = ReadCell(cell, passIds);
                if (string.Equals(spec.Key, ReliabilityMetric, StringComparison.Ordinal))
                {
                    readings = readings with { Counts = ReadCounts(campaign, rowId, passIds) };
                }

                metrics[spec.Key] = readings;
            }

            rows.Add(new ReleaseRowReadings(rowId, metrics));
        }

        dataset = new ReleaseDataset
        {
            MeasuredPasses = passIds,
            Rows = rows,
            Resamples = campaign.Resamples,
            Seed = campaign.Seed,
        };
        error = null;
        return true;
    }

    /// <summary>
    /// Maps the inputs' measured pass numbers onto the campaign's own pass ids, refusing a number the
    /// tree does not hold or a tree with two directories for one number.
    /// </summary>
    private static bool TryMeasuredPasses(
        CampaignModel campaign,
        ReleaseInputs inputs,
        out IReadOnlyList<string> passIds,
        out string? error)
    {
        var byNumber = new Dictionary<int, string>();
        foreach (var passId in campaign.PassIds)
        {
            if (!ReleasePasses.TryNumber(passId, out var number))
            {
                error = $"release: '{passId}' is not a numbered pass directory (pass<N>)";
                passIds = [];
                return false;
            }

            if (!byNumber.TryAdd(number, passId))
            {
                error = string.Create(CultureInfo.InvariantCulture, $"release: pass {number} is listed twice in the campaign tree");
                passIds = [];
                return false;
            }
        }

        var measured = new List<string>(inputs.Passes.Measured.Count);
        foreach (var number in inputs.Passes.Measured)
        {
            if (!byNumber.TryGetValue(number, out var passId))
            {
                error = string.Create(CultureInfo.InvariantCulture, $"release: the campaign tree has no pass {number}, which the release inputs measured");
                passIds = [];
                return false;
            }

            measured.Add(passId);
        }

        passIds = NaturalKey.Sort(measured);
        error = null;
        return true;
    }

    /// <summary>One metric cell's measured-pass slice, with the pass-level reasons kept beside it.</summary>
    private static ReleaseMetricReadings ReadCell(MetricCell cell, IReadOnlyList<string> passIds)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var defined = cell.Status is MetricStatus.Ok or MetricStatus.DnsCarriage;
        foreach (var passId in passIds)
        {
            if (cell.Values.TryGetValue(passId, out var value))
            {
                values[passId] = value;
                continue;
            }

            if (defined)
            {
                reasons[passId] = cell.Reasons.GetValueOrDefault(passId, "no value was read");
            }
        }

        return new ReleaseMetricReadings
        {
            Values = values,
            Reasons = reasons,
            Status = cell.Status,
            StatusReason = cell.StatusReason,
            MissingPasses = passIds.Count - values.Count,
        };
    }

    /// <summary>
    /// The REL arm's per-pass events and exposure. A pass whose two counters cannot both be read is
    /// omitted, and the capability gate reports the missing exposure rather than assuming a zero.
    /// </summary>
    private static Dictionary<string, ReleaseCount> ReadCounts(
        CampaignModel campaign,
        string rowId,
        IReadOnlyList<string> passIds)
    {
        var counts = new Dictionary<string, ReleaseCount>(StringComparer.Ordinal);
        foreach (var passId in passIds)
        {
            var run = campaign.InPass(passId, rowId);
            if (run is null)
            {
                continue;
            }

            var events = ArmAccess.Number(run, ReliabilityArm, UnexpectedEofPath);
            var attempts = ArmAccess.Number(run, ReliabilityArm, ConnectAttemptsPath);
            if (events.Value is not { } numerator
                || attempts.Value is not { } denominator
                || !TryCount(numerator, out var eventCount)
                || !TryCount(denominator, out var attemptCount))
            {
                continue;
            }

            counts[passId] = new ReleaseCount(eventCount, attemptCount);
        }

        return counts;
    }

    /// <summary>Whether a published counter is a non-negative integer a long can hold.</summary>
    private static bool TryCount(double value, out long count)
    {
        count = 0;
        if (!double.IsFinite(value) || value < 0.0 || value > long.MaxValue)
        {
            return false;
        }

        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (Math.Abs(value - rounded) > 1e-9)
        {
            return false;
        }

        count = (long)rounded;
        return true;
    }
}
