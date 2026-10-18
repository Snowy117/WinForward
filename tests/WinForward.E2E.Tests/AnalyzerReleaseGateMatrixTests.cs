using WinForward.E2E.Analysis.Release;
using Xunit;
using static WinForward.E2E.Tests.ReleaseComparisonTestData;

namespace WinForward.E2E.Tests;

/// <summary>
/// The release gate matrix through its public seam: which gate stops which metric cell, and the two
/// campaign-level checks that must never become a global invalidation.
/// </summary>
public sealed class AnalyzerReleaseGateMatrixTests
{
    private const string Reference = "wf-aot-nativeudp";

    private const string Latency = "lat.tcp_rtt.p50";

    private const string Loss = "loss.lossRate";

    private const string Throughput = "thru.goodputMbps";

    private const string Cpu = "cpu.proxy.vcpuPct";

    private const string Memory = "mem.privateBytes.p50";

    private const string Pass = "pass1";

    private const string Row = "proxifyre";

    [Fact]
    public void WindowOverflowInvalidatesOnlyTheArmsLatencyCells()
    {
        var gates = Evaluate(Arm(Row, "LAT", windowOverflow: 3));

        Assert.True(gates.IsInvalidated(Latency, Row, Pass));
        Assert.True(gates.IsInvalidated("lat.tcp_rtt.p99", Row, Pass));
        Assert.True(gates.IsInvalidated("lat.udp_rtt.p50", Row, Pass));
        Assert.False(gates.IsInvalidated("lat.udp_lossRate", Row, Pass));
        Assert.False(gates.IsInvalidated(Loss, Row, Pass));
        Assert.False(gates.IsInvalidated(Cpu, Row, Pass));
        Assert.False(gates.IsInvalidated(Throughput, Row, Pass));
        Assert.All(gates.Invalidations, entry => Assert.Equal("windowOverflow", entry.Gate));
    }

    [Fact]
    public void ClientSendLossInvalidatesTheArmsLatencyAndLossCells()
    {
        var latencyArm = Evaluate(Arm(Row, "LAT", clientSendLoss: 1));
        Assert.True(latencyArm.IsInvalidated(Latency, Row, Pass));
        Assert.True(latencyArm.IsInvalidated("lat.udp_lossRate", Row, Pass));
        Assert.False(latencyArm.IsInvalidated(Loss, Row, Pass));

        var lossArm = Evaluate(Arm(Row, "LOSS", clientSendLoss: 2));
        Assert.True(lossArm.IsInvalidated(Loss, Row, Pass));
        Assert.True(lossArm.IsInvalidated("loss.corruptRate", Row, Pass));
        Assert.False(lossArm.IsInvalidated(Latency, Row, Pass));
        Assert.False(lossArm.IsInvalidated(Throughput, Row, Pass));
    }

    [Fact]
    public void AUdpIdentityMismatchInvalidatesLossAndThroughputForEveryPrefix()
    {
        var gates = Evaluate(
            Arm(Row, "LOSS", udpIdentity: "arrived(6001) ... != sent(6000)"),
            Arm(Row, "MIX", udpIdentity: "arrived(2401) ... != sent(2400)"));

        Assert.True(gates.IsInvalidated(Loss, Row, Pass));
        Assert.True(gates.IsInvalidated("loss.corruptRate", Row, Pass));
        Assert.True(gates.IsInvalidated("mix.udp.lossRate", Row, Pass));
        Assert.True(gates.IsInvalidated(Throughput, Row, Pass));
        Assert.False(gates.IsInvalidated(Latency, Row, Pass));
        Assert.False(gates.IsInvalidated(Cpu, Row, Pass));
        Assert.Contains(gates.Invalidations, entry => entry is { Gate: "udp-identity", RowId: Row });
        Assert.Contains(gates.Invalidations, entry => entry.Gate == "udp-identity" && entry.Reason.Contains("6001", StringComparison.Ordinal));
        Assert.Contains(gates.Invalidations, entry => entry.Gate == "udp-identity" && entry.Reason.Contains("2401", StringComparison.Ordinal));

        var baseArm = Evaluate(Arm(Row, "BASE", udpIdentity: "loss/... != sent"));
        Assert.True(baseArm.IsInvalidated(Throughput, Row, Pass));
        Assert.DoesNotContain(baseArm.Invalidations, entry => entry.Metric == Loss);
    }

