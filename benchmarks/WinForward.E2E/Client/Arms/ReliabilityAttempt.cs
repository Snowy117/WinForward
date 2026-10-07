using System.Runtime.InteropServices;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
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
    // Ceiling on the per-attempt evidence records one arm may write, so a multi-hour campaign
    // cannot grow the file with the arm's runtime. The budget is spent on the disconfirming
    // attempts first -- observed != expected, or a truncated echo -- because those are what a
    // client observation has to be joined against the target's ledger; the ordinary attempts are
    // sampled at one in AttemptSampleStride, which is a fraction of the arm's own attempt count.
    internal const int MaxAttemptRecords = 4096;
    internal const int AttemptSampleStride = 16;

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
                writer.WriteString(ArmKeys.Reliability.Attempt.Observed, ReliabilityArm.s_outcomeNames[(int)attempt.Observed]);
                writer.WriteString(ArmKeys.Reliability.Attempt.Expected, ReliabilityArm.s_outcomeNames[(int)attempt.Expected]);
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
