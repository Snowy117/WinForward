using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal sealed class LatencyTcpState
{
    /// <summary>
    /// 0 or 1, set by the lane body itself and summed across lanes, so a lane that never ran cannot
    /// pass for a lane that merely sent nothing. The connect probe is not a lane and leaves it 0.
    /// </summary>
    internal long _started;

    internal long _supplied;

    internal long _sentOk;

    internal long _sendWouldBlock;

    internal long _windowOverflow;

    internal long _backlogDrops;

    internal long _sendFailures;

    internal long _received;

    internal long _corrupt;

    internal long _protocolErrors;

    internal long _remoteClosed;

    internal long _unmatchedReplies;

    internal long _connectSamples;

    internal long _connectFailures;

    internal long _connectTicks;

    /// <summary>
    /// The one counter both halves of a lane touch: the send half reserves a slot and the receive
    /// half releases it, and the two run as independent async flows on the thread pool.
    /// </summary>
    internal long _inFlight;

    /// <summary>
    /// Requests still open when the lane stopped: sent but unanswered, plus deferred requests that
    /// never got a slot.
    /// </summary>
    internal long _outstanding;

    internal bool _scheduleTruncated;

    /// <summary>
    /// Requests that reached no latency histogram: supplied minus sent, which only counts samples
    /// the client itself destroyed.
    /// </summary>
    internal long ClientSendLoss => Math.Max(0, _supplied - _sentOk);

    internal void AddFrom(LatencyTcpState other)
    {
        _started += other._started;
        _supplied += other._supplied;
        _sentOk += other._sentOk;
        _sendWouldBlock += other._sendWouldBlock;
        _windowOverflow += other._windowOverflow;
        _backlogDrops += other._backlogDrops;
        _sendFailures += other._sendFailures;
        _received += other._received;
        _corrupt += other._corrupt;
        _protocolErrors += other._protocolErrors;
        _remoteClosed += other._remoteClosed;
        _unmatchedReplies += other._unmatchedReplies;
        _connectSamples += other._connectSamples;
        _connectFailures += other._connectFailures;
        _connectTicks += other._connectTicks;
        _outstanding += other._outstanding;
        _scheduleTruncated |= other._scheduleTruncated;
    }
}

internal sealed class UdpLatencyState
{
    /// <summary>0 or 1: set by the lane body itself, exactly as <see cref="LatencyTcpState._started"/>.</summary>
    internal long _started;

    internal long _supplied;

    internal long _sentOk;

    internal long _sendWouldBlock;

    internal long _windowOverflow;

    internal long _backlogDrops;

    internal long _sendFailures;

    /// <summary>
    /// Every valid frame, duplicates included, so it can exceed <see cref="_sentOk"/>. Loss is
    /// therefore published against <c>Received - UnmatchedReplies</c> -- the frames that consumed a
    /// pending request -- which no duplicate can inflate past <see cref="_sentOk"/>.
    /// </summary>
    internal long _received;

    internal long _corrupt;

    internal long _protocolErrors;

    internal long _unmatchedReplies;

    /// <summary>
    /// Replies bearing a connection id this socket never used. Each UDP socket has its own id, so a
    /// datagram from another flow is internally consistent -- its checksum and filler both validate
    /// against its own id -- and would otherwise be credited as a legitimate arrival on this lane.
    /// </summary>
    internal long _foreignConnection;

    internal long _inFlight;

    internal long _outstanding;

    internal bool _scheduleTruncated;

    internal long ClientSendLoss => Math.Max(0, _supplied - _sentOk);
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct DeferredRequest
{
    internal DeferredRequest(long sequence, long intendedTicks)
    {
        Sequence = sequence;
        IntendedTicks = intendedTicks;
    }

    internal long Sequence { get; }

    internal long IntendedTicks { get; }
}

internal static class LatencyArm
{
    private const int DefaultRatePerSecond = 20;
    private const int DefaultPayloadBytes = 120;
    private const int DefaultWindow = 64;
    private const int DefaultLanes = 1;

    // How long a lane may keep deferring work, in seconds of its own offered rate: long enough for
    // any stall the arm should survive, bounded so a product that never answers cannot allocate
    // without limit.
    private const int BacklogSeconds = 10;

    // Ceiling for plans that offer absurd rates, so the first act is never a huge allocation.
    private const int MaxBacklogPerLane = 1 << 20;

    // The receive side drains after the offer loop so the last in-flight cohort is sampled rather
    // than cancelled; the bound keeps a silent product from holding the arm open.
    private const int DrainSeconds = 1;

