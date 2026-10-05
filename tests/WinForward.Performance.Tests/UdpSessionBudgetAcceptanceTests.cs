using System.Globalization;
using System.Text.Json;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using Xunit;

namespace WinForward.Performance.Tests;

/// <summary>
/// The session-budget acceptance arithmetic: the retention ceiling's discrimination precondition and
/// population bound, the per-session descriptor budget, the estimated kernel receive buffer, and the
/// row fields the ratios are recomputed from. Every case is a synthetic sample, so the acceptance is
/// pinned without running the soak.
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

    private const int RelayReceiveBufferBytes = ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes;

    /// <summary>
    /// The acceptance load's steady-state peak at <c>--rate 100</c>: the resident set the shipped
    /// two-class retention and 5 s sweep produce, which is what the retention ceiling and the
    /// descriptor budget are asserted against. It is the measured median <c>steadyStateSessions</c>
    /// of the three after-arm runs in
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
    public void TheAcceptanceShapePassesTheRetentionAndDescriptorHalves()
    {
        // The shipped per-flow shape at the acceptance load: one relay socket and one control
        // connection per live session, so the descriptor delta is 2 x sessions against a budget of
        // ceil(sessions x 1.25) + associations (one association per session).
        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "3600");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions, associations: AcceptanceLoadSessions, fileDescriptors: (2 * AcceptanceLoadSessions) + 100, baselineFileDescriptors: 100)],
            Drain(360_000),
            Accepted: 360_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions));

        Assert.True(acceptance.Passed, string.Join(" | ", acceptance.Failures));
        Assert.True(acceptance.RetentionBounded);
        Assert.Equal(4_200, acceptance.SessionCeiling);
        Assert.Equal(4_200L * RelayReceiveBufferBytes, acceptance.RelayReceiveBufferBudget);
        // Budget 4,200 + ceil(1,014 x 1.25) + margin: the 2-per-session shape leaves headroom.
        Assert.Equal(
            SessionBudgetMath.FileDescriptorBudget(AcceptanceLoadSessions, AcceptanceLoadSessions) + 32 - (2 * AcceptanceLoadSessions),
            acceptance.DescriptorHeadroom);
        // 43 x 100 = 4,300 flows, the first window that exceeds the 4,200 ceiling.
        Assert.Equal(43, acceptance.MinimumChurnSeconds);
    }

    [Fact]
    public void AThirdDescriptorPerSessionFailsTheDescriptorBudget()
    {
        // The budget is the assertion that can see a socket the flow did not release: the shipped
        // shape costs two descriptors per session (relay socket + control connection), so a leaked
        // third — or an association whose teardown does not follow its session's — does not fit.
        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "90");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions, associations: AcceptanceLoadSessions, fileDescriptors: (3 * AcceptanceLoadSessions) + 100, baselineFileDescriptors: 100)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions));

        Assert.False(acceptance.Passed);
        Assert.True(acceptance.DescriptorHeadroom < 0);
        Assert.Contains("above the budget", Assert.Single(acceptance.Failures), StringComparison.Ordinal);
        // The retention half still holds: the descriptor check is the term that sees this shape.
        Assert.True(acceptance.RetentionBounded);
    }

    [Fact]
    public void APopulationAboveTheRetentionCeilingFailsTheRetentionHalf()
    {
        // A retention regression is what the ceiling exists to catch: a live set that keeps
        // accumulating instead of following the active flow set exceeds rate x (idle + 2 x sweep) +
        // margin. At --rate 100 the ceiling is 4,200, so 4,201 live sessions is the first failing
        // value.
        const int sessions = 4_201;
        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "90");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: sessions, associations: sessions, fileDescriptors: (2 * sessions) + 100, baselineFileDescriptors: 100)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: sessions,
            SteadyPeakSessions: sessions));

        Assert.False(acceptance.RetentionBounded);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"above the retention ceiling {acceptance.SessionCeiling}"), Assert.Single(acceptance.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void ANonDiscriminatingChurnWindowIsRefusedInsteadOfClaimingRetention()
    {
        // 100 flows/s x 40 s = 4,000 flows against the 4,200 ceiling: a run that never expired a
        // session would still pass, so the retention property is not claimable.
        var options = Options("--rate", "100", "--churn-seconds", "40");
        var acceptance = new SessionBudgetAcceptance(options, s_idle, s_sweep, RelayReceiveBufferBytes);
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: AcceptanceLoadSessions)],
            Drain(4_000),
            Accepted: 4_000,
            Rejected: 0,
            ChurnPeakSessions: AcceptanceLoadSessions,
            SteadyPeakSessions: AcceptanceLoadSessions));

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
        // zero instead of reporting a zero headroom.
        var acceptance = Acceptance("--rate", "100", "--churn-seconds", "90");
        acceptance.Evaluate(new SessionBudgetOutcome(
            [Sample(phase: "churn", elapsedSeconds: 10)],
            Drain(0),
            Accepted: 0,
            Rejected: 0,
            ChurnPeakSessions: 0,
            SteadyPeakSessions: 0));

        Assert.Null(acceptance.WorstSteady);
        Assert.Null(acceptance.DescriptorHeadroom);
        Assert.False(acceptance.RetentionBounded);
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
            SteadyPeakSessions: AcceptanceLoadSessions));
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
            SteadyPeakSessions: AcceptanceLoadSessions));
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
            SteadyPeakSessions: AcceptanceLoadSessions));
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
    public void TheDrainAssociationTermReadsTheServersOwnControlConnections()
    {
        // A session can be gone from the coordinator while the harness server still sees the control
        // connection it dialed. The association term is the server's own count, so it fires on that
        // shape even though the sessions term is satisfied — a subject the sessions term cannot show.
        var stranded = Acceptance("--rate", "100", "--churn-seconds", "90");
        stranded.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: 0, associations: 2)],
            Drain(9_000, associations: 2),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: 0,
            SteadyPeakSessions: 0));

        Assert.False(stranded.Passed);
        Assert.Contains("control connection(s) the harness SOCKS5 server still saw open", Assert.Single(stranded.Failures), StringComparison.Ordinal);

        // And the converse: a live session whose control connection the server has already seen close
        // is the sessions term's failure alone.
        var liveSessions = Acceptance("--rate", "100", "--churn-seconds", "90");
        liveSessions.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: 2, associations: 0)],
            Drain(9_000, sessions: 2, associations: 0),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: 2,
            SteadyPeakSessions: 2));

        Assert.False(liveSessions.Passed);
        Assert.Contains("ended with 2 live sessions", Assert.Single(liveSessions.Failures), StringComparison.Ordinal);

        // No observation at all (an out-of-process harness server): the association term is left
        // unevaluated instead of being answered with the session count.
        var unobserved = Acceptance("--rate", "100", "--churn-seconds", "90");
        unobserved.Evaluate(new SessionBudgetOutcome(
            [Sample(sessions: 2)],
            Drain(9_000),
            Accepted: 9_000,
            Rejected: 0,
            ChurnPeakSessions: 2,
            SteadyPeakSessions: 2));

        Assert.True(unobserved.Passed, string.Join(" | ", unobserved.Failures));
        // The descriptor budget charges the shipped one-per-session allowance where the observation is
        // missing, so its control-connection term still describes the shape rather than disappearing.
        Assert.Equal(SessionBudgetMath.FileDescriptorBudget(2, 2) + 32, unobserved.DescriptorHeadroom);
    }

    [Fact]
    public void SampleRowsCarryTheBaselinesAndRecomputeTheirRatios()
    {
        var sample = Sample(
            phase: "churn",
            elapsedSeconds: 60,
            sessions: 1_000,
            associations: 998,
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
        // The associations column is the server's own observation, not the session count repeated: it
        // carries the count that was taken, and an unobserved count is omitted from the row rather
        // than reported as a number nobody read (an out-of-process harness server).
        Assert.Equal(998, root.GetProperty("associations").GetInt32());
        using var unobserved = JsonDocument.Parse(StabilityContext.SerializeMetrics(Sample(associations: null).ToRow()));
        Assert.False(unobserved.RootElement.TryGetProperty("associations", out _));
    }

    private static SessionBudgetAcceptance Acceptance(params string[] args)
        => new(Options(args), s_idle, s_sweep, RelayReceiveBufferBytes);

    private static SoakOptions Options(params string[] args)
        => SoakOptions.Parse(["--scenario", "udpSessionBudget", .. args]);

    /// <summary>
    /// The drain-end sample of a clean run: nothing live and the descriptor count back at the baseline.
    /// The association count defaults to no observation, i.e. the out-of-process-server shape.
    /// </summary>
    private static SessionBudgetSample Drain(long accepted, int sessions = 0, int? associations = null)
        => Sample(phase: "drainEnd", elapsedSeconds: 300, sessions: sessions, associations: associations, fileDescriptors: 100, baselineFileDescriptors: 100, accepted: accepted);

    /// <summary>
    /// A synthetic sample. The estimated receive buffer defaults to the value consistent with the
    /// population (<c>sessions × the configured relay buffer</c>); pass it explicitly to model an
    /// inflated or misattributed estimate. <paramref name="associations"/> is the server's own live
    /// control-connection count; null models a run where nobody observed it.
    /// </summary>
    private static SessionBudgetSample Sample(
        string phase = "churn",
        double elapsedSeconds = 60,
        int sessions = 0,
        int? associations = null,
        int fileDescriptors = 0,
        int baselineFileDescriptors = 0,
        long managedBytes = 0,
        long baselineManagedBytes = 0,
        long relayReceiveBufferBytes = -1,
        long accepted = 0,
        long rejected = 0,
        long datagramsLost = 0,
        long misdelivered = 0,
        long capacityRejections = 0,
        long setupRejections = 0,
        long setupFailures = 0)
        => new(
            Phase: phase,
            Index: 1,
            ElapsedSeconds: elapsedSeconds,
            Sessions: sessions,
            Associations: associations,
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
            Misdelivered: misdelivered,
            CapacityRejections: capacityRejections,
            SetupRejections: setupRejections,
            SetupFailures: setupFailures);
}
