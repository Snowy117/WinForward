using WinForward.E2E.Analysis.Release;
using Xunit;
using static WinForward.E2E.Tests.ReleaseComparisonTestData;

namespace WinForward.E2E.Tests;

/// <summary>
/// The release comparison and state machine through its public seam: the reference-vs-opponent family,
/// the raw and Bonferroni-adjusted intervals, the <c>different</c>/<c>inconclusive</c>/<c>unevaluable</c>
/// states, the pre-registered TOST family, and the exploratory demotion of every row outside the family.
/// </summary>
public sealed class AnalyzerReleaseComparisonTests
{
    private const string Reference = "wf-aot-nativeudp";

    private const string Latency = "lat.tcp_rtt.p50";

    private const string Loss = "loss.lossRate";

    private const string Exploratory = "lat.tcp_rtt.p99";

    [Fact]
    public void AnAdjustedIntervalJustInsideTheBandIsInconclusiveAndJustOutsideIsDifferent()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);

        var inside = Dataset(
            passes,
            Row(Reference, passes).With(Latency, Series(100.0, 0.004, 10)),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.17, 0.004, 10)));
        var outside = Dataset(
            passes,
            Row(Reference, passes).With(Latency, Series(100.0, 0.004, 10)),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.20, 0.004, 10)));

        var insidePair = Pair(inside, thresholds, inputs, Latency, "proxifyre");
        var outsidePair = Pair(outside, thresholds, inputs, Latency, "proxifyre");

        Assert.Equal(ReleaseState.Inconclusive, insidePair.State);
        Assert.Equal(ReleaseState.Different, outsidePair.State);
        Assert.NotNull(outsidePair.AdjustedInterval);
        Assert.True(outsidePair.AdjustedInterval.Low > 1.18 || outsidePair.AdjustedInterval.High < 0.82);
        Assert.Contains("Bonferroni-adjusted, family-wise 95 %", outsidePair.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Holm", outsidePair.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportCarriesTheFamilyMetadataAndTheMeasuredPassCounts()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Latency, Series(100.0, 0.004, 10)),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.10, 0.004, 10)));

        var report = ReleaseComparison.Evaluate(thresholds, inputs, dataset);
        var metric = report.Metrics.Single(result => result.Metric == Latency);
        var pair = metric.Pairs.Single(result => result.RowId == "proxifyre");

        Assert.Equal(inputs.Passes.N, report.N);
        Assert.Equal(inputs.Passes.Required, report.Required);
        Assert.False(report.Insufficient);
        Assert.Equal(ReleaseComparison.FamilywiseAlpha, report.FamilywiseAlpha);
        Assert.Equal(
            "pass1,pass2,pass3,pass4,pass5,pass6,pass7,pass8,pass9,pass10",
            string.Join(",", report.MeasuredPasses));
        Assert.Equal(ReleaseMetricRole.Primary, metric.Role);
        Assert.Equal("ratio", metric.Kind);
        Assert.Equal(0.18, metric.Band!.Value, 12);
        Assert.Equal(0.05, metric.Business!.Value, 12);
        Assert.Equal(0.18, metric.Mdd!.Value, 12);
        Assert.Equal(0.05, metric.EquivalenceMargin!.Value, 12);
        Assert.Equal(10, metric.ReferenceValues.Count);
        Assert.NotNull(metric.ReferenceMedian);
        Assert.NotNull(pair.ReferenceMedian);
        Assert.NotNull(pair.RowMedian);
        Assert.NotNull(pair.Median);
        Assert.Equal(0.05, pair.EquivalenceMargin!.Value, 12);
    }

    [Fact]
    public void AnAdjustedIntervalUsesTheFamilySizeAndContainsTheRawInterval()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Latency, Series(100.0, 0.004, 10)),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.10, 0.004, 10)),
            Row("proxifier", passes).With(Latency, Series(100.0 / 0.95, 0.004, 10)),
            Row("proxybridge", passes).With(Latency, Series(100.0 / 1.03, 0.004, 10)));

        var metric = Metric(dataset, thresholds, inputs, Latency);

        Assert.Equal(3, metric.FamilySize);
        foreach (var pair in metric.Pairs.Where(entry => entry.InFamily))
        {
            Assert.NotNull(pair.RawInterval);
            Assert.NotNull(pair.AdjustedInterval);
            Assert.Equal(0.95, pair.RawInterval.Level, 12);
            Assert.Equal(1.0 - (0.05 / 3.0), pair.AdjustedInterval.Level, 12);
            Assert.True(pair.AdjustedInterval.Low <= pair.RawInterval.Low);
            Assert.True(pair.AdjustedInterval.High >= pair.RawInterval.High);
        }
    }

    [Fact]
    public void FewerMeasuredPassesThanRequiredMakesEveryPairInconclusive()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(4);
        var inputs = Inputs(thresholds, passes, required: 6);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Latency, Series(100.0, 0.004, 4)),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.20, 0.004, 4)));

        var metric = Metric(dataset, thresholds, inputs, Latency);
        var pair = Pair(dataset, thresholds, inputs, Latency, "proxifyre");

        Assert.True(inputs.Passes.Insufficient);
        Assert.Equal(ReleaseState.Inconclusive, pair.State);
        Assert.Contains("at least 6", pair.Reason, StringComparison.Ordinal);
        Assert.Equal(ReleaseState.Inconclusive, metric.State);
        Assert.All(metric.Pairs.Where(entry => entry.InFamily), entry => Assert.Equal(ReleaseState.Inconclusive, entry.State));
    }

    [Fact]
    public void AZeroVariancePrimaryMetricIsUnevaluableNotEquivalent()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Latency, [.. Enumerable.Repeat(100.0, 10)]),
            Row("proxifyre", passes).With(Latency, [.. Enumerable.Repeat(100.0, 10)]));

        var metric = Metric(dataset, thresholds, inputs, Latency);
        var pair = Pair(dataset, thresholds, inputs, Latency, "proxifyre");

        Assert.Equal(ReleaseState.Unevaluable, pair.State);
        Assert.NotEqual(ReleaseState.Equivalent, pair.State);
        Assert.Equal(ReleaseState.Unevaluable, metric.State);
        Assert.Contains("degenerate", pair.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APreRegisteredTostFamilyIsEquivalentOnlyInsideTheMargin()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var inside = Dataset(
            passes,
            Row(Reference, passes).With(Latency, Series(100.0, 0.004, 10)),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.02, 0.004, 10)));
        var outside = Dataset(
            passes,
            Row(Reference, passes).With(Latency, Series(100.0, 0.004, 10)),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.12, 0.004, 10)));

        var insidePair = Pair(inside, thresholds, inputs, Latency, "proxifyre");
        var outsidePair = Pair(outside, thresholds, inputs, Latency, "proxifyre");

        Assert.Equal(ReleaseState.Equivalent, insidePair.State);
        Assert.Contains("TOST", insidePair.Reason, StringComparison.Ordinal);
        Assert.Contains("unadjusted", insidePair.Reason, StringComparison.Ordinal);
        Assert.Equal(ReleaseState.Inconclusive, outsidePair.State);
        Assert.NotEqual(ReleaseState.Equivalent, outsidePair.State);
    }

    [Fact]
    public void AnEquivalentAndUnevaluableFamilyAggregatesToUnevaluable()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var reference = Series(100.0, 0.004, 10);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Latency, reference),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.02, 0.004, 10)),
            Row("proxybridge", passes).With(Latency, [.. Enumerable.Repeat(0.0, 10)]));

        var metric = Metric(dataset, thresholds, inputs, Latency);
        var equivalent = Pair(dataset, thresholds, inputs, Latency, "proxifyre");
        var unevaluable = Pair(dataset, thresholds, inputs, Latency, "proxybridge");

        Assert.Equal(ReleaseState.Equivalent, equivalent.State);
        Assert.Equal(ReleaseState.Unevaluable, unevaluable.State);
        Assert.Equal(2, metric.FamilySize);
        Assert.Equal(ReleaseState.Unevaluable, metric.State);
        Assert.Contains("no pair is different or inconclusive", metric.Reason, StringComparison.Ordinal);
        Assert.Contains("unevaluable", metric.Reason, StringComparison.Ordinal);
        Assert.Contains("equivalent", metric.Reason, StringComparison.Ordinal);
        Assert.Equal("proxybridge", metric.DecisionPair);
    }

    [Fact]
    public void APrimaryMetricWithoutAnEquivalenceFamilyHasNoMarginClaim()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Loss, Series(1.0, 0.10, 10)),
            Row("proxifyre", passes).With(Loss, Series(1.4, 0.10, 10)));

        var metric = Metric(dataset, thresholds, inputs, Loss);
        var pair = Pair(dataset, thresholds, inputs, Loss, "proxifyre");

        Assert.Null(metric.EquivalenceMargin);
        Assert.Null(pair.EquivalenceMargin);
        Assert.DoesNotContain("TOST", metric.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("pre-registered", metric.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("TOST", pair.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("pre-registered", pair.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExploratoryMetricsAreReportOnlyAndCarryNoAdjustedInterval()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Exploratory, Series(50.0, 0.01, 10)),
            Row("proxifyre", passes).With(Exploratory, Series(60.0, 0.01, 10)));

        var metric = Metric(dataset, thresholds, inputs, Exploratory);

        Assert.Equal(ReleaseMetricRole.Exploratory, metric.Role);
        Assert.Equal(ReleaseState.ReportOnly, metric.State);
        Assert.Equal(0, metric.FamilySize);
        Assert.Null(metric.Kind);
        Assert.Null(metric.Band);
        foreach (var pair in metric.Pairs)
        {
            Assert.False(pair.InFamily);
            Assert.Null(pair.AdjustedInterval);
            Assert.Equal(ReleaseState.ReportOnly, pair.State);
        }

        var proxifyre = metric.Pairs.Single(pair => pair.RowId == "proxifyre");
        Assert.NotNull(proxifyre.RawInterval);
        Assert.Equal(0.95, proxifyre.RawInterval.Level, 12);
    }

    [Fact]
    public void AnInternalWinForwardRowIsReportOnlyOutsideTheFamily()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Latency, Series(100.0, 0.004, 10)),
            Row("proxifyre", passes).With(Latency, Series(100.0 / 1.10, 0.004, 10)),
            Row("wf-aot-opt", passes).With(Latency, Series(100.0 / 1.01, 0.004, 10)));

        var metric = Metric(dataset, thresholds, inputs, Latency);
        var internalPair = metric.Pairs.Single(pair => pair.RowId == "wf-aot-opt");

        Assert.False(internalPair.InFamily);
        Assert.Equal(ReleaseState.ReportOnly, internalPair.State);
        Assert.Contains("not a declared opponent", internalPair.Ineligibility, StringComparison.Ordinal);
        Assert.Equal(1, metric.FamilySize);
    }

    [Fact]
    public void AProductThatCannotCarryUdpIsExcludedFromTheLossFamily()
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

        var metric = Metric(dataset, thresholds, inputs, Loss);
        var proxifier = metric.Pairs.Single(pair => pair.RowId == "proxifier");
        var proxifyre = metric.Pairs.Single(pair => pair.RowId == "proxifyre");

        Assert.Equal(2, metric.FamilySize);
        Assert.False(proxifier.InFamily);
        Assert.Equal(ReleaseState.Unevaluable, proxifier.State);
        Assert.Contains("not carried", proxifier.Reason, StringComparison.Ordinal);
        Assert.NotNull(proxifyre.AdjustedInterval);
        Assert.Equal(1.0 - (0.05 / 2.0), proxifyre.AdjustedInterval.Level, 12);
    }

    [Fact]
    public void AUdpMetricWithOnlyAnIncapableOpponentHasNoComparableFamily()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).With(Loss, Series(1.0, 0.10, 10)),
            Row("proxifier", passes).WithStatus(Loss, ReleaseReadingStatus.NotCarried, "not carried (UDP bypassed)"));

        var metric = Metric(dataset, thresholds, inputs, Loss);

        Assert.Equal(0, metric.FamilySize);
        Assert.Equal(ReleaseState.Unevaluable, metric.State);
        Assert.Contains("no comparable declared opponent", metric.Reason, StringComparison.Ordinal);
    }

    private static ReleaseMetricResult Metric(
        ReleaseDataset dataset,
        ThresholdsFile thresholds,
        ReleaseInputs inputs,
        string metric) =>
        ReleaseComparison.Evaluate(thresholds, inputs, dataset)
            .Metrics.Single(result => string.Equals(result.Metric, metric, StringComparison.Ordinal));

    private static ReleasePairResult Pair(
        ReleaseDataset dataset,
        ThresholdsFile thresholds,
        ReleaseInputs inputs,
        string metric,
        string rowId) =>
        Metric(dataset, thresholds, inputs, metric)
            .Pairs.Single(pair => string.Equals(pair.RowId, rowId, StringComparison.Ordinal));

}
