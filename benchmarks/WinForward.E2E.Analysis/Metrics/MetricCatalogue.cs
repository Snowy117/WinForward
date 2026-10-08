using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Metrics;

/// <summary>
/// The metrics the headline matrix and <c>verdict.json</c>'s <c>metrics</c> object are built from: each
/// one's identifier, label, unit, rounding, practical-significance family, the definition the report
/// publishes, and the per-pass extraction that reads it out of an arm's records.
/// </summary>
/// <remarks>
/// <para><b>Completed in batches 3 and 4, and the split is the oracle's.</b> The metric extractors are
/// the one verdict slice that belongs to two batches — the latency, UDP, DNS, MIX-loss and goodput
/// metrics land with §5/§8/§9 (batch 3) and the CPU, memory, PERSIST and reliability metrics with
/// §4/§6/§7/§10/§11 (batch 4) — which is why <c>oracle-diff.py</c> slices <c>metrics</c> by member
/// name instead of by key.</para>
/// <para><b>§4 prints every metric column.</b> The headline matrix is one table over all the metrics,
/// so it is a batch-4 section: by the time it is compared, the batch that renders the other fifteen
/// columns has already passed, and no section has to wait on a later batch.</para>
/// <para><b>The rate metrics are percentages, not fractions.</b> The harness publishes a rate as a
/// fraction of its denominator and the metric's unit is percentage points, so the scale is applied
/// once, here, and never again by a renderer.</para>
/// <para><b>The MIX loss rate's fallback is guarded.</b> When the UDP class publishes no loss rate the
/// arm-level field is read instead — but not when the class field came back as a JSON null, because a
/// null is the harness saying the denominator was zero and the fallback would replace a true "nothing
/// was sent" with a number about a different counter.</para>
/// </remarks>
internal static class MetricCatalogue
{
    /// <summary>
    /// What this file still owes, for the run summary a caller reads.
    /// </summary>
    /// <remarks>
    /// The six metrics batch 4 adds are <c>rel.unexpectedEofRate</c>, <c>rel.fidelityRate</c>,
    /// <c>persist.responseRate</c>, <c>persist.reconnects</c>, <c>mem.privateBytes.p50</c> and
    /// <c>cpu.proxy.vcpuPct</c>. They are absent from <see cref="Specs"/> rather than present with a
    /// placeholder extraction, so the oracle reports them as missing slices (exit code 2) instead of
    /// comparing a wrong number: <!-- TODO(batch 4) -->
    /// </remarks>
    internal const string Pending = "4 the six metric extractors the headline matrix and §6/§7/§10/§11 need";

    private const string LatencyFamily = "latency";

    private const string UdpLossFamily = "udp-loss";

    private const string NoFamily = "none";

    private const string CountUnit = "count";

    private static readonly string[] s_percentiles = ["minUs", "meanUs", "p50Us", "p90Us", "p99Us", "p999Us", "maxUs"];

