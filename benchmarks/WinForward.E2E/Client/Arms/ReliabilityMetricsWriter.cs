using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal static class ReliabilityMetricsWriter
{
    /// <summary>
    /// The arm's client send loss: the pacing slots the pacer offered that no attempt ever ran for.
    /// This arm back-pressures rather than discards -- a full attempt window makes the pacer slip,
    /// which <c>achievedRate</c> reports -- so the two counters are equal on every run that retired
    /// its attempts, and the difference is exactly the work the arm ended with still in flight.
    /// </summary>
    internal static long ClientSendLoss(long scheduledAttempts, long connectAttempts) =>
        Math.Max(0, scheduledAttempts - connectAttempts);

    /// <summary>
    /// The record a finished run publishes: the tallies and rates derived from the attempts that ran,
    /// folded once, together with what the evidence writer managed to record.
    /// </summary>
    internal static ReliabilityMetrics BuildMetrics(
        ReliabilityAttempt[] attempts,
        List<TcpMode> schedule,
        string mixText,
        int expectedBytes,
        long elapsedTicks,
        long scheduled,
        AttemptEvidence evidence)
    {
        var tally = ReliabilityTally.TallyAttempts(attempts);
        var observed = tally._observed;
        var expected = tally._expected;
        var truncated = tally._truncated;
        var mismatches = tally._mismatches;
        var connectFailures = tally._connectFailures;
        var unexpectedEof = tally._unexpectedEof;
        var expectedEarlyEof = tally._expectedEarlyEof;
        var connectTicks = tally._connectTicks;
        var connectSamples = tally._connectSamples;
        var transferTicks = tally._transferTicks;
        var transferSamples = tally._transferSamples;

        return new ReliabilityMetrics
        {
            ConnectAttempts = attempts.Length,
            ScheduledAttempts = scheduled,
            Outcomes = Outcomes(observed),
            Expected = Outcomes(expected),
            UnexpectedEof = unexpectedEof,
            ExpectedEarlyEof = expectedEarlyEof,
            Truncated = truncated,
            FidelityMismatch = mismatches,
            FidelityRate = JsonRate.Rate(mismatches, attempts.Length),
            ConnectFail = connectFailures,
            ExpectedBytes = expectedBytes,
            ModeSchedule = string.Join(',', schedule.ConvertAll(TcpCommand.Name)),
            EchoedBytes = tally._echoed,
            TrailerBytes = tally._trailerBytes,
            ByMode = BuildModeBreakdown(attempts, schedule),
            AttemptRecords = evidence.Written,
            AttemptRecordsOmitted = evidence.Omitted,
            MeanConnectMs = connectSamples == 0
                ? null
                : NumberFormat.Round(Clock.ToMicroseconds(connectTicks) / (double)connectSamples / 1000.0),

            // Over the attempts that completed a request send, never over every attempt: an attempt
            // that never connected has no send to average, and letting it in as a zero deflates the
            // mean.
            MeanTransferMs = transferSamples == 0
                ? null
                : NumberFormat.Round(Clock.ToMicroseconds(transferTicks) / (double)transferSamples / 1000.0),
            AchievedRate = JsonPerSecond.PerSecond(attempts.Length, elapsedTicks, System.Diagnostics.Stopwatch.Frequency),
            EffectiveModeMix = mixText,
        };
    }

    /// <summary>
    /// One outcome distribution, read from a tally indexed by <see cref="ReliabilityOutcome"/>. The
    /// properties are assigned from the member the tally counted rather than from a position in a
    /// name array, so an outcome cannot silently move from one name to another.
    /// </summary>
    internal static ReliabilityOutcomes Outcomes(long[] values) => new()
    {
        Clean = values[(int)ReliabilityOutcome.Clean],
        Reset = values[(int)ReliabilityOutcome.Reset],
        UnexpectedEof = values[(int)ReliabilityOutcome.UnexpectedEof],
        Timeout = values[(int)ReliabilityOutcome.Timeout],
        ConnectFail = values[(int)ReliabilityOutcome.ConnectFail],
        HalfCloseViolation = values[(int)ReliabilityOutcome.HalfCloseViolation],
        OtherError = values[(int)ReliabilityOutcome.OtherError],
    };

    /// <summary>
    /// The joint mode x observed distribution. The marginals in <c>outcomes</c> cannot say which
    /// mode produced a reset, a timeout or a half-close violation, and that is the question this arm
    /// exists to answer; every mode the schedule uses appears, including a mode that produced no
    /// attempt at all.
    /// </summary>
    private static Dictionary<string, ReliabilityModeMetrics> BuildModeBreakdown(ReliabilityAttempt[] attempts, List<TcpMode> schedule)
    {
        var tallies = new Dictionary<TcpMode, ModeTally>(schedule.Count);
        foreach (var mode in schedule)
        {
            tallies[mode] = new ModeTally();
        }

        foreach (var attempt in attempts)
        {
            tallies[attempt.Mode].Add(attempt);
        }

        var breakdown = new Dictionary<string, ReliabilityModeMetrics>(schedule.Count, StringComparer.Ordinal);
        foreach (var mode in schedule)
        {
            breakdown[TcpCommand.Name(mode)] = tallies[mode].ToRecord();
        }

        return breakdown;
    }
}