    [Fact]
    public void ADnsPartitionMismatchInvalidatesTheDnsArmsLatencyAndLossCells()
    {
        var gates = Evaluate(Arm(Row, "DNS", dnsPartition: "answered(9)+other(2) != sent(10)"));

        Assert.True(gates.IsInvalidated("dns.rtt.p50", Row, Pass));
        Assert.True(gates.IsInvalidated("dns.answerRate", Row, Pass));
        Assert.False(gates.IsInvalidated(Latency, Row, Pass));
        Assert.False(gates.IsInvalidated(Throughput, Row, Pass));
        Assert.Contains(
            gates.Invalidations,
            entry => entry.Gate == "dns-partition" && entry.Reason.Contains("answered(9)", StringComparison.Ordinal));
    }

    [Fact]
    public void BacklogScheduleLaneAndSamplerStopOnlyTheirAffectedCells()
    {
        var backlog = Evaluate(Arm(Row, "LAT", backlogDrops: 1));
        Assert.True(backlog.IsInvalidated(Latency, Row, Pass));
        Assert.False(backlog.IsInvalidated("lat.udp_lossRate", Row, Pass));

        var truncated = Evaluate(Arm(Row, "THRU", scheduleTruncated: 2));
        Assert.True(truncated.IsInvalidated(Throughput, Row, Pass));
        Assert.False(truncated.IsInvalidated(Loss, Row, Pass));

        var latencyTruncated = Evaluate(Arm(Row, "LAT", scheduleTruncated: 2));
        Assert.True(latencyTruncated.IsInvalidated(Latency, Row, Pass));
        Assert.False(latencyTruncated.IsInvalidated("lat.udp_lossRate", Row, Pass));

        var dnsTruncated = Evaluate(Arm(Row, "DNS", scheduleTruncated: 2));
        Assert.True(dnsTruncated.IsInvalidated("dns.rtt.p50", Row, Pass));
        Assert.False(dnsTruncated.IsInvalidated("dns.answerRate", Row, Pass));

        var shortfall = Evaluate(Arm(Row, "LOSS", laneShortfall: 1));
        Assert.True(shortfall.IsInvalidated(Loss, Row, Pass));
        Assert.False(shortfall.IsInvalidated(Latency, Row, Pass));

        var latencyShortfall = Evaluate(Arm(Row, "LAT", laneShortfall: 1));
        Assert.True(latencyShortfall.IsInvalidated(Latency, Row, Pass));
        Assert.True(latencyShortfall.IsInvalidated("lat.udp_rtt.p50", Row, Pass));
        Assert.True(latencyShortfall.IsInvalidated("lat.udp_lossRate", Row, Pass));

        var sampling = ReleaseGateMatrix.Evaluate(new ReleaseGateEvidence
        {
            Arms = [],
            Sampling = [new ReleaseGateSampling(Pass, Row, SamplerErrors: 2, RejectedSamples: 0)],
        });
        Assert.True(sampling.IsInvalidated(Cpu, Row, Pass));
        Assert.True(sampling.IsInvalidated(Memory, Row, Pass));
        Assert.False(sampling.IsInvalidated(Latency, Row, Pass));
        Assert.False(sampling.IsInvalidated(Loss, Row, Pass));
        Assert.All(sampling.Invalidations, entry => Assert.Equal("samplerError", entry.Gate));
    }