    private static readonly List<MetricSpec> s_specs =
    [
        new("lat.tcp_rtt.p50", "LAT tcp-rtt p50", "us", LatencyFamily, "LAT arm, latency.tcp-rtt.p50Us",
            Arm: "LAT", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LAT", "tcp-rtt", "p50Us")),
        new("lat.tcp_rtt.p99", "LAT tcp-rtt p99", "us", LatencyFamily, "LAT arm, latency.tcp-rtt.p99Us",
            Arm: "LAT", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LAT", "tcp-rtt", "p99Us")),
        new("lat.udp_rtt.p50", "LAT udp-rtt p50", "us", LatencyFamily,
            "LAT arm UDP lane, latency.udp-rtt.p50Us (proxied only where the row carries UDP)",
            Arm: "LAT", UdpPath: "udp", Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LAT", "udp-rtt", "p50Us")),
        new("lat.udp_lossRate", "LAT udp lossRate", "pp", UdpLossFamily,
            "LAT arm UDP lane, metrics.udp.lossRate (percentage points of udp.sent)",
            Arm: "LAT", UdpPath: "udp", Dns53: false, Extract: (_, row) => Percent(ArmAccess.Number(row, "LAT", "metrics/udp.lossRate"))),
        new("latload.tcp_rtt.p50", "LATLOAD tcp-rtt p50", "us", LatencyFamily, "LATLOAD arm, latency.tcp-rtt.p50Us",
            Arm: "LATLOAD", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LATLOAD", "tcp-rtt", "p50Us")),
        new("latload.tcp_rtt.p99", "LATLOAD tcp-rtt p99", "us", LatencyFamily, "LATLOAD arm, latency.tcp-rtt.p99Us",
            Arm: "LATLOAD", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LATLOAD", "tcp-rtt", "p99Us")),
        new("loss.lossRate", "LOSS lossRate", "pp", UdpLossFamily,
            "LOSS arm, metrics.lossRate = (late + never) / sent (percentage points)",
            Arm: "LOSS", UdpPath: "udp", Dns53: false, Extract: (_, row) => Percent(ArmAccess.Number(row, "LOSS", "metrics/lossRate"))),
        new("loss.corruptRate", "LOSS corruptRate", "pp", UdpLossFamily,
            "LOSS arm, metrics.corruptRate (percentage points)",
            Arm: "LOSS", UdpPath: "udp", Dns53: false, Extract: (_, row) => Percent(ArmAccess.Number(row, "LOSS", "metrics/corruptRate"))),
        new("loss.foreignConnection", "LOSS foreignConnection", CountUnit, NoFamily,
            "LOSS arm, metrics.foreignConnection: datagrams delivered into a different flow",
            Arm: "LOSS", UdpPath: "udp", Dns53: false, Extract: (_, row) => ArmAccess.Number(row, "LOSS", "metrics/foreignConnection")),
        new("dns.answerRate", "DNS(53) answerRate", "pp", UdpLossFamily,
            "port-53 DNS arm, metrics.answerRate (percentage points); carriage differs per row",
            Arm: "DNS", UdpPath: "dns", Dns53: true, Extract: (_, row) => Percent(ArmAccess.Number(row, "DNS", "metrics/answerRate"))),
        new("dns.rtt.p50", "DNS(53) dns-rtt p50", "us", LatencyFamily,
            "port-53 DNS arm, latency.dns-rtt.p50Us; carriage differs per row",
            Arm: "DNS", UdpPath: "dns", Dns53: true, Extract: (_, row) => ArmAccess.Latency(row, "DNS", "dns-rtt", "p50Us")),
        new("dnsalt.answerRate", "DNSALT answerRate", "pp", UdpLossFamily,
            "DNSALT arm (a port no product special-cases), metrics.answerRate (percentage points)",
            Arm: "DNSALT", UdpPath: "dns", Dns53: false, Extract: (_, row) => Percent(ArmAccess.Number(row, "DNSALT", "metrics/answerRate"))),
        new("dnsalt.rtt.p50", "DNSALT dns-rtt p50", "us", LatencyFamily,
            "DNSALT arm (a port no product special-cases), latency.dns-rtt.p50Us",
            Arm: "DNSALT", UdpPath: "dns", Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "DNSALT", "dns-rtt", "p50Us")),
        new("thru.goodputMbps", "THRU goodputMbps", "Mbps", NoFamily, "THRU metrics.goodputMbps",
            Arm: "THRU", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Number(row, "THRU", "metrics/goodputMbps")),
        new("mix.udp.lossRate", "MIX udp.lossRate", "pp", UdpLossFamily,
            "MIX metrics.classes.udp.lossRate (percentage points)",
            Arm: "MIX", UdpPath: "udp", Dns53: false, Extract: (_, row) => MixUdpLossRate(row)),
    ];

    /// <summary>Every metric this batch publishes, in the reference's declaration order.</summary>
    internal static IReadOnlyList<MetricSpec> Specs => s_specs;

    /// <summary>The histogram statistics §5 prints, in the harness's own field order.</summary>
    internal static IReadOnlyList<string> Percentiles => s_percentiles;

    /// <summary>
    /// The per-pass readings of one metric on one row, with the status that says whether the absence of
    /// a value is a design statement, a gap, or a metric the row's traffic cannot support.
    /// </summary>
    /// <param name="campaign">The loaded campaign the passes are read from.</param>
    /// <param name="spec">The metric being read.</param>
    /// <param name="rowId">The row being read.</param>
    /// <returns>The cell, whose status is set even when no value was collected.</returns>
    internal static MetricCell PerPassValues(CampaignModel campaign, MetricSpec spec, string rowId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rowId);

        var (status, reason) = MetricStatus.Resolve(campaign, spec.Arm, spec.UdpPath, spec.Dns53, rowId);
        var cell = new MetricCell { Status = status, StatusReason = reason };
        if (status is MetricStatus.NotInPlan or MetricStatus.DeclaredAbsent or MetricStatus.NotCarried)
        {
            return cell;
        }

        foreach (var passId in campaign.PassIds)
        {
            var row = campaign.InPass(passId, rowId);
            if (row is null)
            {
                cell.Reasons[passId] = "row not measured in this pass";
                continue;
            }

            var (value, why) = spec.Extract(campaign, row);
            if (value is null)
            {
                cell.Reasons[passId] = why ?? "unavailable";
            }
            else
            {
                cell.Values[passId] = value.Value;
            }
        }

        return cell;
    }

    /// <summary>
    /// One cell as text from the rounding and unit alone, which is what a section that reads its own
    /// metric — §9's answer rate is one — needs to render it the same way.
    /// </summary>
    /// <param name="cell">The per-pass readings.</param>
    /// <param name="digits">How many digits to keep after the point.</param>
    /// <param name="unit">The unit, with <c>count</c> meaning no unit at all.</param>
    /// <param name="zeroBoundN">The denominator the rule of three bounds an all-zero cell with, or null.</param>
    /// <param name="boundScale">The factor that turns the bound into the cell's own unit, or null.</param>
    /// <returns>The cell's text.</returns>
    internal static string CellText(MetricCell cell, int digits, string unit, int? zeroBoundN = null, double? boundScale = null)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(unit);

        if (string.Equals(cell.Status, MetricStatus.NotCarried, StringComparison.Ordinal))
        {
            return RowProfiles.NotCarriedCell;
        }

        if (cell.Status is MetricStatus.NotInPlan or MetricStatus.DeclaredAbsent)
        {
            return $"n/a ({cell.StatusReason})";
        }

        var values = cell.SortedValues();
        if (values.Count > 0)
        {
            return DescriptiveStats.FmtStat(
                values,
                digits,
                string.Equals(unit, CountUnit, StringComparison.Ordinal) ? string.Empty : " " + unit,
                zeroBoundN,
                boundScale,
                cell.NullPasses,
                cell.Reasons.Count - cell.NullPasses);
        }

        return cell.NullPasses > 0 ? string.Empty : $"n/a ({cell.ReasonSummary()})";
    }

    /// <summary>The same reading as percentage points, which is the unit every rate metric uses.</summary>
    private static (double? Value, string? Reason) Percent((double? Value, string? Reason) reading) =>
        reading.Value is { } value ? (value * 100.0, null) : reading;

    /// <summary>The MIX arm's UDP loss rate, from its UDP class with the arm-level fallback.</summary>
    private static (double? Value, string? Reason) MixUdpLossRate(ClientRun row)
    {
        var reading = ArmAccess.Number(row, "MIX", "metrics/classes/udp/lossRate");
        if (reading.Value is null && !string.Equals(reading.Reason, ArmAccess.NullRateReason, StringComparison.Ordinal))
        {
            reading = ArmAccess.Number(row, "MIX", "metrics/udp.lossRate");
        }

        return Percent(reading);
    }
}
