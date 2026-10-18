using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// Decides whether one reference-vs-row pair belongs to a metric's pre-registered family: the row has to
/// be a declared opponent, both sides have to have run the metric, and the traffic path has to be
/// comparable.
/// </summary>
/// <remarks>
/// <b>A design absence is not a data gap.</b> A product that cannot carry UDP is outside the family by
/// its row profile rather than by a missing value, while a row that ran the arm and produced no value is
/// still in the family and is reported as unevaluable. The distinction is what keeps the Proxifier row
/// out of the loss family without silently shrinking the family for a product that merely had a bad pass.
/// </remarks>
internal static class ReleasePairEligibility
{
    /// <summary>Why one pair cannot enter the family, or null when it can.</summary>
    internal static string? Ineligibility(
        MetricSpec spec,
        PrimaryMetric? primary,
        string referenceRow,
        ReleaseRowReadings? reference,
        ReleaseRowReadings row,
        bool declaredOpponent)
    {
        if (primary is null)
        {
            return "exploratory metric: no decision family";
        }

        if (!declaredOpponent)
        {
            return "row is not a declared opponent: internal and extra rows are exploratory";
        }

        if (reference is null)
        {
            return $"the reference row '{referenceRow}' was not measured";
        }

        if (string.Equals(row.RowId, referenceRow, StringComparison.Ordinal))
        {
            return "the row is the reference row";
        }

        var referenceReadings = Reading(reference, spec.Key);
        var rowReadings = Reading(row, spec.Key);
        if (referenceReadings is null)
        {
            return $"the reference row was not read for {spec.Key}";
        }

        if (rowReadings is null)
        {
            return $"the row was not read for {spec.Key}";
        }

        if (!IsDefined(referenceReadings))
        {
            return $"the reference row did not run the metric: {StatusText(referenceReadings)}";
        }

        return !IsDefined(rowReadings)
            ? $"the row did not run the metric: {StatusText(rowReadings)}"
            : CarriageExclusion(spec, referenceRow, row.RowId);
    }

    /// <summary>The traffic-path rule that keeps a product which cannot carry UDP out of a UDP family.</summary>
    private static string? CarriageExclusion(MetricSpec spec, string referenceRow, string rowId)
    {
        if (spec.UdpPath is null)
        {
            return null;
        }

        var referenceProfile = RowProfiles.Find(referenceRow);
        var rowProfile = RowProfiles.Find(rowId);
        if (rowProfile is not null
            && string.Equals(rowProfile.Udp, RowProfiles.UdpNotCarried, StringComparison.Ordinal))
        {
            return $"'{rowId}' does not carry UDP (UDP bypassed), so it is outside the {spec.Key} family";
        }

        if (referenceProfile is not null
            && string.Equals(referenceProfile.Udp, RowProfiles.UdpNotCarried, StringComparison.Ordinal))
        {
            return $"the reference row '{referenceRow}' does not carry UDP (UDP bypassed)";
        }

        if (spec.Dns53
            && referenceProfile is not null
            && rowProfile is not null
            && !string.Equals(referenceProfile.Udp53, rowProfile.Udp53, StringComparison.Ordinal))
        {
            return string.Concat(
                "the port-53 UDP carriage differs (",
                RowProfiles.Udp53Label(referenceProfile.Udp53),
                " vs ",
                RowProfiles.Udp53Label(rowProfile.Udp53),
                "), so the pair is outside the ",
                spec.Key,
                " family");
        }

        return null;
    }

    /// <summary>One row's readings for one metric, or null when the metric was not read for it.</summary>
    internal static ReleaseMetricReadings? Reading(ReleaseRowReadings? row, string metric) =>
        row is not null && row.Metrics.TryGetValue(metric, out var readings) ? readings : null;

    /// <summary>The band unit a pair is judged in: the declared primary kind, or the metric's own unit.</summary>
    internal static string KindFor(MetricSpec spec, PrimaryMetric? primary) =>
        primary?.Kind
        ?? (string.Equals(spec.Unit, "pp", StringComparison.Ordinal)
            ? ThresholdKind.PercentagePoints
            : ThresholdKind.Ratio);

    private static bool IsDefined(ReleaseMetricReadings readings) =>
        readings.Status is ReleaseReadingStatus.Ok or ReleaseReadingStatus.DnsCarriage;

    private static string StatusText(ReleaseMetricReadings readings) =>
        readings.StatusReason ?? readings.Status;
}
