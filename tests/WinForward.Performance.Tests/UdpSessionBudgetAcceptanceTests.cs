using System.Text.Json;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using Xunit;

namespace WinForward.Performance.Tests;

/// <summary>
/// The session-budget acceptance arithmetic: the shared-placement ceiling's clamped slack, the
/// retention ceiling's discrimination precondition, the two named verdict halves, the estimated
/// kernel receive buffer, and the row fields the ratios are recomputed from. Every case is a
/// synthetic sample, so the acceptance is pinned without running the soak.
/// </summary>
public sealed class UdpSessionBudgetAcceptanceTests
{
    private static readonly TimeSpan s_idle = ConfigurationLoader.DefaultUdpSessionIdleTimeout;

    /// <summary>
    /// The shipped UDP sweep interval: the effective retention floor (the 5 s one-shot class) halved
    /// and floored, i.e. 5 s. It is not a hand number — it is
    /// <c>IdleExpirySweeper.DeriveUdpSweepInterval(main, EffectiveUdpRetentionFloor(idle, oneShot))</c>,
    /// pinned by <c>UdpSessionRetentionTests.EffectiveUdpRetentionFloorFloorsOnTheOneShotClass</c>.
    /// </summary>
    private static readonly TimeSpan s_sweep = TimeSpan.FromSeconds(5);

    private const int FlowsPerAssociation = ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation;
    private const int MaxAssociationsPerServer = ConfigurationLoader.DefaultUdpAssociationMaxPerServer;
    private const int RelayReceiveBufferBytes = ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes;

    /// <summary>
    /// The acceptance load's steady-state peak at <c>--rate 100</c>: the resident set the shipped
    /// two-class retention and 5 s sweep produce, which is what the retention ceiling and the pooling
    /// half are asserted against. It is the measured median <c>steadyStateSessions</c> of the three
    /// after-arm runs in
    /// <c>benchmarks/results/2026-10-01-udp-session-footprint/session-budget-after.jsonl</c>
    /// (1,014 / 1,038 / 1,014), i.e. the one-shot band
    /// <c>rate × (short 5 s + 2 × sweep 5 s) ≈ 1,500</c> with the sawtooth's phase. The same instrument
    /// measures 4,539 on the pre-change uniform-retention arm, so the load's meaning is "the resident set
    /// the shipped retention produces", not the pre-change peak; with a frozen activity clock — the
    /// defect the scenario now mirrors the pump's tick to avoid — it read 500 here, which was a
    /// mass-wipe artifact.
    /// </summary>
    private const int AcceptanceLoadSessions = 1_014;

    [Fact]
    public void TheAcceptanceLoadCeilingIsTheIdealFanOutPlusTheClampedSlack()
    {
        // ceil(1,014 / 16) = 64 shared associations serve the load at the ideal fan-out; the slack
        // is a small constant because cold and warm associations kept beyond it are legitimate but
        // must not scale with the per-server ceiling.
        Assert.Equal(64, (AcceptanceLoadSessions + FlowsPerAssociation - 1) / FlowsPerAssociation);
        Assert.Equal(80, SessionBudgetMath.SharedAssociationCeiling(AcceptanceLoadSessions, FlowsPerAssociation, MaxAssociationsPerServer));
        Assert.Equal(64 + SessionBudgetMath.SharedAssociationSlack, SessionBudgetMath.SharedAssociationCeiling(AcceptanceLoadSessions, FlowsPerAssociation, MaxAssociationsPerServer));
        // The ceiling is independent of the per-server cap above the slack: raising the cap to the
        // product maximum leaves the bound where it is, so a caps change cannot loosen it.
        Assert.Equal(80, SessionBudgetMath.SharedAssociationCeiling(AcceptanceLoadSessions, FlowsPerAssociation, 16_384));
        // A deliberately small cap still tightens it.
        Assert.Equal(64 + 4, SessionBudgetMath.SharedAssociationCeiling(AcceptanceLoadSessions, FlowsPerAssociation, 4));
    }