    private const uint UdpConnectionId = 0x7100_0001u;
    private const uint TcpConnectionIdBase = 0x7400_0000u;

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var plan = new LatencyPlan(context.Spec);
        var metrics = new DictionaryMetrics();
        var outcome = new ArmOutcome { Metrics = metrics };
        plan.WriteParameters(outcome);

        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var lanes = new LaneStates(plan);
        var tasks = StartLanes(context, plan, lanes, startTicks, deadlineTicks, linked.Token);

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var elapsedTicks = Clock.Now - startTicks;
        var tcp = Total(lanes.Tcp, lanes.Probe);

        if (plan.UseTcp)
        {
            WriteTcpMetrics(metrics, tcp, lanes.Tcp, plan, elapsedTicks);
        }

        if (plan.UseUdp)
        {
            WriteUdpMetrics(metrics, lanes.Udp, plan, elapsedTicks);
        }

        var validity = new MeasurementValidity(plan, lanes, tcp, elapsedTicks);
        WriteGates(outcome, lanes, tcp, validity);
        WriteNotes(outcome, plan, lanes, tcp, validity, elapsedTicks);
        return outcome;
    }

    private static List<Task> StartLanes(
        ArmContext context,
        LatencyPlan plan,
        LaneStates lanes,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        if (plan.UseTcp)
        {
            // Own thread per lane: a lane whose first awaits complete synchronously would otherwise
            // run to completion here and the lanes after it would never be constructed.
            for (var lane = 0; lane < plan.Lanes; lane++)
            {
                var laneState = lanes.Tcp[lane];
                var laneIndex = lane;
                tasks.Add(Dedicated.RunOnOwnThreadAsync(() => RunTcpLaneAsync(context, laneState, laneIndex, plan, startTicks, deadlineTicks, cancellationToken)));
            }

            tasks.Add(Dedicated.RunOnOwnThreadAsync(() => RunConnectProbeAsync(context, lanes.Probe, startTicks, deadlineTicks, cancellationToken)));
        }

        if (plan.UseUdp)
        {
            tasks.Add(Dedicated.RunOnOwnThreadAsync(() => RunUdpLaneAsync(context, lanes.Udp, plan, startTicks, deadlineTicks, cancellationToken)));
        }

        return tasks;
    }

    /// <summary>
    /// The ceiling on directly measurable latency: a request slower than window / (per-lane achieved
    /// rate) no longer frees its slot before the next offer, so everything past it is measured
    /// through the deferred queue instead.
    /// </summary>
    private static double InFlightCeilingMs(int window, int lanes, long sentOk, long elapsedTicks)
    {
        var achievedPerSecond = JsonPerSecond.PerSecond(sentOk, elapsedTicks, Stopwatch.Frequency);
        if (achievedPerSecond is not > 0)
        {
            // No elapsed time, or nothing sent: the ceiling is undefined, and it stays the 0 the
            // pre-change arithmetic published rather than taking the record's null convention,
            // because this value is a floor on a measurable latency and not a rate.
            return 0;
        }

        return NumberFormat.Round(1000.0 * window * lanes / achievedPerSecond.GetValueOrDefault());
    }

    /// <summary>
    /// Renders a rate for a note. A note is prose, so an undefined rate says so instead of leaving
    /// the hole an interpolated null leaves between two spaces.
    /// </summary>
    private static string RateText(double? perSecond) =>
        perSecond is { } value ? value.ToString("F3", CultureInfo.InvariantCulture) : "n/a";

