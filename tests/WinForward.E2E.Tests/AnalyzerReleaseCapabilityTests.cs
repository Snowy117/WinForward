using WinForward.E2E.Analysis.Release;
using Xunit;
using static WinForward.E2E.Tests.ReleaseComparisonTestData;

namespace WinForward.E2E.Tests;

/// <summary>
/// The REL capability gate through the public release seam: the pooled rate and exposure, the
/// rule-of-three upper bound, the pass/fail/not-measured statuses, and the rule that a zero-event rate
/// is never turned into an equivalence claim.
/// </summary>
public sealed class AnalyzerReleaseCapabilityTests
{
    private const string Reference = "wf-aot-nativeudp";

    private const string Reliability = "rel.unexpectedEofRate";

    [Fact]
    public void TheCapabilityGatePassesAtTheZeroEventBoundAndFailsWithOneEvent()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var passing = Dataset(
            passes,
            Row(Reference, passes).WithCounts(Reliability, Counts(10, events: 0, attemptsPerPass: 120)));
        var failing = Dataset(
            passes,
            Row(Reference, passes).WithCounts(Reliability, Counts(10, events: 1, attemptsPerPass: 120)));

        var passingResult = Capability(passing, thresholds, inputs);
        var failingResult = Capability(failing, thresholds, inputs);

        Assert.Equal(ReleaseCapabilityStatus.Pass, passingResult.Status);
        Assert.Equal(0.0, passingResult.Rate!.Value, 12);
        Assert.Equal(1200L, passingResult.N!.Value);
        Assert.Equal(0.25, passingResult.UpperBound!.Value, 12);
        Assert.Equal(0.25, passingResult.Threshold);
        Assert.Equal("connectAttempts", passingResult.NFrom);
        Assert.Equal(10, passingResult.Passes.Count);
        Assert.Contains("zero events", passingResult.Reason, StringComparison.Ordinal);

        Assert.Equal(ReleaseCapabilityStatus.Fail, failingResult.Status);
        Assert.Equal(1L, failingResult.Events!.Value);
        Assert.Equal(1200L, failingResult.N!.Value);
        Assert.Null(failingResult.UpperBound);
        Assert.Equal(1.0 / 12.0, failingResult.Rate!.Value, 12);
        Assert.Contains("does not apply", failingResult.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroDenominatorIsNotMeasuredRatherThanAPass()
    {
        var thresholds = ParseFixture("comparison.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).WithCounts(Reliability, Counts(10, events: 0, attemptsPerPass: 0)));

        var result = Capability(dataset, thresholds, inputs);

        Assert.Equal(ReleaseCapabilityStatus.NotMeasured, result.Status);
        Assert.Null(result.Rate);
        Assert.Null(result.N);
        Assert.Null(result.UpperBound);
        Assert.Contains("exposure", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroEventRelInAPrimaryFamilyIsUnevaluableWithTheUpperBound()
    {
        var thresholds = ParseFixture("rel-primary.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).WithCounts(Reliability, Counts(10, events: 0, attemptsPerPass: 120)),
            Row("proxifyre", passes).WithCounts(Reliability, Counts(10, events: 0, attemptsPerPass: 120)));

        var metric = ReleaseComparison.Evaluate(thresholds, inputs, dataset)
            .Metrics.Single(result => string.Equals(result.Metric, Reliability, StringComparison.Ordinal));
        var pair = metric.Pairs.Single(result => string.Equals(result.RowId, "proxifyre", StringComparison.Ordinal));

        Assert.Equal(ReleaseState.Unevaluable, pair.State);
        Assert.Equal(ReleaseState.Unevaluable, metric.State);
        Assert.Equal(1200L, metric.ZeroEventN!.Value);
        Assert.Equal(0.25, metric.ZeroEventUpperBound!.Value, 12);
        Assert.Contains("zero-event", pair.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3/1200", metric.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedCapabilityDenominatorIsNotMeasured()
    {
        var thresholds = ParseFixture("rel-wrong-denominator.json");
        var passes = Passes(10);
        var inputs = Inputs(thresholds, passes, required: 2);
        var dataset = Dataset(
            passes,
            Row(Reference, passes).WithCounts(Reliability, Counts(10, events: 0, attemptsPerPass: 120)));

        var result = Capability(dataset, thresholds, inputs);

        Assert.Equal(ReleaseCapabilityStatus.NotMeasured, result.Status);
        Assert.Equal("scheduledAttempts", result.NFrom);
        Assert.Null(result.UpperBound);
        Assert.Contains("does not sample", result.Reason, StringComparison.Ordinal);
    }

    private static ReleaseCapabilityResult Capability(
        ReleaseDataset dataset,
        ThresholdsFile thresholds,
        ReleaseInputs inputs) =>
        ReleaseComparison.Evaluate(thresholds, inputs, dataset)
            .Capabilities.Single(result =>
                string.Equals(result.Metric, Reliability, StringComparison.Ordinal)
                && string.Equals(result.RowId, Reference, StringComparison.Ordinal));

    private static (long Events, long Attempts)[] Counts(int passes, long events, long attemptsPerPass)
    {
        var counts = new (long Events, long Attempts)[passes];
        for (var index = 0; index < counts.Length; index++)
        {
            counts[index] = (index == 0 ? events : 0, attemptsPerPass);
        }

        return counts;
    }
}