    [Fact]
    public void ARejectedSampleInvalidatesCpuAndMemoryLikeASamplerError()
    {
        var gates = ReleaseGateMatrix.Evaluate(new ReleaseGateEvidence
        {
            Arms = [],
            Sampling = [new ReleaseGateSampling(Pass, Row, SamplerErrors: 0, RejectedSamples: 3)],
        });

        Assert.True(gates.IsInvalidated(Cpu, Row, Pass));
        Assert.True(gates.IsInvalidated(Memory, Row, Pass));
        Assert.False(gates.IsInvalidated(Latency, Row, Pass));
        Assert.False(gates.IsInvalidated(Loss, Row, Pass));
        Assert.Contains(gates.Invalidations, entry => entry.Reason.Contains("3 unreadable sample(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void ControlDriftAndTheBaseFloorAreCaveatsNotCellInvalidations()
    {
        var gates = ReleaseGateMatrix.Evaluate(new ReleaseGateEvidence
        {
            Arms = [],
            ControlDrift = true,
            ControlDriftDetail = "post-pre = 0.8 pp",
            BaseFloorFailed = true,
            BaseFloorDetail = "control-post BASE lossRate=0.008",
            ForeignConnection = true,
            ForeignConnectionDetail = "foreignConnection=3",
            DirectLeak = true,
            DirectLeakDetail = "directLeak=2",
        });

        Assert.Empty(gates.Invalidations);
        Assert.Contains(gates.Caveats, caveat => caveat.Kind == "control-drift");
        Assert.Contains(gates.Caveats, caveat => caveat.Kind == "base-floor");
        Assert.Contains(gates.Caveats, caveat => caveat.Kind == "foreign-connection");
        Assert.Contains(gates.Caveats, caveat => caveat.Kind == "direct-leak");
    }

    [Fact]
    public void CleanEvidenceProducesNoInvalidationsOrCaveats()
    {
        var gates = Evaluate(
            Arm(Reference, "LAT"),
            Arm(Reference, "LOSS"),
            Arm(Reference, "THRU"));

        Assert.Empty(gates.Invalidations);
        Assert.Empty(gates.Caveats);
    }

    [Fact]
    public void AGateThatInvalidatesEveryPassOfAnOpponentShrinksTheFamily()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Loss, Series(1.0, 0.10, 10)),
            Row("proxifyre", passes).With(Loss, Series(2.0, 0.10, 10)),
            Row("proxifier", passes).WithStatus(Loss, ReleaseReadingStatus.NotCarried, "not carried (UDP bypassed)"),
            Row("proxybridge", passes).With(Loss, Series(1.5, 0.10, 10)));
        var gates = ReleaseGateMatrix.Evaluate(new ReleaseGateEvidence
        {
            Arms = [.. passes.Select(pass => new ReleaseGateArm
            {
                Pass = pass,
                RowId = "proxifyre",
                Arm = "LOSS",
                ClientSendLoss = 1.0,
            })],
        });

        var metric = ReleaseComparison.Evaluate(thresholds, inputs, dataset, gates)
            .Metrics.Single(result => result.Metric == Loss);
        var proxifyre = metric.Pairs.Single(pair => pair.RowId == "proxifyre");

        Assert.Equal(1, metric.FamilySize);
        Assert.False(proxifyre.InFamily);
        Assert.Equal(ReleaseState.Unevaluable, proxifyre.State);
        Assert.Contains("a release gate invalidated all 10 measured pass(es)", proxifyre.Reason, StringComparison.Ordinal);
        Assert.Equal(10, proxifyre.InvalidatedPasses.Count);
        Assert.Single(metric.GateInvalidations.Select(entry => entry.RowId).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void APartiallyInvalidatedPairKeepsTheFamilyAndUsesTheSurvivingPasses()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Loss, Series(1.0, 0.10, 10)),
            Row("proxifyre", passes).With(Loss, Series(2.0, 0.10, 10)),
            Row("proxifier", passes).WithStatus(Loss, ReleaseReadingStatus.NotCarried, "not carried (UDP bypassed)"),
            Row("proxybridge", passes).With(Loss, Series(1.5, 0.10, 10)));
        var gates = ReleaseGateMatrix.Evaluate(new ReleaseGateEvidence
        {
            Arms = [.. passes.Take(5).Select(pass => new ReleaseGateArm
            {
                Pass = pass,
                RowId = "proxifyre",
                Arm = "LOSS",
                ClientSendLoss = 1.0,
            })],
        });

        var metric = ReleaseComparison.Evaluate(thresholds, inputs, dataset, gates)
            .Metrics.Single(result => result.Metric == Loss);
        var proxifyre = metric.Pairs.Single(pair => pair.RowId == "proxifyre");