    private static void WriteTcpMetrics(DictionaryMetrics metrics, LatencyTcpState state, List<LatencyTcpState> laneStates, LatencyPlan plan, long elapsedTicks)
    {
        var laneSupplied = new long[laneStates.Count];
        var laneSentOk = new long[laneStates.Count];
        for (var lane = 0; lane < laneStates.Count; lane++)
        {
            laneSupplied[lane] = laneStates[lane]._supplied;
            laneSentOk[lane] = laneStates[lane]._sentOk;
        }

        metrics["tcp.laneStarted"] = state._started;
        metrics["tcp.laneSupplied"] = laneSupplied;
        metrics["tcp.laneSentOk"] = laneSentOk;
        metrics["tcp.supplied"] = state._supplied;
        metrics["tcp.sentOk"] = state._sentOk;
        metrics["tcp.sendWouldBlock"] = state._sendWouldBlock;
        metrics["tcp.windowOverflow"] = state._windowOverflow;
        metrics["tcp.backlogDrops"] = state._backlogDrops;
        metrics["tcp.sendFailures"] = state._sendFailures;
        metrics["tcp.abandonedAtTeardown"] = Math.Max(0, state._supplied - state._sentOk - state._sendFailures - state._backlogDrops);
        metrics["tcp.clientSendLoss"] = state.ClientSendLoss;
        metrics["tcp.received"] = state._received;
        metrics["tcp.outstandingAtTeardown"] = state._outstanding;
        metrics["tcp.corrupt"] = state._corrupt;
        metrics["tcp.protocolErrors"] = state._protocolErrors;
        metrics["tcp.remoteClosed"] = state._remoteClosed;
        metrics["tcp.unmatchedReplies"] = state._unmatchedReplies;
        metrics["tcp.windowCeilingMs"] = InFlightCeilingMs(plan.InFlightWindow, plan.Lanes, state._sentOk, elapsedTicks);
        metrics["tcp.scheduleTruncated"] = state._scheduleTruncated ? 1L : 0L;
        metrics["tcp.achievedRate"] = JsonPerSecond.PerSecond(state._sentOk, elapsedTicks, Stopwatch.Frequency);
        metrics["tcp.connectAttempts"] = state._connectSamples + state._connectFailures;
        metrics["tcp.connectFailures"] = state._connectFailures;
        metrics["tcp.meanConnectMs"] = state._connectSamples == 0
            ? null
            : NumberFormat.Round(Clock.ToMicroseconds(state._connectTicks) / (double)state._connectSamples / 1000.0);
    }

    private static void WriteUdpMetrics(DictionaryMetrics metrics, UdpLatencyState state, LatencyPlan plan, long elapsedTicks)
    {
        metrics["udp.laneStarted"] = state._started;
        metrics["udp.supplied"] = state._supplied;
        metrics["udp.sentOk"] = state._sentOk;
        metrics["udp.sendWouldBlock"] = state._sendWouldBlock;
        metrics["udp.windowOverflow"] = state._windowOverflow;
        metrics["udp.backlogDrops"] = state._backlogDrops;
        metrics["udp.sendFailures"] = state._sendFailures;
        metrics["udp.abandonedAtTeardown"] = Math.Max(0, state._supplied - state._sentOk - state._sendFailures - state._backlogDrops);
        metrics["udp.clientSendLoss"] = state.ClientSendLoss;
        metrics["udp.received"] = state._received;
        metrics["udp.corrupt"] = state._corrupt;
        metrics["udp.protocolErrors"] = state._protocolErrors;
        metrics["udp.unmatchedReplies"] = state._unmatchedReplies;
        metrics["udp.foreignConnection"] = state._foreignConnection;
        metrics["udp.outstandingAtTeardown"] = state._outstanding;
        metrics["udp.windowCeilingMs"] = InFlightCeilingMs(plan.InFlightWindow, 1, state._sentOk, elapsedTicks);
        metrics["udp.scheduleTruncated"] = state._scheduleTruncated ? 1L : 0L;

        // SentOk minus matched arrivals, never SentOk minus Received, which duplicates can exceed.
        metrics["udp.lossRate"] = JsonRate.Rate(state._sentOk - (state._received - state._unmatchedReplies), state._sentOk);
        metrics["udp.achievedRate"] = JsonPerSecond.PerSecond(state._sentOk, elapsedTicks, Stopwatch.Frequency);
    }

    private static void WriteGates(ArmOutcome outcome, LaneStates lanes, LatencyTcpState tcp, MeasurementValidity validity)
    {
        outcome.Gates["clientSendLoss"] = tcp.ClientSendLoss + lanes.Udp.ClientSendLoss;
        outcome.Gates["windowOverflow"] = tcp._windowOverflow + lanes.Udp._windowOverflow;
        outcome.Gates["backlogDrops"] = tcp._backlogDrops + lanes.Udp._backlogDrops;
        outcome.Gates["sendFailures"] = tcp._sendFailures + lanes.Udp._sendFailures;
        outcome.Gates["lanesPlanned"] = validity.PlannedLanes;
        outcome.Gates["lanesStarted"] = validity.StartedLanes;
        outcome.Gates["laneShortfall"] = validity.LaneShortfall;
        outcome.Gates["scheduleTruncated"] = validity.Truncated ? 1L : 0L;
        outcome.Gates["inFlightCeilingMs"] = TightestCeiling(validity.TcpCeilingMs, validity.UdpCeilingMs);
        outcome.Gates["windowMs"] = 0L;
    }

