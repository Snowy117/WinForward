using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal enum ReliabilityOutcome
{
    Clean,
    Reset,
    UnexpectedEof,
    Timeout,
    ConnectFail,
    HalfCloseViolation,
    OtherError,
}

internal enum ExchangeStatus
{
    Completed,
    ConnectFail,
    Cancelled,
    Timeout,
}

[StructLayout(LayoutKind.Auto)]
internal struct ExchangeResult
{
    internal ExchangeStatus _status;
    internal bool _reset;
}

internal sealed class ReliabilityAttempt
{
    internal ReliabilityOutcome Observed { get; set; } = ReliabilityOutcome.OtherError;

    internal ReliabilityOutcome Expected { get; init; } = ReliabilityOutcome.OtherError;

    internal TcpMode Mode { get; init; } = TcpMode.Clean;

    internal bool Truncated { get; set; }

    internal long ConnectTicks { get; set; }

    internal long TransferTicks { get; set; }

    /// <summary>
    /// Whether the request send completed, which is what makes <see cref="TransferTicks"/> a sample:
    /// a send that finished inside one stopwatch tick is a real near-zero sample, so the mean's
    /// denominator cannot be "ticks > 0" without biasing it upward.
    /// </summary>
    internal bool TransferMeasured { get; set; }

    /// <summary>
    /// The connection id the attempt used, so its client-side observation can be joined 1:1 with the
    /// target ledger's verdict for the same connection.
    /// </summary>
    internal uint ConnectionId { get; init; }

    internal ExchangeStatus Status { get; set; } = ExchangeStatus.Completed;

    /// <summary>
    /// What the client saw on the wire, kept for every attempt whatever its outcome. A verdict of
    /// "timeout" or "reset" on its own cannot say whether the echo or the trailer was cut short,
    /// and those are exactly the attempts a half-close investigation has to dissect.
    /// </summary>
    internal long Echoed { get; set; }

    internal long TrailerBytes { get; set; }

    internal bool Eof { get; set; }

    internal bool Reset { get; set; }

    internal bool ProtocolError { get; set; }

    internal bool OtherError { get; set; }
}

internal sealed class ReliabilityTally
{
    internal long[] _observed = [];
    internal long[] _expected = [];
    internal long _truncated;
    internal long _mismatches;
    internal long _connectFailures;
    internal long _unexpectedEof;
    internal long _expectedEarlyEof;
    internal long _connectTicks;
    internal long _connectSamples;
    internal long _transferTicks;
    internal long _transferSamples;
    internal long _echoed;
    internal long _trailerBytes;
}

internal static class ReliabilityArm
{
    private const int DefaultConnectionsPerSecond = 20;
    private const int DefaultExpectedBytes = 8192;
    private const int FramePayloadBytes = 1024;
    private const int AttemptTimeoutMilliseconds = 10_000;
    private const uint ConnectionIdBase = 0x5245_0000u;

    // Bounds how many attempts may be in flight at once, so that a product which never retires a
    // connection throttles the pacer instead of growing the list without limit.
    private const int MaxInFlightAttempts = 512;

    // Ceiling on the per-attempt evidence records one arm may write, so a multi-hour campaign
    // cannot grow the file with the arm's runtime. The budget is spent on the disconfirming
    // attempts first -- observed != expected, or a truncated echo -- because those are what a
    // client observation has to be joined against the target's ledger; the ordinary attempts are
    // sampled at one in AttemptSampleStride, which is a fraction of the arm's own attempt count.
    private const int MaxAttemptRecords = 4096;
    private const int AttemptSampleStride = 16;

