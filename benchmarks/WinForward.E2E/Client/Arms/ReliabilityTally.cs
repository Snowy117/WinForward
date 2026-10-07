using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

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

    internal static ReliabilityTally TallyAttempts(ReliabilityAttempt[] attempts)
    {
        var tally = new ReliabilityTally
        {
            _observed = new long[ReliabilityArm.s_outcomeNames.Length],
            _expected = new long[ReliabilityArm.s_outcomeNames.Length],
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
}

/// <summary>One mode's slice of the joint distribution, accumulated in a single pass over the attempts.</summary>
internal sealed class ModeTally
{
    private readonly long[] _observed = new long[ReliabilityArm.s_outcomeNames.Length];
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
        Observed = ReliabilityMetricsWriter.Outcomes(_observed),
        Truncated = _truncated,
        EchoedBytes = _echoed,
        TrailerBytes = _trailer,
        MinEchoedBytes = _count == 0 ? null : _minEchoed,
        MaxEchoedBytes = _count == 0 ? null : _maxEchoed,
        MinTrailerBytes = _count == 0 ? null : _minTrailer,
        MaxTrailerBytes = _count == 0 ? null : _maxTrailer,
    };
}