    private static void WriteNotes(
        ArmOutcome outcome,
        LatencyPlan plan,
        LaneStates lanes,
        LatencyTcpState tcp,
        MeasurementValidity validity,
        long elapsedTicks)
    {
        outcome.Notes.Add("latency is measured from each request's intended send instant, never from the actual send instant.");
        outcome.Notes.Add("sendWouldBlock counts sends the kernel did not accept synchronously; those sends still complete, so they are reported but not folded into clientSendLoss.");
        outcome.Notes.Add("a reply carrying another flow's connection id validates against that flow's own filler, so it is rejected by connection id before the filler check and booked as foreignConnection; only a reply that carries this socket's own id and fails its filler is booked as corrupt.");
        outcome.Notes.Add(string.Concat(
            "tcp.connectAttempts counts every TCP connect the arm made -- the per-lane lane connects plus the 1 Hz connect probe -- and tcp.connectFailures counts the attempts in that same population that failed, so tcp.meanConnectMs is the mean connect duration over its successful attempts and is null when there were none; ",
            "latency.tcp-connect separately holds the probe's own samples, measured from each probe's intended instant."));
        outcome.Notes.Add("a request offered at a full in-flight window is deferred, not dropped: it goes out as soon as a reply frees a slot, still stamped with its original intended instant, so a stalled product is published as an inflated sample instead of a missing one; windowOverflow counts those deferrals.");
        outcome.Notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"the deferred queue holds at most {plan.BacklogLimit} request(s) per lane, i.e. {BacklogSeconds} s of the offered rate capped at {MaxBacklogPerLane}; a request that bound discards is counted in backlogDrops and folded into gates.clientSendLoss, so an arm that censors its own tail cannot publish a clean gate."));
        outcome.Notes.Add("sendFailures counts individual sends that threw; the offer loop continues after one, so a transient error costs that request's sample instead of the rest of the schedule, and that sample is folded into gates.clientSendLoss.");
        outcome.Notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"each lane drains its receive side for up to {DrainSeconds} s after the offer loop so the cohort still in flight is sampled rather than discarded; outstandingAtTeardown counts what was still unanswered, plus any deferred request that never got a slot, when the bound expired."));
        outcome.Notes.Add("tcp and udp counters are summed only after every lane has joined, so sentOk cannot exceed supplied however many lanes a plan asks for.");

        if (plan.UseTcp)
        {
            outcome.Notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"in-flight ceiling (tcp): {plan.Lanes} lane(s) at {RateText(JsonPerSecond.PerSecond(tcp._sentOk, elapsedTicks, Stopwatch.Frequency))} requests/s aggregate achieved with inFlightWindow {plan.InFlightWindow} per lane put the measurable direct-latency ceiling at {validity.TcpCeilingMs:F1} ms; slower requests are measured through the deferred queue, and the ceiling was {(tcp._windowOverflow > 0 ? "reached" : "not reached")} (windowOverflow = {tcp._windowOverflow})."));
        }

        if (plan.UseUdp)
        {
            outcome.Notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"in-flight ceiling (udp): 1 lane at {RateText(JsonPerSecond.PerSecond(lanes.Udp._sentOk, elapsedTicks, Stopwatch.Frequency))} requests/s achieved with inFlightWindow {plan.InFlightWindow} put the measurable direct-latency ceiling at {validity.UdpCeilingMs:F1} ms; slower requests are measured through the deferred queue, and the ceiling was {(lanes.Udp._windowOverflow > 0 ? "reached" : "not reached")} (windowOverflow = {lanes.Udp._windowOverflow})."));
        }

        outcome.Notes.Add(string.Concat(
            "lanes actually run: ",
            plan.UseTcp ? string.Create(CultureInfo.InvariantCulture, $"tcp {tcp._started} of {plan.Lanes}") : string.Empty,
            // ReSharper disable once MergeIntoPattern // Two independent plan flags read better as a conjunction than as a property pattern.
            plan.UseTcp && plan.UseUdp ? ", " : string.Empty,
            plan.UseUdp ? string.Create(CultureInfo.InvariantCulture, $"udp {lanes.Udp._started} of 1") : string.Empty,
            "; per-lane supplied and sent counts are published as tcp.laneSupplied and tcp.laneSentOk."));

        if (tcp._scheduleTruncated || lanes.Udp._scheduleTruncated)
        {
            outcome.Notes.Add("a lane never connected or an offer loop ended before its deadline, so part of the offered schedule was never offered at all; gates.scheduleTruncated marks the record.");
        }

        if (validity.LaneShortfall > 0)
        {
            outcome.Notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{validity.StartedLanes} of {validity.PlannedLanes} planned lane(s) ran and supplied traffic; a lane that supplies nothing is a harness failure because it removes its whole share of the offered schedule (gates.laneShortfall)."));
        }
    }

    private static LatencyTcpState Total(List<LatencyTcpState> lanes, LatencyTcpState probe)
    {
        var total = new LatencyTcpState();
        foreach (var lane in lanes)
        {
            total.AddFrom(lane);
        }

        total.AddFrom(probe);
        return total;
    }

    private static double TightestCeiling(double first, double second)
    {
        if (first <= 0)
        {
            return second;
        }

        return second <= 0 ? first : Math.Min(first, second);
    }

    private static async Task RunTcpLaneAsync(
        ArmContext context,
        LatencyTcpState state,
        int laneIndex,
        LatencyPlan plan,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        state._started = 1;
        using var socket = context.CreateTcpSocket();
        var begin = Clock.Now;
        if (!await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref state._connectFailures);

            // A lane that never connects offered none of its share of the schedule, so the record
            // must say the schedule was short rather than read as a quiet run.
            state._scheduleTruncated = true;
            return;
        }

        // One population: every connect the arm attempted, lane connects and probe alike.
        var end = Clock.Now;
        Interlocked.Increment(ref state._connectSamples);
        Interlocked.Add(ref state._connectTicks, end - begin);

        var connectionId = TcpConnectionIdBase + (uint)laneIndex;
        await SocketOps.SendCommandAsync(socket, connectionId, TcpMode.Clean, 0, cancellationToken).ConfigureAwait(false);
        var frame = new FrameBuffer(plan.PayloadBytes);
        var pending = new ConcurrentQueue<long>();
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveLoopAsync(context, socket, pending, state, laneCancellation.Token);
        var unsent = await SendLoopAsync(socket, frame, connectionId, pending, state, plan, startTicks, deadlineTicks, laneCancellation.Token).ConfigureAwait(false);
        await GraceDrainAsync(receive, pending, laneCancellation.Token).ConfigureAwait(false);
        await laneCancellation.CancelAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
        state._outstanding = pending.Count + unsent;
    }

    private static async Task<int> SendLoopAsync(
        Socket socket,
        FrameBuffer frame,
        uint connectionId,
        ConcurrentQueue<long> pending,
        LatencyTcpState state,
        LatencyPlan plan,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var pacer = new Pacer(plan.Rate, startTicks);
        var backlog = new DeferredQueue(plan.BacklogLimit);
        long index = 0;
        try
        {
            while (Clock.Now < deadlineTicks)
            {
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
                // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
                Pacer.WaitUntil(pacer.IntendedTicks(index), cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042
                index++;
                state._supplied = index;
                await OfferFrameAsync(socket, frame, connectionId, pending, state, backlog, new DeferredRequest(index, pacer.IntendedTicks(index - 1)), plan.InFlightWindow, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            /* a fatal socket error ended the offer loop; the shortfall is published below */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }

        // Stopping before the deadline means the tail of the schedule was never offered at all.
        state._scheduleTruncated = Clock.Now < deadlineTicks || state._scheduleTruncated;
        return backlog.Count;
    }

    private static async ValueTask OfferFrameAsync(
        Socket socket,
        FrameBuffer frame,
        uint connectionId,
        ConcurrentQueue<long> pending,
        LatencyTcpState state,
        DeferredQueue backlog,
        DeferredRequest request,
        int window,
        CancellationToken cancellationToken)
    {
        // The longest-waiting intent goes out first, so a sample's age stays tied to when its
        // request was wanted rather than to whichever slot happened to free.
        while (Interlocked.Read(ref state._inFlight) < window && backlog.TryDequeue(out var deferred))
        {
            await SendFrameAsync(socket, frame, connectionId, deferred, pending, state, cancellationToken).ConfigureAwait(false);
        }

        if (Interlocked.Read(ref state._inFlight) < window)
        {
            await SendFrameAsync(socket, frame, connectionId, request, pending, state, cancellationToken).ConfigureAwait(false);
            return;
        }

        state._windowOverflow++;
        if (!backlog.TryEnqueue(request))
        {
            // Queue full too: the request yields no sample, which is censoring and is counted.
            state._backlogDrops++;
        }
    }

    private static async ValueTask SendFrameAsync(
        Socket socket,
        FrameBuffer frame,
        uint connectionId,
        DeferredRequest request,
        ConcurrentQueue<long> pending,
        LatencyTcpState state,
        CancellationToken cancellationToken)
    {
        var length = frame.Build(connectionId, (ulong)request.Sequence, request.IntendedTicks);
        var send = socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken);
        if (!send.IsCompleted)
        {
            state._sendWouldBlock++;
        }

        try
        {
            await send.ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // One request failed, not the loop: the schedule continues without that sample.
            state._sendFailures++;
            return;
        }

        state._sentOk++;
        Interlocked.Increment(ref state._inFlight);
        pending.Enqueue(request.IntendedTicks);
    }

    private static async Task ReceiveLoopAsync(
        ArmContext context,
        Socket socket,
        ConcurrentQueue<long> pending,
        LatencyTcpState state,
        CancellationToken cancellationToken)
    {
        var reader = new FrameStreamReader(socket);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                switch (status)
                {
                    case FrameReadStatus.EndOfStream:
                        state._remoteClosed++;
                        return;
                    case FrameReadStatus.BadChecksum:
                        state._corrupt++;
                        continue;
                    case FrameReadStatus.BadMagic:
                    case FrameReadStatus.BadLength:
                        state._protocolErrors++;
                        return;
                    case FrameReadStatus.Frame:
                        state._received++;
                        Interlocked.Decrement(ref state._inFlight);
                        if (!pending.TryDequeue(out var intended))
                        {
                            state._unmatchedReplies++;
                            continue;
                        }

                        context.Latency.TcpRtt.Record(Clock.ToNanoseconds(Clock.Now - intended));
                        continue;
                    default:
                        state._protocolErrors++;
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            state._protocolErrors++;
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }

    private static async Task RunConnectProbeAsync(
        ArmContext context,
        LatencyTcpState state,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var intervalTicks = Clock.FromSeconds(1);
        long index = 0;
        try
        {
            while (true)
            {
                var intended = startTicks + (index * intervalTicks);
                if (intended >= deadlineTicks)
                {
                    return;
                }

                await Pacer.WaitUntilAsync(intended, cancellationToken).ConfigureAwait(false);
                using var socket = context.CreateTcpSocket();
                var begin = Clock.Now;
                if (!await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false))
                {
                    state._connectFailures++;
                    index++;
                    continue;
                }

                var end = Clock.Now;
                state._connectSamples++;
                state._connectTicks += end - begin;
                context.Latency.TcpConnect.Record(Clock.ToNanoseconds(end - intended));
                SocketOps.ShutdownQuietly(socket, SocketShutdown.Both);
                index++;
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
    }

    private static async Task RunUdpLaneAsync(
        ArmContext context,
        UdpLatencyState state,
        LatencyPlan plan,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        state._started = 1;
        using var socket = context.CreateUdpSocket();
        await socket.ConnectAsync(context.UdpEndPoint, cancellationToken).ConfigureAwait(false);
        var frame = new FrameBuffer(plan.PayloadBytes);
        var receiveBuffer = new byte[FrameCodec.HeaderSize + plan.PayloadBytes + FrameCodec.TrailerSize + 64];
        var pending = new ConcurrentDictionary<ulong, long>();
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveUdpLoopAsync(context, socket, pending, state, receiveBuffer, laneCancellation.Token);
        var unsent = await SendUdpLoopAsync(socket, frame, pending, state, plan, startTicks, deadlineTicks, laneCancellation.Token).ConfigureAwait(false);
        await GraceDrainAsync(receive, pending, laneCancellation.Token).ConfigureAwait(false);
        await laneCancellation.CancelAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
        state._outstanding = pending.Count + unsent;
    }

    private static async Task<int> SendUdpLoopAsync(
        Socket socket,
        FrameBuffer frame,
        ConcurrentDictionary<ulong, long> pending,
        UdpLatencyState state,
        LatencyPlan plan,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var pacer = new Pacer(plan.Rate, startTicks);
        var backlog = new DeferredQueue(plan.BacklogLimit);
        long index = 0;
        try
        {
            while (Clock.Now < deadlineTicks)
            {
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
                // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
                Pacer.WaitUntil(pacer.IntendedTicks(index), cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042
                index++;
                state._supplied = index;
                await OfferDatagramAsync(socket, frame, pending, state, backlog, new DeferredRequest(index, pacer.IntendedTicks(index - 1)), plan.InFlightWindow, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            /* a fatal socket error ended the offer loop; the shortfall is published below */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }

        state._scheduleTruncated = Clock.Now < deadlineTicks || state._scheduleTruncated;
        return backlog.Count;
    }

    private static async ValueTask OfferDatagramAsync(
        Socket socket,
        FrameBuffer frame,
        ConcurrentDictionary<ulong, long> pending,
        UdpLatencyState state,
        DeferredQueue backlog,
        DeferredRequest request,
        int window,
        CancellationToken cancellationToken)
    {
        while (Interlocked.Read(ref state._inFlight) < window && backlog.TryDequeue(out var deferred))
        {
            await SendDatagramAsync(socket, frame, deferred, pending, state, cancellationToken).ConfigureAwait(false);
        }

        if (Interlocked.Read(ref state._inFlight) < window)
        {
            await SendDatagramAsync(socket, frame, request, pending, state, cancellationToken).ConfigureAwait(false);
            return;
        }

        state._windowOverflow++;
        if (!backlog.TryEnqueue(request))
        {
            state._backlogDrops++;
        }
    }

    private static async ValueTask SendDatagramAsync(
        Socket socket,
        FrameBuffer frame,
        DeferredRequest request,
        ConcurrentDictionary<ulong, long> pending,
        UdpLatencyState state,
        CancellationToken cancellationToken)
    {
        var length = frame.Build(UdpConnectionId, (ulong)request.Sequence, request.IntendedTicks);
        var send = socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken);
        if (!send.IsCompleted)
        {
            state._sendWouldBlock++;
        }

        try
        {
            await send.ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // A connected udp socket surfaces a previous datagram's icmp error here; the next send
            // may well succeed, so one failure must not end the schedule.
            state._sendFailures++;
            return;
        }

        state._sentOk++;
        Interlocked.Increment(ref state._inFlight);
        pending[(ulong)request.Sequence] = request.IntendedTicks;
    }

    private static async Task ReceiveUdpLoopAsync(
        ArmContext context,
        Socket socket,
        ConcurrentDictionary<ulong, long> pending,
        UdpLatencyState state,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                var now = Clock.Now;
                if (!FrameCodec.TryDecode(receiveBuffer.AsSpan(0, received), out var header, out var payload, out _))
                {
                    state._corrupt++;
                    continue;
                }

                // Connection id before filler, as in the loss and mix arms: a reply from another
                // flow validates against that flow's own filler, so a filler-first rule would book
                // the product mixing two flows as an ordinary corrupt datagram.
                if (header.ConnectionId != UdpConnectionId)
                {
                    state._foreignConnection++;
                    continue;
                }

                if (!Filler.Matches(header.ConnectionId, header.Sequence, payload))
                {
                    state._corrupt++;
                    continue;
                }

                state._received++;
                Interlocked.Decrement(ref state._inFlight);
                if (!pending.TryRemove(header.Sequence, out var intended))
                {
                    state._unmatchedReplies++;
                    continue;
                }

                context.Latency.UdpRtt.Record(Clock.ToNanoseconds(now - intended));
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            state._protocolErrors++;
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }

    private static async Task GraceDrainAsync(Task receive, ConcurrentQueue<long> pending, CancellationToken cancellationToken)
    {
        var until = Clock.Now + Clock.FromSeconds(DrainSeconds);
        try
        {
            while (Clock.Now < until && !receive.IsCompleted && !pending.IsEmpty)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
    }

    private static async Task GraceDrainAsync(Task receive, ConcurrentDictionary<ulong, long> pending, CancellationToken cancellationToken)
    {
        var until = Clock.Now + Clock.FromSeconds(DrainSeconds);
        try
        {
            while (Clock.Now < until && !receive.IsCompleted && !pending.IsEmpty)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
    }

    /// <summary>The effective run shape after plan defaults, shared by every lane and by the record.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly struct LatencyPlan
    {
        internal LatencyPlan(ArmSpec spec)
        {
            Seconds = spec.Seconds;
            Protocol = spec.Protocol;
            Rate = spec.RatePerSecond > 0 ? spec.RatePerSecond : DefaultRatePerSecond;
            PayloadBytes = spec.PayloadBytes > 0 ? spec.PayloadBytes : DefaultPayloadBytes;
            InFlightWindow = spec.Window > 0 ? spec.Window : DefaultWindow;
            Lanes = spec.Lanes > 0 ? spec.Lanes : DefaultLanes;
            BacklogLimit = BacklogCapacity(Rate, InFlightWindow);
            UseTcp = spec.Protocol is "tcp" or "tcp+udp";
            UseUdp = spec.Protocol is "udp" or "tcp+udp";
        }

        private double Seconds { get; }

        private string Protocol { get; }

        internal int Rate { get; }

        internal int PayloadBytes { get; }

        internal int InFlightWindow { get; }

        internal int Lanes { get; }

        internal int BacklogLimit { get; }

        internal bool UseTcp { get; }

        internal bool UseUdp { get; }

        internal void WriteParameters(ArmOutcome outcome)
        {
            outcome.Parameters["seconds"] = Seconds;
            outcome.Parameters["ratePerSecond"] = Rate;
            outcome.Parameters["payloadBytes"] = PayloadBytes;
            outcome.Parameters["protocol"] = Protocol;
            outcome.Parameters["lanes"] = Lanes;

            // in-flight requests per lane; the loss arm's window is a millisecond threshold, so the
            // two arms must not publish one bare "window" that means different things.
            outcome.Parameters["inFlightWindow"] = InFlightWindow;
        }

        private static int BacklogCapacity(int rate, int window) =>
            (int)Math.Min(MaxBacklogPerLane, Math.Max((long)window * 4, (long)rate * BacklogSeconds));
    }

    /// <summary>
    /// Per-lane state containers: each lane writes only its own state and the totals are formed
    /// after the lanes join.
    /// </summary>
    private sealed class LaneStates
    {
        internal LaneStates(LatencyPlan plan)
        {
            // States exist before any lane starts, so a lane that never runs still appears in the
            // per-lane arrays instead of shrinking them.
            for (var lane = 0; plan.UseTcp && lane < plan.Lanes; lane++)
            {
                Tcp.Add(new LatencyTcpState());
            }
        }

        internal List<LatencyTcpState> Tcp { get; } = [];

        internal LatencyTcpState Probe { get; } = new();

        internal UdpLatencyState Udp { get; } = new();
    }

    private sealed class DeferredQueue
    {
        private readonly int _capacity;
        private readonly Queue<DeferredRequest> _requests;

        internal DeferredQueue(int capacity)
        {
            _capacity = capacity;
            _requests = new Queue<DeferredRequest>(capacity);
        }

        internal int Count => _requests.Count;

        internal bool TryEnqueue(DeferredRequest request)
        {
            if (_requests.Count >= _capacity)
            {
                return false;
            }

            _requests.Enqueue(request);
            return true;
        }

        internal bool TryDequeue(out DeferredRequest request) => _requests.TryDequeue(out request);
    }

    /// <summary>
    /// Whether the record measured what it offered: lanes run, schedule shortfall, and the ceiling
    /// on directly measurable latency. Derived once so gates and notes cannot disagree.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly struct MeasurementValidity
    {
        internal MeasurementValidity(LatencyPlan plan, LaneStates lanes, LatencyTcpState tcp, long elapsedTicks)
        {
            PlannedLanes = (plan.UseTcp ? plan.Lanes : 0) + (plan.UseUdp ? 1 : 0);
            StartedLanes = (plan.UseTcp ? tcp._started : 0) + (plan.UseUdp ? lanes.Udp._started : 0);
            var idleLanes = (plan.UseTcp ? CountIdleLanes(lanes.Tcp) : 0) + (IsIdle(lanes.Udp._started, lanes.Udp._supplied) ? 1 : 0);
            LaneShortfall = PlannedLanes - StartedLanes + idleLanes;
            TcpCeilingMs = plan.UseTcp ? InFlightCeilingMs(plan.InFlightWindow, plan.Lanes, tcp._sentOk, elapsedTicks) : 0;
            UdpCeilingMs = plan.UseUdp ? InFlightCeilingMs(plan.InFlightWindow, 1, lanes.Udp._sentOk, elapsedTicks) : 0;
            Truncated = tcp._scheduleTruncated || lanes.Udp._scheduleTruncated || LaneShortfall > 0;
        }

        internal long PlannedLanes { get; }

        internal long StartedLanes { get; }

        internal long LaneShortfall { get; }

        internal double TcpCeilingMs { get; }

        internal double UdpCeilingMs { get; }

        internal bool Truncated { get; }

        private static bool IsIdle(long started, long supplied) => started > 0 && supplied == 0;

        private static long CountIdleLanes(List<LatencyTcpState> lanes)
        {
            long idle = 0;
            foreach (var lane in lanes)
            {
                idle += IsIdle(lane._started, lane._supplied) ? 1 : 0;
            }

            return idle;
        }
    }
}
