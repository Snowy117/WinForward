using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Metrics;

/// <summary>
/// The metrics the headline matrix and <c>verdict.json</c>'s <c>metrics</c> object are built from: each
/// one's identifier, label, unit, rounding, practical-significance family, the definition the report
/// publishes, and the per-pass extraction that reads it out of an arm's records.
/// </summary>
/// <remarks>
/// <para><b>The extractors are the one verdict slice the oracle splits in two.</b> <c>oracle-diff.py</c>
/// owns the latency, UDP, DNS, MIX-loss and goodput extractors with §5/§8/§9 and the CPU, memory,
/// PERSIST and reliability ones with §4/§6/§7/§10/§11, which is why it slices <c>metrics</c> by member
/// name instead of by key.</para>
/// <para><b>§4 prints every metric column.</b> The headline matrix is one table over all the metrics, and
/// it is compared behind the sections that render the other fifteen columns, so no section has to wait on
/// one that is still to come.</para>
/// <para><b>The rate metrics are percentages, not fractions.</b> The harness publishes a rate as a
/// fraction of its denominator and the metric's unit is percentage points, so the scale is applied
/// once, here, and never again by a renderer.</para>
/// <para><b>The MIX loss rate's fallback is guarded.</b> When the UDP class publishes no loss rate the
/// arm-level field is read instead — but not when the class field came back as a JSON null, because a
/// null is the harness saying the denominator was zero and the fallback would replace a true "nothing
/// was sent" with a number about a different counter.</para>
/// <para><b>Two metrics are read from samples rather than from an arm.</b> The steady-state private
/// bytes and the row's proxy CPU carry no arm, so their extraction takes the campaign with the row: the
/// first needs the warmup window, the second needs the loaded arms the row's CPU is summed over. Both
/// answer the reference's own reason when the run sampled no product process at all, which is what the
/// two control blocks are.</para>
/// </remarks>
internal static class MetricCatalogue
{
    private const string LatencyFamily = "latency";

    private const string UdpLossFamily = "udp-loss";

    private const string TcpUnexpectedFamily = "tcp-unexpected";

    private const string MemoryFamily = "memory";

    private const string CpuFamily = "cpu";

    private const string NoFamily = "none";

    /// <summary>The arm that loads nothing, which no CPU cell of a row's own total may include.</summary>
    private const string Idle = "IDLE";

    /// <summary>The reason a sampling metric has no value when the run matched no product process.</summary>
    private const string NoProductProcess = "no product process was sampled (run.json samplerProcesses is empty)";

    private const string CountUnit = "count";

    private static readonly string[] s_percentiles = ["minUs", "meanUs", "p50Us", "p90Us", "p99Us", "p999Us", "maxUs"];