    [Fact]
    public void ATenthOfTheAcceptanceLoadHeldPrivatelyFailsTheSharedPlacementCeiling()
    {
        // 101 of 1,014 flows served from private per-flow associations: 101 + ceil(913 / 16) = 159
        // associations, well above the 80 the shared placement allows.
        const int privateAssociations = AcceptanceLoadSessions / 10;
        const int sharedAssociations = (AcceptanceLoadSessions - privateAssociations + FlowsPerAssociation - 1) / FlowsPerAssociation;
        Assert.Equal(159, privateAssociations + sharedAssociations);
        Assert.True(privateAssociations + sharedAssociations > SessionBudgetMath.SharedAssociationCeiling(AcceptanceLoadSessions, FlowsPerAssociation, MaxAssociationsPerServer));

        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "90");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions, associations: privateAssociations + sharedAssociations, fileDescriptors: AcceptanceLoadSessions + privateAssociations + sharedAssociations, baselineFileDescriptors: 100)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions,
            SteadyPeakAssociations: privateAssociations + sharedAssociations));
        Assert.False(acceptance.PoolingCovered);
        // The retention half still holds, and the descriptor accounting still fits the 10 %-private
        // shape: the pooling ceiling is the assertion that sees it, and the verdict halves let a
        // reader see that only that term failed.
        Assert.True(acceptance.RetentionBounded);
        Assert.Contains("above the shared-placement ceiling 80", Assert.Single(acceptance.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePostCapsAcceptanceShapePassesBothHalves()
    {
        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "3600", "--require-pooling");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions, associations: 66, fileDescriptors: AcceptanceLoadSessions + 66 + 3, baselineFileDescriptors: 100)],
            Drain(360_000),
            Accepted: 360_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions,
            SteadyPeakAssociations: 66));

        Assert.True(acceptance.Passed, string.Join(" | ", acceptance.Failures));
        Assert.True(acceptance.RetentionBounded);
        Assert.True(acceptance.PoolingCovered);
        Assert.False(acceptance.PoolingSaturated);
        Assert.Equal(0, acceptance.SessionsBeyondSharedBudget);
        Assert.Equal(4_200, acceptance.SessionCeiling);
        // 43 x 100 = 4,300 flows, the first window that exceeds the 4,200 ceiling.
        Assert.Equal(43, acceptance.MinimumChurnSeconds);
    }

    [Fact]
    public void ASaturatedPopulationSkipsThePoolingHalfAndRequirePoolingRefusesIt()
    {
        // The pre-caps head (16 x 16 = 256 flows per server) is the shape the coverage check cannot
        // describe: the pool serves the overflow from private associations, so the run must say so
        // rather than never evaluate the pooling term.
        const int sharedFlowBudget = 16 * 16;
        const int privateAssociations = AcceptanceLoadSessions - sharedFlowBudget;
        var outcome = new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions, associations: privateAssociations + 16, fileDescriptors: 100 + AcceptanceLoadSessions + privateAssociations)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions,
            SteadyPeakAssociations: privateAssociations + 16);
        var lenient = new SessionBudgetAcceptance(Options("--rate", "100", "--churn-seconds", "90"), s_idle, s_sweep, FlowsPerAssociation, 16, RelayReceiveBufferBytes);
        lenient.Evaluate(outcome);
        Assert.False(lenient.PoolingCovered);
        Assert.True(lenient.PoolingSaturated);
        Assert.Equal(AcceptanceLoadSessions - 256, lenient.SessionsBeyondSharedBudget);
        Assert.True(lenient.RetentionBounded);
        Assert.True(lenient.Passed, string.Join(" | ", lenient.Failures));

        var strict = new SessionBudgetAcceptance(Options("--rate", "100", "--churn-seconds", "90", "--require-pooling"), s_idle, s_sweep, FlowsPerAssociation, 16, RelayReceiveBufferBytes);
        strict.Evaluate(outcome);
        Assert.False(strict.Passed);
        Assert.Contains(strict.Failures, failure => failure.Contains("--require-pooling", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonDiscriminatingChurnWindowIsRefusedInsteadOfClaimingRetention()
    {
        // 100 flows/s x 40 s = 4,000 flows against the 4,200 ceiling: a run that never expired a
        // session would still pass, so the retention property is not claimable.
        var options = Options("--rate", "100", "--churn-seconds", "40");
        var acceptance = new SessionBudgetAcceptance(options, s_idle, s_sweep, FlowsPerAssociation, MaxAssociationsPerServer, RelayReceiveBufferBytes);
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions)],
            Drain(4_000),
            Accepted: 4_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions,
            SteadyPeakAssociations: 107));

        Assert.False(acceptance.RetentionDiscriminating);
        Assert.False(acceptance.RetentionBounded);
        Assert.Equal(43, acceptance.MinimumChurnSeconds);
        Assert.Contains(acceptance.Failures, failure => failure.Contains("raise --churn-seconds above 42", StringComparison.Ordinal));
        // The shape is refused before the load, so a bad invocation cannot spend the soak.
        Assert.Contains(
            "raise --churn-seconds above 42",
            Assert.Throws<InvalidOperationException>(() => UdpSessionBudgetScenario.ValidateChurnWindow(options, s_idle, s_sweep)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultChurnWindowDiscriminatesAtTheDefaultRate()
    {
        var options = SoakOptions.Parse(["--scenario", "udpSessionBudget"]);
        var margin = SessionBudgetMath.SteadyStateMargin(options.Rate);
        var ceiling = SessionBudgetMath.SteadyStateSessionCeiling(options.Rate, s_idle, s_sweep, margin);
        Assert.Equal(840, ceiling);
        Assert.True(options.Rate * options.ChurnSeconds > ceiling);
        UdpSessionBudgetScenario.ValidateChurnWindow(options, s_idle, s_sweep);
    }

    [Fact]
    public void AMissingSteadyStateSampleLeavesTheSampleDerivedFieldsAbsent()
    {
        // Defensive branch: a discriminating window is always longer than idle + sweep, so a real run
        // always samples steady state; the acceptance still distinguishes "no sample" from a measured
        // zero instead of reporting a false saturation and a zero headroom.
        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "90");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(phase: "churn", elapsedSeconds: 10)],
            Drain(0),
            Accepted: 0,
            Rejected: 0,
            ChurnPeakSessions: 0,
            SteadyPeakSessions: 0,
            SteadyPeakAssociations: 0));

        Assert.Null(acceptance.WorstSteady);
        Assert.Null(acceptance.PoolingSaturated);
        Assert.Null(acceptance.SessionsBeyondSharedBudget);
        Assert.Null(acceptance.DescriptorHeadroom);
        Assert.False(acceptance.RetentionBounded);
        Assert.False(acceptance.PoolingCovered);
        Assert.Contains(acceptance.Failures, failure => failure.Contains("never reached steady state", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRetentionHalfCoversTheEstimatedKernelReceiveBuffer()
    {
        const long buffer = (long)AcceptanceLoadSessions * RelayReceiveBufferBytes;
        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "90");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions, relayReceiveBufferBytes: buffer)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions,
            SteadyPeakAssociations: 0));
        Assert.True(acceptance.RetentionBounded, string.Join(" | ", acceptance.Failures));
        Assert.Equal(4_200L * RelayReceiveBufferBytes, acceptance.RelayReceiveBufferBudget);
        Assert.Equal(buffer, acceptance.WorstSteady!.RelayReceiveBufferBytes);
        Assert.Equal(RelayReceiveBufferBytes, acceptance.WorstSteady.RelayReceiveBufferBytesPerSession);

        // An estimate inflated past the retention bound is rejected, not merely reported.
        var inflated = Acceptance("--rate", "100", "--churn-seconds", "90");
        inflated.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions, relayReceiveBufferBytes: (4_200L * RelayReceiveBufferBytes) + 1)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions,
            SteadyPeakAssociations: 0));
        Assert.False(inflated.RetentionBounded);
        Assert.Contains(inflated.Failures, failure => failure.Contains("above the retention bound", StringComparison.Ordinal));

        // An estimate that does not reproduce the configured per-session buffer is rejected too, so
        // the reported bytes cannot drift away from the population they are derived from. 32 KiB is
        // deliberately below the shipped default: an estimate at the default would be correct and
        // the misattribution check would pass vacuously.
        var misattributed = Acceptance("--rate", "100", "--churn-seconds", "90");
        misattributed.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions, relayReceiveBufferBytes: (long)AcceptanceLoadSessions * 32 * 1_024)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions,
            SteadyPeakAssociations: 0));
        Assert.False(misattributed.RetentionBounded);
        Assert.Contains(misattributed.Failures, failure => failure.Contains("per live session, not the configured", StringComparison.Ordinal));
    }

    [Fact]
    public void TheChurnArrivalScheduleIsAnchoredAtTheChurnStart()
    {
        // The warm-up took 3 s; the first churn arrival is due 1 / rate later, not immediately, so
        // the warm-up duration is not emitted as a burst of catch-up arrivals.
        var arrivals = new SessionBudgetArrivals(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(0.01));
        Assert.Equal(0.01, arrivals.Advance(TimeSpan.FromSeconds(3)).TotalSeconds, 6);
        Assert.Equal(0.01, arrivals.Advance(TimeSpan.FromSeconds(3.01)).TotalSeconds, 6);
        // A deadline that already passed still returns a negative wait, which the loop reads as
        // "fire now": a stall is caught up instead of shifting the realized rate.
        Assert.True(arrivals.Advance(TimeSpan.FromSeconds(4)).TotalSeconds < 0);
    }

    [Fact]
    public void SampleRowsCarryTheBaselinesAndRecomputeTheirRatios()
    {
        var sample = Sample(
            phase: "churn",
            elapsedSeconds: 60,
            sessions: 1_000,
            associations: 63,
            fileDescriptors: 1_100,
            baselineFileDescriptors: 90,
            managedBytes: 42_000_000,
            baselineManagedBytes: 2_000_000,
            relayReceiveBufferBytes: 1_000L * RelayReceiveBufferBytes);
        using var row = JsonDocument.Parse(StabilityContext.SerializeMetrics(sample.ToRow()));
        var root = row.RootElement;
        Assert.Equal(90, root.GetProperty("baselineFileDescriptors").GetInt32());
        Assert.Equal(2_000_000, root.GetProperty("baselineManagedBytes").GetInt64());
        // Both ratios are recomputable from the row: (measured - baseline) / sessions.
        Assert.Equal((1_100 - 90) / 1_000.0, root.GetProperty("fileDescriptorsPerSession").GetDouble(), 9);
        Assert.Equal((42_000_000 - 2_000_000) / 1_000.0, root.GetProperty("managedBytesPerSession").GetDouble(), 6);
        Assert.Equal(RelayReceiveBufferBytes, root.GetProperty("relayReceiveBufferBytesPerSession").GetDouble(), 6);
        Assert.Equal(1_000L * RelayReceiveBufferBytes, root.GetProperty("relayReceiveBufferBytes").GetInt64());
    }

    [Fact]
    public void ThePoolingCheckReadsTheArrivalGranularPeakNotTheSampledRow()
    {
        // The acceptance shape: the live population sawtooths as each sweep releases a cohort, and the
        // 5 s rows alias it — a sampled row can read well below the arrival-granular peak while the
        // association count has already ratcheted to the peak's placement, so the sample reads above
        // the ideal fan-out of its own smaller population. The arrival-granular peak sees the load
        // itself and the associations the pool actually placed, i.e. ceil(load / 16) plus the
        // retained slack.
        const int sampledSessions = AcceptanceLoadSessions / 2;
        const int placedAssociations = (AcceptanceLoadSessions + FlowsPerAssociation - 1) / FlowsPerAssociation;
        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "90", "--require-pooling");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: sampledSessions, associations: placedAssociations, fileDescriptors: sampledSessions + placedAssociations, baselineFileDescriptors: 100)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: sampledSessions,
            SteadyPeakSessions: AcceptanceLoadSessions,
            SteadyPeakAssociations: placedAssociations + 2));

        Assert.Equal(placedAssociations + 2, acceptance.SteadyPeakAssociations);
        Assert.Equal(AcceptanceLoadSessions, acceptance.SteadyPeakSessions);
        Assert.Equal((AcceptanceLoadSessions + FlowsPerAssociation - 1) / FlowsPerAssociation, placedAssociations);
        Assert.Equal((double)(placedAssociations + 2) / AcceptanceLoadSessions, acceptance.PeakAssociationsPerSession, 3);
        // The sampled shape alone would fail the pooling check: that is what makes the arrival-granular
        // reading the discriminating one.
        Assert.True(placedAssociations > SessionBudgetMath.SharedAssociationCeiling(sampledSessions, FlowsPerAssociation, MaxAssociationsPerServer));
        Assert.True(acceptance.RetentionBounded, string.Join(" | ", acceptance.Failures));
        Assert.True(acceptance.PoolingCovered);
        Assert.True(acceptance.Passed, string.Join(" | ", acceptance.Failures));
    }

    private static SessionBudgetAcceptance Acceptance(params string[] args)
        => new(Options(args), s_idle, s_sweep, FlowsPerAssociation, MaxAssociationsPerServer, RelayReceiveBufferBytes);

    private static SoakOptions Options(params string[] args)
        => SoakOptions.Parse(["--scenario", "udpSessionBudget", .. args]);

    /// <summary>The drain-end sample of a clean run: nothing live, no lease outstanding, and the descriptor count back at the baseline.</summary>
    private static SessionBudgetSample Drain(long accepted)
        => Sample(phase: "drainEnd", elapsedSeconds: 300, fileDescriptors: 100, baselineFileDescriptors: 100, accepted: accepted);

    /// <summary>
    /// A synthetic sample. The estimated receive buffer defaults to the value consistent with the
    /// population (<c>sessions × the configured relay buffer</c>); pass it explicitly to model an
    /// inflated or misattributed estimate.
    /// </summary>
    private static SessionBudgetSample Sample(
        string phase = "churn",
        double elapsedSeconds = 60,
        int sessions = 0,
        int associations = 0,
        int leasedFlows = 0,
        int fileDescriptors = 0,
        int baselineFileDescriptors = 0,
        long managedBytes = 0,
        long baselineManagedBytes = 0,
        long relayReceiveBufferBytes = -1,
        long accepted = 0,
        long rejected = 0,
        long datagramsLost = 0,
        long capacityRejections = 0,
        long setupRejections = 0,
        long setupFailures = 0)
        => new(
            Phase: phase,
            Index: 1,
            ElapsedSeconds: elapsedSeconds,
            Sessions: sessions,
            Associations: associations,
            LeasedFlows: leasedFlows,
            FileDescriptors: fileDescriptors,
            RawFileDescriptors: fileDescriptors,
            HarnessServerConnections: 0,
            ManagedBytes: managedBytes,
            WorkingSetBytes: 0,
            BaselineFileDescriptors: baselineFileDescriptors,
            BaselineManagedBytes: baselineManagedBytes,
            RelayReceiveBufferBytes: relayReceiveBufferBytes < 0 ? (long)sessions * RelayReceiveBufferBytes : relayReceiveBufferBytes,
            Accepted: accepted,
            Rejected: rejected,
            Expired: 0,
            DatagramsSent: accepted,
            DatagramsReceived: accepted - datagramsLost,
            DatagramsLost: datagramsLost,
            CapacityRejections: capacityRejections,
            SetupRejections: setupRejections,
            SetupFailures: setupFailures);
}
