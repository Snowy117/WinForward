using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal sealed class PersistentCounters
{
    internal long _requests;
    internal long _responses;
    internal long _reconnects;
    internal long _sendWouldBlock;
    internal long _sendFailures;
    internal long _timeouts;
    internal long _remoteClosed;
    internal long _protocolErrors;
    internal long _corrupt;
    internal long _unmatchedReplies;
    internal long _connectFailures;
    internal long _connectSamples;
    internal long _connectTicks;
    internal long _generation;
    internal long _lastSendTicks;
    internal long _idleBeginTicks;
    internal long _idleEndTicks;
    internal long _idleGeneration;
    internal bool _idleEntered;
    internal bool _survivedIdle;
}

internal enum PersistentExchange
{
    Echoed,
    NotSent,
    NotEchoed,
}

internal enum PersistentFrame
{
    Matched,
    Skipped,
    Dead,
}

internal enum PersistentWait
{
    Readable,
    TimedOut,
    Faulted,
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct PersistentPlan
{
    internal PersistentPlan(int intervalMilliseconds, int idleSeconds, int payloadBytes, uint expectedBytes, long responseTimeoutTicks)
    {
        IntervalMilliseconds = intervalMilliseconds;
        IdleSeconds = idleSeconds;
        PayloadBytes = payloadBytes;
        ExpectedBytes = expectedBytes;
        ResponseTimeoutTicks = responseTimeoutTicks;
    }

    internal int IntervalMilliseconds { get; }

    internal int IdleSeconds { get; }

    internal int PayloadBytes { get; }

    internal uint ExpectedBytes { get; }

    internal long ResponseTimeoutTicks { get; }
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct PersistentSchedule
{
    internal PersistentSchedule(long startTicks, long deadlineTicks, long intervalTicks, int slots, int idleStartIndex, int idleEndIndex)
    {
        StartTicks = startTicks;
        DeadlineTicks = deadlineTicks;
        IntervalTicks = intervalTicks;
        Slots = slots;
        IdleStartIndex = idleStartIndex;
        IdleEndIndex = idleEndIndex;
    }

    internal long StartTicks { get; }

    internal long DeadlineTicks { get; }

    private long IntervalTicks { get; }

    internal int Slots { get; }

    internal int IdleStartIndex { get; }

    internal int IdleEndIndex { get; }

    internal bool HasIdleWindow => IdleStartIndex < Slots;

    internal long ScheduledIdleTicks => HasIdleWindow ? (IdleEndIndex - IdleStartIndex + 1) * IntervalTicks : 0;

    internal long IntendedTicks(int index) => StartTicks + (index * IntervalTicks);
}

internal sealed class PersistentLink : IDisposable
{
    internal PersistentLink(Socket socket, FrameStreamReader reader, uint connectionId)
    {
        Socket = socket;
        Reader = reader;
        ConnectionId = connectionId;
    }

    internal Socket Socket { get; }

    internal FrameStreamReader Reader { get; }

    internal uint ConnectionId { get; }

    public void Dispose()
    {
        SocketOps.ShutdownQuietly(Socket, SocketShutdown.Both);
        Socket.Dispose();
    }
}

internal static class PersistentArm
{
    private const int DefaultIntervalMilliseconds = 1000;
    private const int DefaultIdleSeconds = 20;
    private const int DefaultPayloadBytes = 120;
    private const int DefaultResponseTimeoutMilliseconds = 2000;
    private const uint ConnectionIdBase = 0x5045_0000u;

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var spec = context.Spec;
        var intervalMilliseconds = spec.IntervalMs > 0 ? spec.IntervalMs : DefaultIntervalMilliseconds;
        var idleSeconds = spec.IdleSeconds > 0 ? spec.IdleSeconds : DefaultIdleSeconds;
        var payloadBytes = spec.PayloadBytes > 0 ? spec.PayloadBytes : DefaultPayloadBytes;
        var expectedBytes = (uint)Math.Max(0, spec.ExpectedBytes);
        // The echo timeout only has to separate a slow round trip from a connection that will never
        // answer, and it may not be shorter than the pacing interval it has to report within.
        var responseTimeoutMilliseconds = Math.Max(DefaultResponseTimeoutMilliseconds, intervalMilliseconds);

        var outcome = new ArmOutcome
        {
            Parameters =
            {
                ["seconds"] = spec.Seconds,
                ["intervalMs"] = intervalMilliseconds,
                ["idleSeconds"] = idleSeconds,
                ["payloadBytes"] = payloadBytes,
                ["expectedBytes"] = spec.ExpectedBytes,
                ["responseTimeoutMs"] = responseTimeoutMilliseconds,
            },
        };

        var plan = new PersistentPlan(intervalMilliseconds, idleSeconds, payloadBytes, expectedBytes, Clock.FromSeconds(responseTimeoutMilliseconds / 1000.0));
        using var linked = context.CreateLinkedTokenSource();
        var cancellationToken = linked.Token;
        var state = new PersistentCounters();
        var schedule = await Dedicated.RunOnOwnThreadAsync(() => RunPersistentAsync(context, state, plan, cancellationToken)).ConfigureAwait(false);

        WriteMetrics(outcome, state, schedule, Clock.Now);
        outcome.Gates["clientSendLoss"] = state._connectFailures + state._sendFailures;
        outcome.Gates["windowMs"] = 0;
        outcome.Notes.Add("requests counts paced exchanges attempted and responses counts the echoes that completed them, each timed from its request's intended send instant; the difference is explained by connectFailures, sendFailures, timeouts, remoteClosed and protocolErrors.");
        outcome.Notes.Add("a request that finds its connection dead opens a replacement connection first and is counted as a reconnect: it records no tcp-rtt sample, so the latency histograms exclude both the reconnect and the round it carries, while responses and responseRate still count that round when it echoes.");
        outcome.Notes.Add("survivedIdle is the connection live when the idle window opened completing the first request after it; it is false when that request had to reconnect, when it failed, and when no idle window fitted inside the arm.");
        outcome.Notes.Add("idleSecondsScheduled is the whole number of pacing intervals the requested idle period was rounded to, shortened to what the arm can hold; idleSecondsObserved is the silence between the last request sent before the window and the first sent after it, so it includes any reconnect the first post-idle request needed.");
        outcome.Notes.Add("sendWouldBlock counts sends the kernel did not accept synchronously, and meanConnectMs is null when the arm never completed a connect rather than a zero that would read as an instantaneous one.");
        return outcome;
    }

    private static void WriteMetrics(ArmOutcome outcome, PersistentCounters state, PersistentSchedule schedule, long endTicks)
    {
        var elapsedTicks = Math.Max(0, endTicks - schedule.StartTicks);
        var idleTicks = 0L;
        if (state._idleEntered && state._idleBeginTicks != 0)
        {
            var idleEndTicks = state._idleEndTicks > state._idleBeginTicks ? state._idleEndTicks : endTicks;
            idleTicks = Math.Max(0, idleEndTicks - state._idleBeginTicks);
        }

        outcome.Metrics["requests"] = state._requests;
        outcome.Metrics["responses"] = state._responses;
        outcome.Metrics["reconnects"] = state._reconnects;
        outcome.Metrics["survivedIdle"] = state._survivedIdle;
        outcome.Metrics["idleSecondsScheduled"] = JsonValue.Round(Clock.ToSeconds(schedule.ScheduledIdleTicks));
        outcome.Metrics["idleSecondsObserved"] = JsonValue.Round(Clock.ToSeconds(idleTicks));
        outcome.Metrics["sendWouldBlock"] = state._sendWouldBlock;
        outcome.Metrics["sendFailures"] = state._sendFailures;
        outcome.Metrics["timeouts"] = state._timeouts;
        outcome.Metrics["remoteClosed"] = state._remoteClosed;
        outcome.Metrics["protocolErrors"] = state._protocolErrors;
        outcome.Metrics["corrupt"] = state._corrupt;
        outcome.Metrics["unmatchedReplies"] = state._unmatchedReplies;
        outcome.Metrics["connectAttempts"] = state._connectSamples + state._connectFailures;
        outcome.Metrics["connectFailures"] = state._connectFailures;
        outcome.Metrics["meanConnectMs"] = state._connectSamples == 0
            ? null
            : JsonValue.Round(Clock.ToMicroseconds(state._connectTicks) / (double)state._connectSamples / 1000.0);
        outcome.Metrics["responseRate"] = JsonValue.Ratio(state._responses, state._requests);
        outcome.Metrics["achievedRate"] = JsonValue.PerSecond(state._responses, elapsedTicks, Stopwatch.Frequency);
    }

    private static PersistentSchedule BuildSchedule(long startTicks, long deadlineTicks, PersistentPlan plan)
    {
        var intervalTicks = Math.Max(1, Clock.FromSeconds(plan.IntervalMilliseconds / 1000.0));
        var slots = (int)(((deadlineTicks - startTicks) + intervalTicks - 1) / intervalTicks);
        // The gap is a whole number of intervals: the skipped instants plus the interval that separates
        // the last request before the gap from the first one after it.
        var idleGapSlots = (Clock.FromSeconds(plan.IdleSeconds) + intervalTicks - 1) / intervalTicks;
        var skipped = (int)Math.Clamp(idleGapSlots - 1, 0, Math.Max(0, slots - 2));
        // One request on each side of the gap is what makes the gap observable at all.
        var idleStartIndex = slots < 2 ? slots : Math.Max(1, (slots - skipped) / 2);
        return new PersistentSchedule(startTicks, deadlineTicks, intervalTicks, slots, idleStartIndex, idleStartIndex + skipped);
    }

    private static void WaitForInstant(long intendedTicks, CancellationToken cancellationToken)
    {
#pragma warning disable S6966, VSTHRD103 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
        Pacer.WaitUntil(intendedTicks, cancellationToken);
#pragma warning restore S6966, VSTHRD103
    }

    private static async Task<PersistentSchedule> RunPersistentAsync(
        ArmContext context,
        PersistentCounters state,
        PersistentPlan plan,
        CancellationToken cancellationToken)
    {
        // The paced window opens once the first connection is up, so that the first request is a clean
        // round trip instead of one that also carries the connect.
        var link = await TryOpenLinkAsync(context, state, plan, cancellationToken).ConfigureAwait(false);
        var startTicks = Clock.Now;
        var schedule = BuildSchedule(startTicks, context.DeadlineTicks(startTicks), plan);
        var frame = new FrameBuffer(plan.PayloadBytes);
        try
        {
            for (var index = 0; index < schedule.Slots; index++)
            {
                WaitForInstant(schedule.IntendedTicks(index), cancellationToken);

                if (schedule.HasIdleWindow && index == schedule.IdleStartIndex)
                {
                    state._idleEntered = true;
                    state._idleBeginTicks = state._lastSendTicks;
                    state._idleGeneration = state._generation;
                }

                if (schedule.HasIdleWindow && index >= schedule.IdleStartIndex && index < schedule.IdleEndIndex)
                {
                    continue;
                }

                link = await RunRoundAsync(context, state, schedule, plan, index, frame, link, cancellationToken).ConfigureAwait(false);
            }

            WaitForInstant(schedule.DeadlineTicks, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            /* a connection that failed outside the per-round handling still ends the arm with the record so far */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
        finally
        {
            link?.Dispose();
        }

        return schedule;
    }

    private static async ValueTask<PersistentLink?> RunRoundAsync(
        ArmContext context,
        PersistentCounters state,
        PersistentSchedule schedule,
        PersistentPlan plan,
        int index,
        FrameBuffer frame,
        PersistentLink? link,
        CancellationToken cancellationToken)
    {
        state._requests++;
        // A round that has to open a connection first cannot time a round trip: the connect sits between
        // the intended instant and the echo, so the round is reported as a reconnect instead.
        var timedRound = link is not null;
        link ??= await TryOpenLinkAsync(context, state, plan, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return null;
        }

        var survivalRound = state._idleEntered && index == schedule.IdleEndIndex;
        var roundDeadlineTicks = Math.Min(Clock.Now + plan.ResponseTimeoutTicks, schedule.DeadlineTicks);
        var exchange = await ExchangeAsync(context, link, frame, (ulong)index + 1, schedule.IntendedTicks(index), roundDeadlineTicks, state, timedRound, cancellationToken).ConfigureAwait(false);
        // ReSharper disable once MergeIntoPattern // Two independent counters are tested here; a property pattern would hide which one decided and read worse than the two comparisons.
        if (state._idleEntered && state._idleEndTicks == 0 && exchange != PersistentExchange.NotSent)
        {
            state._idleEndTicks = state._lastSendTicks;
        }

        if (survivalRound)
        {
            state._survivedIdle = exchange == PersistentExchange.Echoed && state._generation == state._idleGeneration;
        }

        if (exchange == PersistentExchange.Echoed)
        {
            return link;
        }

        link.Dispose();
        return null;
    }

    private static async ValueTask<PersistentExchange> ExchangeAsync(
        ArmContext context,
        PersistentLink link,
        FrameBuffer frame,
        ulong sequence,
        long intendedTicks,
        long roundDeadlineTicks,
        PersistentCounters state,
        bool timedRound,
        CancellationToken cancellationToken)
    {
        var length = frame.Build(link.ConnectionId, sequence, Clock.Now);
        try
        {
            var send = link.Socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken);
            if (!send.IsCompleted)
            {
                state._sendWouldBlock++;
            }

            await send.ConfigureAwait(false);
        }
        catch (SocketException)
        {
            state._sendFailures++;
            return PersistentExchange.NotSent;
        }

        state._lastSendTicks = Clock.Now;
        return await TryReadEchoAsync(context, link, sequence, intendedTicks, timedRound, roundDeadlineTicks, state, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PersistentExchange> TryReadEchoAsync(
        ArmContext context,
        PersistentLink link,
        ulong sequence,
        long intendedTicks,
        bool timedRound,
        long roundDeadlineTicks,
        PersistentCounters state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (WaitForReadable(link.Socket, roundDeadlineTicks, cancellationToken))
            {
                case PersistentWait.TimedOut:
                    state._timeouts++;
                    return PersistentExchange.NotEchoed;
                case PersistentWait.Faulted:
                    // A socket that faults while the arm waits is the same evidence as a clean close: the
                    // connection is gone, which is what the idle window is there to expose.
                    state._remoteClosed++;
                    return PersistentExchange.NotEchoed;
                case PersistentWait.Readable:
                    switch (await TryReadFrameAsync(link, sequence, state, cancellationToken).ConfigureAwait(false))
                    {
                        case PersistentFrame.Dead:
                            return PersistentExchange.NotEchoed;
                        case PersistentFrame.Skipped:
                            continue;
                        case PersistentFrame.Matched:
                            state._responses++;
                            if (timedRound)
                            {
                                context.Latency.TcpRtt.Record(Clock.ToNanoseconds(Clock.Now - intendedTicks));
                            }

                            return PersistentExchange.Echoed;
                        default:
                            state._protocolErrors++;
                            return PersistentExchange.NotEchoed;
                    }

                default:
                    state._protocolErrors++;
                    return PersistentExchange.NotEchoed;
            }
        }
    }

    private static async ValueTask<PersistentFrame> TryReadFrameAsync(
        PersistentLink link,
        ulong sequence,
        PersistentCounters state,
        CancellationToken cancellationToken)
    {
        FrameReadStatus status;
        try
        {
            status = await link.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            state._remoteClosed++;
            return PersistentFrame.Dead;
        }

        switch (status)
        {
            case FrameReadStatus.EndOfStream:
                state._remoteClosed++;
                return PersistentFrame.Dead;
            case FrameReadStatus.BadChecksum:
                state._corrupt++;
                return PersistentFrame.Skipped;
            case FrameReadStatus.BadMagic:
            case FrameReadStatus.BadLength:
                state._protocolErrors++;
                return PersistentFrame.Dead;
            case FrameReadStatus.Frame:
                break;
            default:
                state._protocolErrors++;
                return PersistentFrame.Dead;
        }

        if (link.Reader.Header.ConnectionId != link.ConnectionId || link.Reader.Header.Sequence != sequence)
        {
            state._unmatchedReplies++;
            return PersistentFrame.Skipped;
        }

        if (!Filler.Matches(link.ConnectionId, sequence, link.Reader.Payload.Span))
        {
            state._corrupt++;
            return PersistentFrame.Skipped;
        }

        return PersistentFrame.Matched;
    }

    // Poll parks the dedicated arm thread in the kernel. The alternative, a cancellation source armed with
    // CancelAfter for every request, would put an allocation and a timer on the per-request path.
    private static PersistentWait WaitForReadable(Socket socket, long roundDeadlineTicks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remainingTicks = roundDeadlineTicks - Clock.Now;
        if (remainingTicks <= 0)
        {
            return PersistentWait.TimedOut;
        }

        try
        {
            var microseconds = Math.Min(Clock.ToMicroseconds(remainingTicks), int.MaxValue);
            return socket.Poll((int)microseconds, SelectMode.SelectRead) ? PersistentWait.Readable : PersistentWait.TimedOut;
        }
        catch (SocketException)
        {
            return PersistentWait.Faulted;
        }
    }

    private static async ValueTask<PersistentLink?> TryOpenLinkAsync(
        ArmContext context,
        PersistentCounters state,
        PersistentPlan plan,
        CancellationToken cancellationToken)
    {
        var socket = context.CreateTcpSocket();
        var begin = Clock.Now;
        if (!await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false))
        {
            socket.Dispose();
            state._connectFailures++;
            return null;
        }

        // Every TCP connection the arm opens carries its own id, so a reconnect shows up in the target's
        // ledger as a second connection instead of as more traffic on the first one.
        var connectionId = ConnectionIdBase + (uint)state._connectSamples;
        state._connectSamples++;
        state._connectTicks += Clock.Now - begin;
        try
        {
            await SocketOps.SendCommandAsync(socket, connectionId, TcpMode.Clean, plan.ExpectedBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            state._sendFailures++;
            socket.Dispose();
            return null;
        }

        state._generation++;
        if (state._generation > 1)
        {
            state._reconnects++;
        }

        return new PersistentLink(socket, new FrameStreamReader(socket), connectionId);
    }
}
