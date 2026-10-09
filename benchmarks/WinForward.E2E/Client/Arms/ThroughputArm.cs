using System.Diagnostics;
using System.Net.Sockets;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal sealed class ThroughputStreamState
{
    internal long _bytesSent;
    internal long _bytesEchoed;
    internal long _framesSent;
    internal long _framesEchoed;
    internal long _corrupt;
    internal long _protocolErrors;
    internal long _sendFailures;
    internal long _connectFailures;
}

internal sealed class AggregateRateLimiter
{
    private readonly Lock _gate = new();
    private readonly double _bytesPerSecond;
    private readonly long _totalBudget;
    private readonly long _startTicks;
    private long _reserved;
    private bool _budgetExhausted;

    internal AggregateRateLimiter(long bytesPerSecond, long totalBudget, long startTicks)
    {
        _bytesPerSecond = Math.Max(1, bytesPerSecond);
        _totalBudget = totalBudget;
        _startTicks = startTicks;
    }

    /// <summary>
    /// Whether a frame was refused because the declared budget had less than one frame left. The
    /// budget is never spent to the byte -- a reservation that would overshoot it is refused whole --
    /// so "reserved == budget" is not the exhaustion signal, this is.
    /// </summary>
    internal bool BudgetExhausted
    {
        get
        {
            lock (_gate)
            {
                return _budgetExhausted;
            }
        }
    }

    internal bool TryReserve(int bytes, out long waitUntilTicks)
    {
        waitUntilTicks = 0;
        lock (_gate)
        {
            if (_reserved + bytes > _totalBudget)
            {
                _budgetExhausted = true;
                return false;
            }

            _reserved += bytes;
            var allowedByNow = _bytesPerSecond * (Stopwatch.GetTimestamp() - _startTicks) / Stopwatch.Frequency;
            if (_reserved > allowedByNow)
            {
                waitUntilTicks = _startTicks + (long)(_reserved / _bytesPerSecond * Stopwatch.Frequency);
            }

            return true;
        }
    }
}

internal static class ThroughputArm
{
    private const int DefaultStreams = 4;
    private const long DefaultTargetBytesPerSecond = 25_000_000;
    private const int FramePayloadBytes = 32 * 1024;
    private const uint ConnectionIdBase = 0x5448_0000u;

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var spec = context.Spec;
        var streams = spec.Streams > 0 ? spec.Streams : DefaultStreams;
        var targetBytesPerSecond = spec.TargetBytesPerSecond > 0 ? spec.TargetBytesPerSecond : DefaultTargetBytesPerSecond;
        const int frameLength = FrameCodec.HeaderSize + FramePayloadBytes + FrameCodec.TrailerSize;
        var budget = (long)(targetBytesPerSecond * spec.Seconds);

        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var cancellationToken = linked.Token;

        var limiter = new AggregateRateLimiter(targetBytesPerSecond, budget, startTicks);
        var states = new ThroughputStreamState[streams];
        var tasks = new Task[streams];
        for (var index = 0; index < streams; index++)
        {
            states[index] = new ThroughputStreamState();
            var streamIndex = index;
            tasks[index] = DedicatedThread.RunOnOwnThreadAsync(() => RunStreamAsync(context, states[streamIndex], streamIndex, limiter, frameLength, deadlineTicks, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var elapsedTicks = Clock.Now - startTicks;
        var metrics = BuildMetrics(states, elapsedTicks, budget, targetBytesPerSecond, frameLength, limiter.BudgetExhausted);
        return new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = spec.Seconds,
                Streams = streams,
                TargetBytesPerSecond = targetBytesPerSecond,
                FramePayloadBytes = FramePayloadBytes,
            },
            Metrics = metrics,
            Gates =
            {
                // This arm reserves a frame against its byte budget before offering it, so a frame the
                // budget refuses is never a sample it destroyed; a send that threw is the one way this
                // arm loses a frame of its own, and that is the counter behind sendFailures.
                [ArmKeys.Common.Gates.ClientSendLoss] = metrics.SendFailures,
                [ArmKeys.Common.Gates.WindowMs] = 0L,
            },
            Notes =
            {
                "bytes counts echoed frame bytes that the client read back; the echo path is the measured transfer, so ingress and egress are both exercised.",
                "the aggregate send rate is paced to targetBytesPerSecond so per-stream counters stay comparable across proxifiers.",
                "streamConnects, connectFailures and sendFailures are the stream counters behind bytesSent and bytes: a run where no stream connected publishes framesSent = 0 with connectFailures = streams, so an arm that transferred nothing says which end failed instead of reading as an arm that offered nothing.",
                "budgetReached is true only when a stream was refused a frame because fewer than framePayloadBytes remained of budgetBytes, i.e. the run ended because the budget was spent rather than because seconds elapsed; a frame is never part-budgeted, so bytesSent stays below budgetBytes and budgetRemainingBytes is that unused remainder.",
            },
        };
    }

