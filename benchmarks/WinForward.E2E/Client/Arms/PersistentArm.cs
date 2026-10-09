using System.Diagnostics;
using System.Net.Sockets;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Client.Arms;

internal sealed class PersistentCounters
{
    internal long _requests;
    internal long _sentRequests;
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

internal static class PersistentArm
{
    private const int DefaultIntervalMilliseconds = 1000;
    private const int DefaultIdleSeconds = 20;
    private const int DefaultPayloadBytes = 120;
    private const int DefaultResponseTimeoutMilliseconds = 2000;

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

        var plan = new PersistentPlan(intervalMilliseconds, idleSeconds, payloadBytes, expectedBytes, Clock.FromSeconds(responseTimeoutMilliseconds / 1000.0));
        using var linked = context.CreateLinkedTokenSource();
        var cancellationToken = linked.Token;
        var state = new PersistentCounters();
        var schedule = await DedicatedThread.RunOnOwnThreadAsync(() => RunPersistentAsync(context, state, plan, cancellationToken)).ConfigureAwait(false);

        var outcome = new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = spec.Seconds,
                IntervalMs = intervalMilliseconds,
                IdleSeconds = idleSeconds,
                PayloadBytes = payloadBytes,
                ExpectedBytes = spec.ExpectedBytes,
                ResponseTimeoutMs = responseTimeoutMilliseconds,
            },
            Metrics = BuildMetrics(state, schedule, Clock.Now),
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = state._connectFailures + state._sendFailures,
                [ArmKeys.Common.Gates.WindowMs] = 0,
            },
        };
        outcome.Notes.Add("requests counts paced exchanges attempted and responses counts the echoes that completed them, each timed from its request's intended send instant; the difference is explained by connectFailures, sendFailures, timeouts, remoteClosed and protocolErrors. achievedRate counts the requests whose send completed per elapsed second and completionRate counts the responses per elapsed second, so requests neither sent nor answered are in neither rate.");
        outcome.Notes.Add("a request that finds its connection dead opens a replacement connection first and is counted as a reconnect: it records no tcp-rtt sample, so the latency histograms exclude both the reconnect and the round it carries, while responses and responseRate still count that round when it echoes.");
        outcome.Notes.Add("survivedIdle is the connection live when the idle window opened completing the first request after it; it is false when that request had to reconnect, when it failed, and when no idle window fitted inside the arm.");
        outcome.Notes.Add("idleSecondsScheduled is the whole number of pacing intervals the requested idle period was rounded to, shortened to what the arm can hold; idleSecondsObserved is the silence between the last request sent before the window and the first sent after it, so it includes any reconnect the first post-idle request needed.");
        outcome.Notes.Add("sendWouldBlock counts sends the kernel did not accept synchronously, and meanConnectMs is null when the arm never completed a connect rather than a zero that would read as an instantaneous one.");
        return outcome;
    }

    /// <summary>
    /// The record a finished run publishes: the counters the arm ended with, the idle window it
    /// actually held, and the rates derived from them.
    /// </summary>
    private static PersistentMetrics BuildMetrics(PersistentCounters state, PersistentSchedule schedule, long endTicks)
    {
        var elapsedTicks = Math.Max(0, endTicks - schedule.StartTicks);
        var idleTicks = 0L;
        if (state._idleEntered && state._idleBeginTicks != 0)
        {
            var idleEndTicks = state._idleEndTicks > state._idleBeginTicks ? state._idleEndTicks : endTicks;
            idleTicks = Math.Max(0, idleEndTicks - state._idleBeginTicks);
        }

        return new PersistentMetrics
        {
            Requests = state._requests,
            Responses = state._responses,
            Reconnects = state._reconnects,
            SurvivedIdle = state._survivedIdle,
            IdleSecondsScheduled = NumberFormat.Round(Clock.ToSeconds(schedule.ScheduledIdleTicks)),
            IdleSecondsObserved = NumberFormat.Round(Clock.ToSeconds(idleTicks)),
            SendWouldBlock = state._sendWouldBlock,
            SendFailures = state._sendFailures,
            Timeouts = state._timeouts,
            RemoteClosed = state._remoteClosed,
            ProtocolErrors = state._protocolErrors,
            Corrupt = state._corrupt,
            UnmatchedReplies = state._unmatchedReplies,
            ConnectAttempts = state._connectSamples + state._connectFailures,
            ConnectFailures = state._connectFailures,
            MeanConnectMs = state._connectSamples == 0
                ? null
                : NumberFormat.Round(Clock.ToMicroseconds(state._connectTicks) / (double)state._connectSamples / 1000.0),
            ResponseRate = JsonRate.Rate(state._responses, state._requests),
            AchievedRate = JsonPerSecond.PerSecond(state._sentRequests, elapsedTicks, Stopwatch.Frequency),
            CompletionRate = JsonPerSecond.PerSecond(state._responses, elapsedTicks, Stopwatch.Frequency),
        };
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
        var link = await PersistentConnection.TryOpenLinkAsync(context, state, plan, cancellationToken).ConfigureAwait(false);
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
        link ??= await PersistentConnection.TryOpenLinkAsync(context, state, plan, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return null;
        }

        var survivalRound = state._idleEntered && index == schedule.IdleEndIndex;
        var roundDeadlineTicks = Math.Min(Clock.Now + plan.ResponseTimeoutTicks, schedule.DeadlineTicks);
        var exchange = await PersistentConnection.ExchangeAsync(context, link, frame, (ulong)index + 1, schedule.IntendedTicks(index), roundDeadlineTicks, state, timedRound, cancellationToken).ConfigureAwait(false);
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
}