    private static readonly List<MetricSpec> s_specs =
    [
        new("lat.tcp_rtt.p50", "LAT tcp-rtt p50", "us", 1, LatencyFamily, "LAT arm, latency.tcp-rtt.p50Us",
            Arm: "LAT", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LAT", "tcp-rtt", "p50Us")),
        new("lat.tcp_rtt.p99", "LAT tcp-rtt p99", "us", 1, LatencyFamily, "LAT arm, latency.tcp-rtt.p99Us",
            Arm: "LAT", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LAT", "tcp-rtt", "p99Us")),
        new("lat.udp_rtt.p50", "LAT udp-rtt p50", "us", 1, LatencyFamily,
            "LAT arm UDP lane, latency.udp-rtt.p50Us (proxied only where the row carries UDP)",
            Arm: "LAT", UdpPath: "udp", Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LAT", "udp-rtt", "p50Us")),
        new("lat.udp_lossRate", "LAT udp lossRate", "pp", 4, UdpLossFamily,
            "LAT arm UDP lane, metrics.udp.lossRate (percentage points of udp.sent)",
            Arm: "LAT", UdpPath: "udp", Dns53: false, Extract: (_, row) => Percent(ArmAccess.Number(row, "LAT", "metrics/udp.lossRate"))),
        new("latload.tcp_rtt.p50", "LATLOAD tcp-rtt p50", "us", 1, LatencyFamily, "LATLOAD arm, latency.tcp-rtt.p50Us",
            Arm: "LATLOAD", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LATLOAD", "tcp-rtt", "p50Us")),
        new("latload.tcp_rtt.p99", "LATLOAD tcp-rtt p99", "us", 1, LatencyFamily, "LATLOAD arm, latency.tcp-rtt.p99Us",
            Arm: "LATLOAD", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "LATLOAD", "tcp-rtt", "p99Us")),
        new("loss.lossRate", "LOSS lossRate", "pp", 4, UdpLossFamily,
            "LOSS arm, metrics.lossRate = (late + never) / sent (percentage points)",
            Arm: "LOSS", UdpPath: "udp", Dns53: false, Extract: (_, row) => Percent(ArmAccess.Number(row, "LOSS", "metrics/lossRate"))),
        new("loss.corruptRate", "LOSS corruptRate", "pp", 4, UdpLossFamily,
            "LOSS arm, metrics.corruptRate (percentage points)",
            Arm: "LOSS", UdpPath: "udp", Dns53: false, Extract: (_, row) => Percent(ArmAccess.Number(row, "LOSS", "metrics/corruptRate"))),
        new("loss.foreignConnection", "LOSS foreignConnection", CountUnit, 0, NoFamily,
            "LOSS arm, metrics.foreignConnection: datagrams delivered into a different flow",
            Arm: "LOSS", UdpPath: "udp", Dns53: false, Extract: (_, row) => ArmAccess.Number(row, "LOSS", "metrics/foreignConnection")),
        new("rel.unexpectedEofRate", "REL unexpectedEofRate", "pp", 4, TcpUnexpectedFamily,
            "REL metrics.unexpectedEof / metrics.connectAttempts (percentage points)",
            Arm: "REL", UdpPath: null, Dns53: false,
            Extract: (_, row) => Percent(ArmAccess.Ratio(row, "REL", "metrics/unexpectedEof", "metrics/connectAttempts"))),
        new("rel.fidelityRate", "REL fidelityRate", "pp", 4, TcpUnexpectedFamily,
            "REL metrics.fidelityMismatch / metrics.connectAttempts (percentage points)",
            Arm: "REL", UdpPath: null, Dns53: false,
            Extract: (_, row) => Percent(ArmAccess.Ratio(row, "REL", "metrics/fidelityMismatch", "metrics/connectAttempts"))),
        new("dns.answerRate", "DNS(53) answerRate", "pp", 4, UdpLossFamily,
            "port-53 DNS arm, metrics.answerRate (percentage points); carriage differs per row",
            Arm: "DNS", UdpPath: "dns", Dns53: true, Extract: (_, row) => Percent(ArmAccess.Number(row, "DNS", "metrics/answerRate"))),
        new("dns.rtt.p50", "DNS(53) dns-rtt p50", "us", 1, LatencyFamily,
            "port-53 DNS arm, latency.dns-rtt.p50Us; carriage differs per row",
            Arm: "DNS", UdpPath: "dns", Dns53: true, Extract: (_, row) => ArmAccess.Latency(row, "DNS", "dns-rtt", "p50Us")),
        new("dnsalt.answerRate", "DNSALT answerRate", "pp", 4, UdpLossFamily,
            "DNSALT arm (a port no product special-cases), metrics.answerRate (percentage points)",
            Arm: "DNSALT", UdpPath: "dns", Dns53: false, Extract: (_, row) => Percent(ArmAccess.Number(row, "DNSALT", "metrics/answerRate"))),
        new("dnsalt.rtt.p50", "DNSALT dns-rtt p50", "us", 1, LatencyFamily,
            "DNSALT arm (a port no product special-cases), latency.dns-rtt.p50Us",
            Arm: "DNSALT", UdpPath: "dns", Dns53: false, Extract: (_, row) => ArmAccess.Latency(row, "DNSALT", "dns-rtt", "p50Us")),
        new("thru.goodputMbps", "THRU goodputMbps", "Mbps", 3, NoFamily, "THRU metrics.goodputMbps",
            Arm: "THRU", UdpPath: null, Dns53: false, Extract: (_, row) => ArmAccess.Number(row, "THRU", "metrics/goodputMbps")),
        new("mix.udp.lossRate", "MIX udp.lossRate", "pp", 4, UdpLossFamily,
            "MIX metrics.classes.udp.lossRate (percentage points)",
            Arm: "MIX", UdpPath: "udp", Dns53: false, Extract: (_, row) => MixUdpLossRate(row)),
        new("persist.responseRate", "PERSIST responseRate", "pp", 4, TcpUnexpectedFamily,
            "PERSIST metrics.responseRate = responses / requests (percentage points)",
            Arm: "PERSIST", UdpPath: null, Dns53: false,
            Extract: (_, row) => Percent(ArmAccess.Number(row, "PERSIST", "metrics/responseRate"))),
        new("persist.reconnects", "PERSIST reconnects", CountUnit, 1, NoFamily,
            "PERSIST metrics.reconnects: a long-lived connection that had to be replaced",
            Arm: "PERSIST", UdpPath: null, Dns53: false,
            Extract: (_, row) => ArmAccess.Number(row, "PERSIST", "metrics/reconnects")),
        new("mem.privateBytes.p50", "steady-state private bytes", "MiB", 2, MemoryFamily,
            "per-pass p50 of the product's steady-state privateBytes, then median across passes",
            Arm: null, UdpPath: null, Dns53: false, Extract: SteadyPrivateBytes),
        new("cpu.proxy.vcpuPct", "proxy CPU", "%vcpu", 2, CpuFamily,
            "row proxy CPU over the loaded arms (IDLE excluded), percent of one vCPU",
            Arm: null, UdpPath: null, Dns53: false, Extract: (_, row) => ProxyCpu(row)),
    ];

    /// <summary>Every metric the analysis publishes, in the reference's declaration order.</summary>
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

    /// <summary>
    /// The same cell rendered from its declaration, which is what a section that prints every metric —
    /// §4 is the only one — needs: the rounding and the unit are the metric's own, not the section's.
    /// </summary>
    /// <param name="cell">The per-pass readings.</param>
    /// <param name="spec">The metric the cell was read for.</param>
    /// <returns>The cell's text.</returns>
    internal static string CellText(MetricCell cell, MetricSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return CellText(cell, spec.Digits, spec.Unit);
    }

    /// <summary>The same reading as percentage points, which is the unit every rate metric uses.</summary>
    private static Measured<double?> Percent(Measured<double?> reading) =>
        reading.Value is { } value ? new(Value: value * 100.0, Reason: null) : reading;

    /// <summary>The MIX arm's UDP loss rate, from its UDP class with the arm-level fallback.</summary>
    private static Measured<double?> MixUdpLossRate(ClientRun row)
    {
        var reading = ArmAccess.Number(row, "MIX", "metrics/classes/udp/lossRate");
        if (reading.Value is null && !string.Equals(reading.Reason, ArmAccess.NullRateReason, StringComparison.Ordinal))
        {
            reading = ArmAccess.Number(row, "MIX", "metrics/udp.lossRate");
        }

        return Percent(reading);
    }

    /// <summary>
    /// The product's steady-state private bytes: the per-pass p50 over every arm's post-warmup samples,
    /// which the headline matrix then medians across passes.
    /// </summary>
    /// <param name="campaign">The campaign the warmup window is read from.</param>
    /// <param name="row">The run to read.</param>
    private static Measured<double?> SteadyPrivateBytes(CampaignModel campaign, ClientRun row)
    {
        if (RunSamples.PrimaryProductProcess(row) is not { } primary)
        {
            return new(Value: null, Reason: NoProductProcess);
        }

        var values = new List<double>();
        foreach (var arm in row.Arms.All)
        {
            foreach (var sample in RunSamples.SteadySamples(row, arm.Name, primary, campaign.WarmupSeconds))
            {
                if (RunSamples.ProcessPrivateBytes(sample) is { } bytes)
                {
                    values.Add(bytes / RunSamples.Mebibyte);
                }
            }
        }

        return values.Count == 0
            ? new(Value: null, Reason: "no steady-state product samples")
            : new(Value: DescriptiveStats.Quantile(values, 0.50), Reason: null);
    }

    /// <summary>
    /// The row's proxy CPU: one percentage of a vCPU over every loaded arm except <c>IDLE</c>, read
    /// through the same per-identity accumulation §6 prints per arm.
    /// </summary>
    /// <param name="row">The run to read.</param>
    private static Measured<double?> ProxyCpu(ClientRun row)
    {
        if (RunSamples.PrimaryProductProcess(row) is not { } primary)
        {
            return new(Value: null, Reason: NoProductProcess);
        }

        var ordered = row.Arms.All
            .Where(arm => !string.Equals(arm.Name, Idle, StringComparison.Ordinal))
            .SelectMany(arm => RunSamples.PresentProductSamples(row, arm.Name, primary))
            .Where(sample => JsonValue.Number(sample, ArmKeys.Sample.Ticks) is not null)
            .OrderBy(sample => JsonValue.Number(sample, ArmKeys.Sample.Ticks)!.Value);
        var (value, why, _) = CpuDetail.Compute(
            [.. ordered],
            RunClocks.TickFrequency(row));
        return new(Value: value, Reason: why);
    }
}