    private static ThroughputMetrics BuildMetrics(
        ThroughputStreamState[] states,
        long elapsedTicks,
        long budget,
        long targetBytesPerSecond,
        int frameLength,
        bool budgetExhausted)
    {
        var totals = new ThroughputTotals();
        foreach (var state in states)
        {
            totals.Add(state);
        }

        var elapsedSeconds = Clock.ToSeconds(elapsedTicks);
        return new ThroughputMetrics
        {
            Bytes = totals._bytesEchoed,
            BytesSent = totals._bytesSent,
            Frames = frameLength == 0 ? 0 : totals._bytesEchoed / frameLength,
            FramesSent = totals._framesSent,
            FramesEchoed = totals._framesEchoed,
            StreamConnects = states.Length - totals._connectFailures,
            ConnectFailures = totals._connectFailures,
            SendFailures = totals._sendFailures,
            BudgetBytes = budget,
            BudgetReached = budgetExhausted,
            BudgetRemainingBytes = Math.Max(0, budget - totals._bytesSent),
            ElapsedSeconds = NumberFormat.Round(elapsedSeconds, 4),
            GoodputBps = NumberFormat.Round(totals._bytesEchoed / elapsedSeconds),
            GoodputMbps = NumberFormat.Round(totals._bytesEchoed * 8.0 / elapsedSeconds / 1_000_000.0, 4),
            PerStreamMinBytes = totals._minEchoed == long.MaxValue ? 0 : totals._minEchoed,
            PerStreamMaxBytes = totals._maxEchoed,
            Corrupt = totals._corrupt,
            ProtocolErrors = totals._protocolErrors,
            TargetBytesPerSecond = targetBytesPerSecond,
        };
    }

    /// <summary>Arm-wide stream totals, folded in one pass so a total and the per-stream counters cannot drift.</summary>
    private sealed class ThroughputTotals
    {
        internal long _bytesSent;
        internal long _bytesEchoed;
        internal long _framesSent;
        internal long _framesEchoed;
        internal long _sendFailures;
        internal long _connectFailures;
        internal long _corrupt;
        internal long _protocolErrors;
        internal long _minEchoed = long.MaxValue;
        internal long _maxEchoed;

        internal void Add(ThroughputStreamState state)
        {
            _bytesSent += state._bytesSent;
            _bytesEchoed += state._bytesEchoed;
            _framesSent += state._framesSent;
            _framesEchoed += state._framesEchoed;
            _sendFailures += state._sendFailures;
            _connectFailures += state._connectFailures;
            _corrupt += state._corrupt;
            _protocolErrors += state._protocolErrors;
            _minEchoed = Math.Min(_minEchoed, state._bytesEchoed);
            _maxEchoed = Math.Max(_maxEchoed, state._bytesEchoed);
        }
    }

    private static async Task RunStreamAsync(
        ArmContext context,
        ThroughputStreamState state,
        int streamIndex,
        AggregateRateLimiter limiter,
        int frameLength,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateTcpSocket();
        if (!(await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            Interlocked.Increment(ref state._connectFailures);
            return;
        }

        var frame = new FrameBuffer(FramePayloadBytes);
        var connectionId = ConnectionIdBase + (uint)streamIndex;
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await SocketOps.SendCommandAsync(socket, connectionId, TcpMode.Clean, 0, streamCancellation.Token).ConfigureAwait(false);
        var receive = ReceiveLoopAsync(socket, state, streamCancellation.Token);

        try
        {
            ulong sequence = 0;
            while (Clock.Now < deadlineTicks && !streamCancellation.IsCancellationRequested)
            {
                if (!limiter.TryReserve(frameLength, out var waitUntilTicks))
                {
                    break;
                }

                if (waitUntilTicks > 0)
                {
                    await Pacer.WaitUntilAsync(waitUntilTicks, streamCancellation.Token).ConfigureAwait(false);
                }

                var length = frame.Build(connectionId, ++sequence, Clock.Now);
                await socket.SendAsync(frame.Memory[..length], SocketFlags.None, streamCancellation.Token).ConfigureAwait(false);
                Interlocked.Add(ref state._bytesSent, length);
                Interlocked.Increment(ref state._framesSent);
            }
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref state._sendFailures);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }

        SocketOps.ShutdownQuietly(socket, SocketShutdown.Send);
        await streamCancellation.CancelAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
    }

    private static async Task ReceiveLoopAsync(Socket socket, ThroughputStreamState state, CancellationToken cancellationToken)
    {
        var reader = new FrameStreamReader(socket, 128 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (status == FrameReadStatus.EndOfStream)
                {
                    break;
                }

                if (status == FrameReadStatus.BadChecksum)
                {
                    Interlocked.Increment(ref state._corrupt);
                    continue;
                }

                if (status != FrameReadStatus.Frame)
                {
                    Interlocked.Increment(ref state._protocolErrors);
                    break;
                }

                Interlocked.Add(ref state._bytesEchoed, reader.Raw.Length);
                Interlocked.Increment(ref state._framesEchoed);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref state._protocolErrors);
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }
}
