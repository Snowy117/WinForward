using System.Collections.Concurrent;
using System.Globalization;
using WinForward.E2E.Contracts;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal static class ReliabilityArm
{
    private const int DefaultConnectionsPerSecond = 20;
    private const int DefaultExpectedBytes = 8192;
    internal const int FramePayloadBytes = 1024;
    private const uint ConnectionIdBase = 0x5245_0000u;

    // Bounds how many attempts may be in flight at once, so that a product which never retires a
    // connection throttles the pacer instead of growing the list without limit.
    private const int MaxInFlightAttempts = 512;

    internal static readonly string[] s_outcomeNames =
    [
        "clean",
        "reset",
        "unexpectedEof",
        "timeout",
        "connectFail",
        "halfCloseViolation",
        "otherError",
    ];

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var spec = context.Spec;
        var rate = spec.ConnectionsPerSecond > 0 ? spec.ConnectionsPerSecond : DefaultConnectionsPerSecond;
        var expectedBytes = spec.ExpectedBytes > 0 ? spec.ExpectedBytes : DefaultExpectedBytes;
        var mixText = string.IsNullOrEmpty(spec.ModeMix) ? ArmSpec.DefaultModeMix : spec.ModeMix;

        if (!PlanFile.TryParseModeMix(mixText, out var weights, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var schedule = BuildSchedule(weights);
        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var cancellationToken = linked.Token;
        var results = new ConcurrentBag<ReliabilityAttempt>();
        var evidence = new AttemptEvidence(context.Sink);

        var scheduled = await Dedicated.RunOnOwnThreadAsync(() => PumpAsync(context, schedule, expectedBytes, rate, startTicks, deadlineTicks, results, evidence, cancellationToken)).ConfigureAwait(false);

        var outcome = new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = spec.Seconds,
                ConnectionsPerSecond = rate,
                ExpectedBytes = expectedBytes,
                ModeMix = mixText,
                FramePayloadBytes = FramePayloadBytes,
            },
            Metrics = ReliabilityMetricsWriter.BuildMetrics([.. results], schedule, mixText, expectedBytes, Clock.Now - startTicks, scheduled, evidence),
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = 0,
                [ArmKeys.Common.Gates.WindowMs] = 0,
            },
        };
        outcome.Notes.Add("outcome values are what the client observed; 'expected' is what the requested mode calls for, and fidelityMismatch counts any divergence plus truncated echoes.");
        outcome.Notes.Add("partialFin is expected to end as unexpectedEof: the server closes its send side while the client has deliberately not half-closed.");
        outcome.Notes.Add("echoedBytes and trailerBytes are kept for every attempt whatever its outcome, so an attempt that timed out or reset still carries the echo and trailer evidence the verdict alone cannot; trailerBytes counts the bytes read after expectedBytes of echo, and the trailer the target writes is TrailerProtocol.TotalBytes.");
        outcome.Notes.Add("byMode is the joint mode x observed distribution: outcomes gives the marginals, and only byMode says which mode produced them. Each mode also carries its truncated count and its echoed/trailer byte totals and extremes, so an outcome that is not uniform across the schedule is visible without guessing.");
        outcome.Notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"the arm writes one 'attempt' record per disconfirming attempt (observed != expected, or truncated) and one in every {AttemptEvidence.AttemptSampleStride} of the ordinary ones, capped at {AttemptEvidence.MaxAttemptRecords} records for the whole arm: the records carry connectionId, mode, observed and the echo/trailer evidence so a client observation can be joined 1:1 with the target ledger's verdict, and attemptRecordsOmitted counts the qualifying attempts that did not fit the cap."));
        return outcome;
    }

    private static async Task<long> PumpAsync(
        ArmContext context,
        List<TcpMode> schedule,
        int expectedBytes,
        int rate,
        long startTicks,
        long deadlineTicks,
        ConcurrentBag<ReliabilityAttempt> results,
        AttemptEvidence evidence,
        CancellationToken cancellationToken)
    {
        var pacer = new Pacer(rate, startTicks);
        using var slots = new SemaphoreSlim(MaxInFlightAttempts, MaxInFlightAttempts);
        var pending = new List<Task>();
        long index = 0;
        while (Clock.Now < deadlineTicks)
        {
            var intended = pacer.IntendedTicks(index);
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
            // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
            Pacer.WaitUntil(intended, cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042
            // Back-pressure, not discard: when the product cannot retire attempts fast enough the
            // pacer slips and achievedRate reports it, but every attempt that did run is tallied.
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            var mode = schedule[(int)(index % schedule.Count)];
            var connectionId = ConnectionIdBase + (uint)(index & 0xFFFF);
            index++;
            pending.Add(CollectAsync(ReliabilityExchange.RunAttemptAsync(context, mode, expectedBytes, connectionId, intended, cancellationToken), results, slots, evidence));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        return index;
    }

    private static async Task CollectAsync(
        Task<ReliabilityAttempt> attempt,
        ConcurrentBag<ReliabilityAttempt> results,
        SemaphoreSlim slots,
        AttemptEvidence evidence)
    {
        try
        {
            var completed = await attempt.ConfigureAwait(false);
            results.Add(completed);

            // Waits for the evidence record, so a slow sink applies back-pressure to this attempt
            // slot instead of queueing records without limit.
            await evidence.RecordAsync(completed, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }

    private static List<TcpMode> BuildSchedule(List<ModeWeight> weights)
    {
        var divisor = 0;
        foreach (var weight in weights)
        {
            divisor = GreatestCommonDivisor(divisor, weight.Weight);
        }

        if (divisor <= 0)
        {
            divisor = 1;
        }

        var schedule = new List<TcpMode>();
        foreach (var weight in weights)
        {
            for (var repeat = 0; repeat < weight.Weight / divisor; repeat++)
            {
                schedule.Add(weight.Mode);
            }
        }

        return schedule;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}