    private static readonly string[] s_outcomeNames =
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
            Metrics = BuildMetrics([.. results], schedule, mixText, expectedBytes, Clock.Now - startTicks, scheduled, evidence),
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
            $"the arm writes one 'attempt' record per disconfirming attempt (observed != expected, or truncated) and one in every {AttemptSampleStride} of the ordinary ones, capped at {MaxAttemptRecords} records for the whole arm: the records carry connectionId, mode, observed and the echo/trailer evidence so a client observation can be joined 1:1 with the target ledger's verdict, and attemptRecordsOmitted counts the qualifying attempts that did not fit the cap."));
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
            pending.Add(CollectAsync(RunAttemptAsync(context, mode, expectedBytes, connectionId, intended, cancellationToken), results, slots, evidence));
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

    /// <summary>
    /// Per-attempt evidence written into the arm's own output file as <c>type: "attempt"</c> records.
    /// It exists so a client observation can be joined 1:1 with the target ledger's verdict for the
    /// same connectionId, which is what turns "the target says reset, the client says
    /// unexpectedEof" from an inference into a proof. What it writes is bounded by the arm's own
    /// constants, never by the arm's runtime: at most <see cref="MaxAttemptRecords"/> records, spent
    /// on the disconfirming attempts first, with the ordinary attempts sampled at one in
    /// <see cref="AttemptSampleStride"/>. The two counters say how much of the arm the file covers.
    /// The type is visible to the test assembly so the record's keys can be asserted against
    /// <see cref="ArmKeys.Reliability.Attempt"/>.
    /// </summary>
    internal sealed class AttemptEvidence
    {
        private readonly JsonlSink _sink;
        private long _seen;
        private long _claimed;
        private long _written;
        private long _omitted;

        internal AttemptEvidence(JsonlSink sink)
        {
            _sink = sink;
        }

        internal long Written => Interlocked.Read(ref _written);

        internal long Omitted => Interlocked.Read(ref _omitted);

        internal async ValueTask RecordAsync(ReliabilityAttempt attempt, CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref _seen);
            var disconfirming = attempt.Observed != attempt.Expected || attempt.Truncated;
            if (!disconfirming && ordinal % AttemptSampleStride != 0)
            {
                return;
            }

            // Each caller claims a distinct ordinal, so the cap is exact however many attempts are
            // in flight: the (MaxAttemptRecords+1)-th claim and every later one is counted, not written.
            if (Interlocked.Increment(ref _claimed) > MaxAttemptRecords)
            {
                Interlocked.Increment(ref _omitted);
                return;
            }

            Interlocked.Increment(ref _written);
            await _sink.WriteAsync(
                writer =>
                {
                    writer.WriteString(ArmKeys.Common.Record.Type, "attempt");
                    writer.WriteNumber(ArmKeys.Reliability.Attempt.ConnectionId, attempt.ConnectionId);
                    writer.WriteString(ArmKeys.Reliability.Attempt.Mode, TcpCommand.Name(attempt.Mode));
                    writer.WriteString(ArmKeys.Reliability.Attempt.Status, StatusName(attempt.Status));
                    writer.WriteString(ArmKeys.Reliability.Attempt.Observed, s_outcomeNames[(int)attempt.Observed]);
                    writer.WriteString(ArmKeys.Reliability.Attempt.Expected, s_outcomeNames[(int)attempt.Expected]);
                    writer.WriteBoolean(ArmKeys.Reliability.Attempt.Truncated, attempt.Truncated);
                    writer.WriteNumber(ArmKeys.Reliability.Attempt.EchoedBytes, attempt.Echoed);
                    writer.WriteNumber(ArmKeys.Reliability.Attempt.TrailerBytes, attempt.TrailerBytes);
                    writer.WriteBoolean(ArmKeys.Reliability.Attempt.Eof, attempt.Eof);
                    writer.WriteBoolean(ArmKeys.Reliability.Attempt.Reset, attempt.Reset);
                    writer.WriteBoolean(ArmKeys.Reliability.Attempt.ProtocolError, attempt.ProtocolError);
                    writer.WriteBoolean(ArmKeys.Reliability.Attempt.OtherError, attempt.OtherError);
                    writer.WriteNumber(ArmKeys.Reliability.Attempt.ConnectTicks, attempt.ConnectTicks);
                    writer.WriteNumber(ArmKeys.Reliability.Attempt.TransferTicks, attempt.TransferTicks);
                },
                cancellationToken).ConfigureAwait(false);
        }

        private static string StatusName(ExchangeStatus status) => status switch
        {
            ExchangeStatus.Completed => "exchanged",
            ExchangeStatus.ConnectFail => "connectFail",
            ExchangeStatus.Cancelled => "cancelled",
            _ => "timeout",
        };
    }

    /// <summary>
    /// The record a finished run publishes: the tallies and rates derived from the attempts that ran,
    /// folded once, together with what the evidence writer managed to record.
    /// </summary>
    private static ReliabilityMetrics BuildMetrics(
        ReliabilityAttempt[] attempts,
        List<TcpMode> schedule,
        string mixText,
        int expectedBytes,
        long elapsedTicks,
        long scheduled,
        AttemptEvidence evidence)
    {
        var tally = TallyAttempts(attempts);
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
    private static ReliabilityOutcomes Outcomes(long[] values) => new()
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

    /// <summary>One mode's slice of the joint distribution, accumulated in a single pass over the attempts.</summary>
    private sealed class ModeTally
    {
        private readonly long[] _observed = new long[s_outcomeNames.Length];
        private long _count;
        private long _truncated;
        private long _echoed;
        private long _trailer;
        private long _minEchoed = long.MaxValue;
        private long _maxEchoed;
        private long _minTrailer = long.MaxValue;
        private long _maxTrailer;

        internal void Add(ReliabilityAttempt attempt)
        {
            _observed[(int)attempt.Observed]++;
            _count++;
            _echoed += attempt.Echoed;
            _trailer += attempt.TrailerBytes;
            _minEchoed = Math.Min(_minEchoed, attempt.Echoed);
            _maxEchoed = Math.Max(_maxEchoed, attempt.Echoed);
            _minTrailer = Math.Min(_minTrailer, attempt.TrailerBytes);
            _maxTrailer = Math.Max(_maxTrailer, attempt.TrailerBytes);
            if (attempt.Truncated)
            {
                _truncated++;
            }
        }

        internal ReliabilityModeMetrics ToRecord() => new()
        {
            Attempts = _count,
            Observed = Outcomes(_observed),
            Truncated = _truncated,
            EchoedBytes = _echoed,
            TrailerBytes = _trailer,
            MinEchoedBytes = _count == 0 ? null : _minEchoed,
            MaxEchoedBytes = _count == 0 ? null : _maxEchoed,
            MinTrailerBytes = _count == 0 ? null : _minTrailer,
            MaxTrailerBytes = _count == 0 ? null : _maxTrailer,
        };
    }

    private static ReliabilityTally TallyAttempts(ReliabilityAttempt[] attempts)
    {
        var tally = new ReliabilityTally
        {
            _observed = new long[s_outcomeNames.Length],
            _expected = new long[s_outcomeNames.Length],
        };

        foreach (var attempt in attempts)
        {
            tally._observed[(int)attempt.Observed]++;
            tally._expected[(int)attempt.Expected]++;
            tally._connectTicks += attempt.ConnectTicks;
            tally._transferTicks += attempt.TransferTicks;
            tally._echoed += attempt.Echoed;
            tally._trailerBytes += attempt.TrailerBytes;
            if (attempt.ConnectTicks > 0)
            {
                tally._connectSamples++;
            }

            if (attempt.TransferMeasured)
            {
                tally._transferSamples++;
            }

            if (attempt.Truncated)
            {
                tally._truncated++;
            }

            if (attempt.Observed != attempt.Expected || attempt.Truncated)
            {
                tally._mismatches++;
            }

            if (attempt.Observed == ReliabilityOutcome.ConnectFail)
            {
                tally._connectFailures++;
            }

            if (attempt.Observed != ReliabilityOutcome.UnexpectedEof)
            {
                continue;
            }

            if (attempt.Mode == TcpMode.PartialFin)
            {
                tally._expectedEarlyEof++;
            }
            else
            {
                tally._unexpectedEof++;
            }
        }

        return tally;
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

    private static ReliabilityOutcome ExpectedOutcome(TcpMode mode) => mode switch
    {
        TcpMode.ResetAfterN => ReliabilityOutcome.Reset,
        TcpMode.PartialFin => ReliabilityOutcome.UnexpectedEof,
        _ => ReliabilityOutcome.Clean,
    };

    private static async Task<ReliabilityAttempt> RunAttemptAsync(
        ArmContext context,
        TcpMode mode,
        int expectedBytes,
        uint connectionId,
        long intendedTicks,
        CancellationToken cancellationToken)
    {
        var attempt = new ReliabilityAttempt { Mode = mode, Expected = ExpectedOutcome(mode), ConnectionId = connectionId };
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(AttemptTimeoutMilliseconds);
        var token = attemptCancellation.Token;
        using var socket = context.CreateTcpSocket();

        var result = await ExchangeAsync(context, socket, attempt, mode, expectedBytes, connectionId, intendedTicks, token, cancellationToken).ConfigureAwait(false);
        attempt.Status = result._status;
        attempt.Reset = result._reset;
        switch (result._status)
        {
            case ExchangeStatus.ConnectFail:
                attempt.Observed = ReliabilityOutcome.ConnectFail;
                return attempt;
            case ExchangeStatus.Cancelled:
                return attempt;
            case ExchangeStatus.Timeout:
                attempt.Observed = ReliabilityOutcome.Timeout;
                return attempt;
            case ExchangeStatus.Completed:
                // A reset after N bytes legitimately destroys whatever echo was still buffered, so only
                // modes whose contract is a complete echo can be truncated.
                attempt.Truncated = mode != TcpMode.ResetAfterN && attempt.Echoed < expectedBytes;
                attempt.Observed = Classify(mode, attempt.Reset, attempt.Eof, attempt.ProtocolError, attempt.OtherError, attempt.Echoed, attempt.TrailerBytes, expectedBytes);
                return attempt;
            default:
                // A status this arm has no classification for is a harness defect, not a smaller
                // measurement: a completed-looking attempt would hide the new mode from every gate.
                throw new InvalidOperationException($"the exchange ended with an unclassified status: {result._status}");
        }
    }

    private static async ValueTask<ExchangeResult> ExchangeAsync(
        ArmContext context,
        Socket socket,
        ReliabilityAttempt attempt,
        TcpMode mode,
        int expectedBytes,
        uint connectionId,
        long intendedTicks,
        CancellationToken token,
        CancellationToken cancellationToken)
    {
        var sessionStart = Clock.Now;
        var result = new ExchangeResult { _status = ExchangeStatus.Completed };

        try
        {
            if (!await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, token).ConfigureAwait(false))
            {
                result._status = ExchangeStatus.ConnectFail;
                return result;
            }

            attempt.ConnectTicks = Clock.Now - sessionStart;
            context.Latency.TcpConnect.Record(Clock.ToNanoseconds(Clock.Now - intendedTicks));

            // The transfer is the request send on its own: sessionStart predates the connect, so
            // timing from it would publish connect + send under a name that promises a transfer.
            var sendStart = Clock.Now;
            await SendRequestAsync(socket, connectionId, mode, expectedBytes, token).ConfigureAwait(false);
            attempt.TransferTicks = Clock.Now - sendStart;
            attempt.TransferMeasured = true;

            if (mode is TcpMode.Clean or TcpMode.HalfClose or TcpMode.Stall)
            {
                SocketOps.ShutdownQuietly(socket, SocketShutdown.Send);
            }

            // Counted into the attempt as each frame arrives, so a receive that ends in a reset or a
            // timeout still carries what had already been observed.
            await ReceivePhaseAsync(socket, expectedBytes, attempt, token).ConfigureAwait(false);
            return result;
        }
        catch (SocketException exception)
        {
            if (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                result._reset = true;
            }
            else
            {
                attempt.OtherError = true;
            }
        }
        catch (OperationCanceledException)
        {
            result._status = cancellationToken.IsCancellationRequested ? ExchangeStatus.Cancelled : ExchangeStatus.Timeout;
        }
        catch (ObjectDisposedException)
        {
            attempt.OtherError = true;
        }
        catch (IOException)
        {
            attempt.OtherError = true;
        }

        return result;
    }

    private static async ValueTask ReceivePhaseAsync(Socket socket, int expectedBytes, ReliabilityAttempt attempt, CancellationToken cancellationToken)
    {
        var reader = new FrameStreamReader(socket, 32 * 1024);
        while (true)
        {
            var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (status)
            {
                case FrameReadStatus.EndOfStream:
                    attempt.Eof = true;
                    return;
                case FrameReadStatus.BadChecksum:
                    attempt.ProtocolError = true;
                    continue;
                case FrameReadStatus.BadMagic:
                case FrameReadStatus.BadLength:
                    attempt.ProtocolError = true;
                    return;
                case FrameReadStatus.Frame:
                    if (attempt.Echoed < expectedBytes)
                    {
                        attempt.Echoed += reader.Payload.Length;
                    }
                    else
                    {
                        attempt.TrailerBytes += reader.Payload.Length;
                    }

                    continue;
                default:
                    // An unclassified status is booked as a protocol error for this attempt rather
                    // than absorbed as a good frame.
                    attempt.ProtocolError = true;
                    return;
            }
        }
    }

    private static ReliabilityOutcome Classify(
        TcpMode mode,
        bool reset,
        bool eof,
        bool protocolError,
        bool otherError,
        long echoed,
        long trailerBytes,
        long expectedBytes)
    {
        if (otherError && !reset && !eof)
        {
            return ReliabilityOutcome.OtherError;
        }

        if (reset)
        {
            return mode == TcpMode.HalfClose ? ReliabilityOutcome.HalfCloseViolation : ReliabilityOutcome.Reset;
        }

        if (protocolError && !eof)
        {
            return ReliabilityOutcome.OtherError;
        }

        if (!eof)
        {
            return ReliabilityOutcome.Timeout;
        }

        return mode switch
        {
            TcpMode.HalfClose => echoed >= expectedBytes && trailerBytes >= TrailerProtocol.TotalBytes
                ? ReliabilityOutcome.Clean
                : ReliabilityOutcome.HalfCloseViolation,
            TcpMode.PartialFin => ReliabilityOutcome.UnexpectedEof,
            _ => echoed >= expectedBytes ? ReliabilityOutcome.Clean : ReliabilityOutcome.UnexpectedEof,
        };
    }

    private static async ValueTask SendRequestAsync(Socket socket, uint connectionId, TcpMode mode, int expectedBytes, CancellationToken cancellationToken)
    {
        await SocketOps.SendCommandAsync(socket, connectionId, mode, (uint)expectedBytes, cancellationToken).ConfigureAwait(false);

        var frame = new FrameBuffer(FramePayloadBytes);
        var sent = 0;
        ulong sequence = 0;
        while (sent < expectedBytes)
        {
            var chunk = Math.Min(FramePayloadBytes, expectedBytes - sent);
            sequence++;
            var length = frame.Build(connectionId, sequence, Clock.Now, chunk);
            await socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            sent += chunk;
        }
    }
}