        Assert.Equal(2, metric.FamilySize);
        Assert.True(proxifyre.InFamily);
        Assert.Equal(5, proxifyre.PassesUsed);
        Assert.Equal(5, proxifyre.InvalidatedPasses.Count);
        Assert.Contains("a release gate invalidated 5 measured pass(es)", proxifyre.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AGateThatRemovesNoValueDoesNotShrinkTheFamily()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var empty = new ReleaseMetricReadings
        {
            Values = new Dictionary<string, double>(StringComparer.Ordinal),
            MissingPasses = passes.Length,
        };
        var dataset = new ReleaseDataset
        {
            MeasuredPasses = passes,
            Rows =
            [
                Row(Reference, passes).With(Loss, Series(1.0, 0.10, 10)).Build(),
                new ReleaseRowReadings(
                    "proxifyre",
                    new Dictionary<string, ReleaseMetricReadings>(StringComparer.Ordinal) { [Loss] = empty }),
                Row("proxifier", passes).WithStatus(Loss, ReleaseReadingStatus.NotCarried, "not carried (UDP bypassed)").Build(),
                Row("proxybridge", passes).With(Loss, Series(1.5, 0.10, 10)).Build(),
            ],
            Resamples = 2000,
            Seed = 20261006,
        };
        var gates = Evaluate(Arm("proxifyre", "LOSS", clientSendLoss: 1));

        var metric = ReleaseComparison.Evaluate(thresholds, inputs, dataset, gates)
            .Metrics.Single(result => result.Metric == Loss);
        var proxifyre = metric.Pairs.Single(pair => pair.RowId == "proxifyre");

        Assert.Equal(2, metric.FamilySize);
        Assert.True(proxifyre.InFamily);
        Assert.Empty(proxifyre.InvalidatedPasses);
        Assert.Equal(ReleaseState.Unevaluable, proxifyre.State);
        Assert.DoesNotContain("release gate invalidated", proxifyre.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdjustedIntervalLevelUsesTheShrunkFamily()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Loss, Series(1.0, 0.10, 10)),
            Row("proxifyre", passes).With(Loss, Series(2.0, 0.10, 10)),
            Row("proxifier", passes).WithStatus(Loss, ReleaseReadingStatus.NotCarried, "not carried (UDP bypassed)"),
            Row("proxybridge", passes).With(Loss, Series(1.2, 0.10, 10)));
        var gates = ReleaseGateMatrix.Evaluate(new ReleaseGateEvidence
        {
            Arms = [.. passes.Select(pass => new ReleaseGateArm
            {
                Pass = pass,
                RowId = "proxifyre",
                Arm = "LOSS",
                ClientSendLoss = 1.0,
            })],
        });

        var metric = ReleaseComparison.Evaluate(thresholds, inputs, dataset, gates)
            .Metrics.Single(result => result.Metric == Loss);
        var bridge = metric.Pairs.Single(pair => pair.RowId == "proxybridge");

        Assert.Equal(1, metric.FamilySize);
        Assert.True(bridge.InFamily);
        Assert.NotNull(bridge.AdjustedInterval);
        Assert.Equal(0.95, bridge.AdjustedInterval.Level, 12);
    }

    private static ReleaseGateMatrixResult Evaluate(params ReleaseGateArm[] arms) =>
        ReleaseGateMatrix.Evaluate(new ReleaseGateEvidence { Arms = arms });

    private static ReleaseGateArm Arm(
        string rowId,
        string arm,
        double? windowOverflow = null,
        double? clientSendLoss = null,
        double? backlogDrops = null,
        double? scheduleTruncated = null,
        double? laneShortfall = null,
        string? udpIdentity = null,
        string? dnsPartition = null) =>
        new()
        {
            Pass = Pass,
            RowId = rowId,
            Arm = arm,
            WindowOverflow = windowOverflow,
            ClientSendLoss = clientSendLoss,
            BacklogDrops = backlogDrops,
            ScheduleTruncated = scheduleTruncated,
            LaneShortfall = laneShortfall,
            UdpIdentityViolated = udpIdentity is not null,
            UdpIdentityDetail = udpIdentity,
            DnsPartitionViolated = dnsPartition is not null,
            DnsPartitionDetail = dnsPartition,
        };
}
