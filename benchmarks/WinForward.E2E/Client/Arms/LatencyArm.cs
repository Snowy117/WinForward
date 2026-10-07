using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

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

    // How much room a lane's receive buffer keeps past one whole frame, so a datagram that is not a
    // frame can still be read and judged instead of being cut off at the buffer's edge.
    private const int ReceiveSlack = 64;

    private const uint UdpConnectionId = 0x7100_0001u;
    private const uint TcpConnectionIdBase = 0x7400_0000u;

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var plan = new LatencyPlan(context.Spec);

        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var lanes = new LaneStates(plan);
        var tasks = StartLanes(context, plan, lanes, startTicks, deadlineTicks, linked.Token);

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var elapsedTicks = Clock.Now - startTicks;

        // Both totals are formed here, once every lane has joined: the engine's send-side snapshot and
        // the policy's own book are joined by lane, never by a counter that one of them copied.
        var tcp = TotalTcp(lanes.Tcp, lanes.Probe);
        var udp = TotalUdp(lanes.Udp);

        // The metrics value is built after the lanes joined, so a protocol the arm did not run
        // publishes no block at all instead of a block of zeros that would read as a measurement.
        var outcome = new ArmOutcome
        {
            Parameters = plan.Parameters(),
            Metrics = new LatencyMetrics
            {
                Tcp = plan.UseTcp ? TcpMetrics(tcp, lanes.Tcp, plan, elapsedTicks) : null,
                Udp = plan.UseUdp ? UdpMetrics(udp, plan, elapsedTicks) : null,
            },
        };

        var validity = new MeasurementValidity(plan, lanes, tcp, udp, elapsedTicks);
        WriteGates(outcome, tcp, udp, validity);
        WriteNotes(outcome, plan, tcp, udp, validity, elapsedTicks);
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
    /// What one lane tells the engine about itself: the pace and deadline it shares with every other
    /// lane, its own share of the deferred backlog, and the two buffers the engine reuses for every
    /// slot. The receive buffer holds one whole frame plus the slack a non-frame datagram is read into.
    /// </summary>
    private static LaneEngineOptions LaneOptions(LatencyPlan plan, long startTicks, long deadlineTicks) => new()
    {
        RatePerSecond = plan.Rate,
        StartTicks = startTicks,
        DeadlineTicks = deadlineTicks,
        BacklogLimit = plan.BacklogLimit,
        SendBufferBytes = FrameCodec.HeaderSize + plan.PayloadBytes + FrameCodec.TrailerSize,
        ReceiveBufferBytes = FrameCodec.HeaderSize + plan.PayloadBytes + FrameCodec.TrailerSize + ReceiveSlack,

        // The arm's own bound, passed rather than left to the engine's default, so the record's note
        // and the bound the drain actually ran with cannot drift apart.
        DrainLimitTicks = Clock.FromSeconds(DrainSeconds),
    };

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

    private static LatencyTcpMetrics TcpMetrics(LatencyTotals state, List<Lane<LatencyTcpState>> laneStates, LatencyPlan plan, long elapsedTicks)
    {
        var laneSupplied = new long[laneStates.Count];
        var laneSentOk = new long[laneStates.Count];
        for (var lane = 0; lane < laneStates.Count; lane++)
        {
            laneSupplied[lane] = laneStates[lane].Counts.Supplied;
            laneSentOk[lane] = laneStates[lane].Counts.SentOk;
        }

        return new LatencyTcpMetrics
        {
            LaneStarted = state.Started,
            LaneSupplied = laneSupplied,
            LaneSentOk = laneSentOk,
            Supplied = state.Supplied,
            Sent = state.SentOk,
            SendWouldBlock = state.SendWouldBlock,
            WindowOverflow = state.WindowOverflow,
            BacklogDrops = state.BacklogDrops,
            SendFailures = state.SendFailures,
            AbandonedAtTeardown = Math.Max(0, state.Supplied - state.SentOk - state.SendFailures - state.BacklogDrops),
            ClientSendLoss = state.ClientSendLoss,
            Received = state.Received,
            OutstandingAtTeardown = state.Outstanding,
            Corrupt = state.Corrupt,
            ProtocolErrors = state.ProtocolErrors,
            RemoteClosed = state.RemoteClosed,
            UnmatchedReplies = state.UnmatchedReplies,
            WindowCeilingMs = InFlightCeilingMs(plan.InFlightWindow, plan.Lanes, state.SentOk, elapsedTicks),
            ScheduleTruncated = state.Truncated ? 1L : 0L,
            AchievedRate = JsonPerSecond.PerSecond(state.SentOk, elapsedTicks, Stopwatch.Frequency),
            ConnectAttempts = state.ConnectSamples + state.ConnectFailures,
            ConnectFailures = state.ConnectFailures,
            MeanConnectMs = state.ConnectSamples == 0
                ? null
                : NumberFormat.Round(Clock.ToMicroseconds(state.ConnectTicks) / (double)state.ConnectSamples / 1000.0),
        };
    }

    private static LatencyUdpMetrics UdpMetrics(LatencyTotals state, LatencyPlan plan, long elapsedTicks) => new()
    {
        LaneStarted = state.Started,
        Supplied = state.Supplied,
        Sent = state.SentOk,
        SendWouldBlock = state.SendWouldBlock,
        WindowOverflow = state.WindowOverflow,
        BacklogDrops = state.BacklogDrops,
        SendFailures = state.SendFailures,
        AbandonedAtTeardown = Math.Max(0, state.Supplied - state.SentOk - state.SendFailures - state.BacklogDrops),
        ClientSendLoss = state.ClientSendLoss,
        Received = state.Received,
        Corrupt = state.Corrupt,
        ProtocolErrors = state.ProtocolErrors,
        UnmatchedReplies = state.UnmatchedReplies,
        ForeignConnection = state.ForeignConnection,
        OutstandingAtTeardown = state.Outstanding,
        WindowCeilingMs = InFlightCeilingMs(plan.InFlightWindow, 1, state.SentOk, elapsedTicks),
        ScheduleTruncated = state.Truncated ? 1L : 0L,

        // Sent minus matched arrivals, never sent minus received, which duplicates can exceed.
        LossRate = JsonRate.Rate(state.SentOk - (state.Received - state.UnmatchedReplies), state.SentOk),
        AchievedRate = JsonPerSecond.PerSecond(state.SentOk, elapsedTicks, Stopwatch.Frequency),
    };

    private static void WriteGates(ArmOutcome outcome, LatencyTotals tcp, LatencyTotals udp, MeasurementValidity validity)
    {
        outcome.Gates[ArmKeys.Common.Gates.ClientSendLoss] = tcp.ClientSendLoss + udp.ClientSendLoss;
        outcome.Gates[ArmKeys.Common.Gates.WindowOverflow] = tcp.WindowOverflow + udp.WindowOverflow;
        outcome.Gates[ArmKeys.Common.Gates.BacklogDrops] = tcp.BacklogDrops + udp.BacklogDrops;
        outcome.Gates[ArmKeys.Common.Gates.SendFailures] = tcp.SendFailures + udp.SendFailures;
        outcome.Gates[ArmKeys.Common.Gates.LanesPlanned] = validity.PlannedLanes;
        outcome.Gates[ArmKeys.Common.Gates.LanesStarted] = validity.StartedLanes;
        outcome.Gates[ArmKeys.Common.Gates.LaneShortfall] = validity.LaneShortfall;
        outcome.Gates[ArmKeys.Common.Gates.ScheduleTruncated] = validity.Truncated ? 1L : 0L;
        outcome.Gates[ArmKeys.Common.Gates.InFlightCeilingMs] = TightestCeiling(validity.TcpCeilingMs, validity.UdpCeilingMs);
        outcome.Gates[ArmKeys.Common.Gates.WindowMs] = 0L;
    }

    private static void WriteNotes(
        ArmOutcome outcome,
        LatencyPlan plan,
        LatencyTotals tcp,
        LatencyTotals udp,
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
                $"in-flight ceiling (tcp): {plan.Lanes} lane(s) at {RateText(JsonPerSecond.PerSecond(tcp.SentOk, elapsedTicks, Stopwatch.Frequency))} requests/s aggregate achieved with inFlightWindow {plan.InFlightWindow} per lane put the measurable direct-latency ceiling at {validity.TcpCeilingMs:F1} ms; slower requests are measured through the deferred queue, and the ceiling was {(tcp.WindowOverflow > 0 ? "reached" : "not reached")} (windowOverflow = {tcp.WindowOverflow})."));
        }

        if (plan.UseUdp)
        {
            outcome.Notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"in-flight ceiling (udp): 1 lane at {RateText(JsonPerSecond.PerSecond(udp.SentOk, elapsedTicks, Stopwatch.Frequency))} requests/s achieved with inFlightWindow {plan.InFlightWindow} put the measurable direct-latency ceiling at {validity.UdpCeilingMs:F1} ms; slower requests are measured through the deferred queue, and the ceiling was {(udp.WindowOverflow > 0 ? "reached" : "not reached")} (windowOverflow = {udp.WindowOverflow})."));
        }

        outcome.Notes.Add(string.Concat(
            "lanes actually run: ",
            plan.UseTcp ? string.Create(CultureInfo.InvariantCulture, $"tcp {tcp.Started} of {plan.Lanes}") : string.Empty,
            // ReSharper disable once MergeIntoPattern // Two independent plan flags read better as a conjunction than as a property pattern.
            plan.UseTcp && plan.UseUdp ? ", " : string.Empty,
            plan.UseUdp ? string.Create(CultureInfo.InvariantCulture, $"udp {udp.Started} of 1") : string.Empty,
            "; per-lane supplied and sent counts are published as tcp.laneSupplied and tcp.laneSentOk."));

        if (tcp.Truncated || udp.Truncated)
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

    /// <summary>
    /// The arm's published counters as one value: the engine's send-side snapshot and the policy's own
    /// book, joined per lane and then summed. It is the sum and nothing else — the engine owns what was
    /// offered and sent, the policy owns what came back — so a published number can never drift from
    /// the counter that produced it.
    /// </summary>
    private sealed class LatencyTotals
    {
        internal long Started { get; private set; }

        internal long Supplied { get; private set; }

        internal long SentOk { get; private set; }

        internal long SendWouldBlock { get; private set; }

        internal long WindowOverflow { get; private set; }

        internal long BacklogDrops { get; private set; }

        internal long SendFailures { get; private set; }

        /// <summary>Unanswered requests plus deferred intents that never got a slot, when the lane stopped.</summary>
        internal long Outstanding { get; private set; }

        internal long Received { get; private set; }

        internal long Corrupt { get; private set; }

        internal long UnmatchedReplies { get; private set; }

        internal long ForeignConnection { get; private set; }

        internal long RemoteClosed { get; private set; }

        internal long ProtocolErrors { get; private set; }

        internal long ConnectSamples { get; private set; }

        internal long ConnectFailures { get; private set; }

        internal long ConnectTicks { get; private set; }

        internal bool Truncated { get; private set; }

        /// <summary>
        /// Requests that reached no latency histogram: supplied minus sent, which only counts samples
        /// the client itself destroyed.
        /// </summary>
        internal long ClientSendLoss => Math.Max(0, Supplied - SentOk);

        internal void AddTcp(Lane<LatencyTcpState> lane)
        {
            AddCounts(lane.Counts);
            var book = lane.Book;
            Started += book.Started;
            Received += book.Received;
            Corrupt += book.Corrupt;
            ProtocolErrors += book.ProtocolErrors;
            RemoteClosed += book.RemoteClosed;
            UnmatchedReplies += book.UnmatchedReplies;
            AddConnect(book);
        }

        internal void AddUdp(Lane<UdpLatencyState> lane)
        {
            AddCounts(lane.Counts);
            var book = lane.Book;
            Started += book.Started;
            Received += book.Received;
            Corrupt += book.Corrupt;
            ProtocolErrors += book.ProtocolErrors;
            ForeignConnection += book.ForeignConnection;
            UnmatchedReplies += book.UnmatchedReplies;
        }

        /// <summary>Adds the policy half of <c>outstandingAtTeardown</c>: requests no reply consumed.</summary>
        internal void AddPending(long pending) => Outstanding += pending;

        /// <summary>The connect probe is not a lane: it has connect facts and no engine counts at all.</summary>
        internal void AddProbe(LatencyTcpState probe)
        {
            ConnectSamples += probe.ConnectSamples;
            ConnectFailures += probe.ConnectFailures;
            ConnectTicks += probe.ConnectTicks;
        }

        private void AddCounts(LaneCounts counts)
        {
            Supplied += counts.Supplied;
            SentOk += counts.SentOk;
            SendWouldBlock += counts.SendWouldBlock;
            SendFailures += counts.SendFailures;

            // D18.5 #7: the engine's two defer counters are the contract's two defer keys, by name and
            // by value; the arm does not keep a second pair.
            WindowOverflow += counts.DeferredQueued;
            BacklogDrops += counts.DeferredDropped;

            // The other half of outstandingAtTeardown: intents the window never let out (D18.6 #1).
            Outstanding += counts.DeferredPending;
            Truncated |= counts.ScheduleTruncated;
        }

        private void AddConnect(LatencyTcpState book)
        {
            ConnectSamples += book.ConnectSamples;
            ConnectFailures += book.ConnectFailures;
            ConnectTicks += book.ConnectTicks;
        }

    }

    private static LatencyTotals TotalTcp(List<Lane<LatencyTcpState>> lanes, LatencyTcpState probe)
    {
        var total = new LatencyTotals();
        foreach (var lane in lanes)
        {
            total.AddTcp(lane);
            total.AddPending(lane.Book.Pending);
        }

        total.AddProbe(probe);
        return total;
    }

    private static LatencyTotals TotalUdp(Lane<UdpLatencyState> lane)
    {
        var total = new LatencyTotals();
        total.AddUdp(lane);
        total.AddPending(lane.Book.Pending);
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
        Lane<LatencyTcpState> lane,
        int laneIndex,
        LatencyPlan plan,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        lane.Book.Started = 1;
        var connectionId = TcpConnectionIdBase + (uint)laneIndex;
        var options = LaneOptions(plan, startTicks, deadlineTicks);

        // The transport owns the socket: it connects, sends the lane's command frame and reads the
        // stream, and the engine disposes nothing.
        using var transport = new TcpLaneTransport(context.CreateTcpSocket(), context.TcpEndPoint, connectionId);
        var policy = new LatencyTcpPolicy(lane.Book, connectionId, plan.PayloadBytes, plan.InFlightWindow, context.Latency.TcpRtt);
        var engine = new LaneEngine<TcpLaneTransport>(transport, policy, options);
        lane.Counts = await engine.RunAsync(cancellationToken).ConfigureAwait(false);
        lane.Book.BookConnect(transport.ConnectOk, transport.ConnectTicks);
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
                var open = await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false);
                var end = Clock.Now;
                state.BookConnect(open.Ok, end - begin);
                if (!open.Ok)
                {
                    index++;
                    continue;
                }

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
        Lane<UdpLatencyState> lane,
        LatencyPlan plan,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        lane.Book.Started = 1;
        var options = LaneOptions(plan, startTicks, deadlineTicks);
        using var transport = new UdpLaneTransport(context.CreateUdpSocket(), context.UdpEndPoint, options.ReceiveBufferBytes);
        var policy = new UdpLatencyPolicy(lane.Book, UdpConnectionId, plan.PayloadBytes, plan.InFlightWindow, context.Latency.UdpRtt);
        var engine = new LaneEngine<UdpLaneTransport>(transport, policy, options);
        lane.Counts = await engine.RunAsync(cancellationToken).ConfigureAwait(false);

        // A udp lane books no connect facts: the record publishes them under tcp.* only (a udp connect
        // has no handshake to time), so a failed open shows up the way a tcp lane's does -- through
        // scheduleTruncated and laneShortfall -- instead of inflating the tcp connect population.
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

        /// <summary>
        /// The load this arm actually ran with, after its own defaults were applied.
        /// </summary>
        internal ArmParameters Parameters() => new()
        {
            Seconds = Seconds,
            RatePerSecond = Rate,
            PayloadBytes = PayloadBytes,
            Protocol = Protocol,
            Lanes = Lanes,

            // in-flight requests per lane; the loss arm's window is a millisecond threshold, so the
            // two arms must not publish one bare "window" that means different things.
            InFlightWindow = InFlightWindow,
        };

        private static int BacklogCapacity(int rate, int window) =>
            (int)Math.Min(MaxBacklogPerLane, Math.Max((long)window * 4, (long)rate * BacklogSeconds));
    }

    /// <summary>
    /// One lane's two halves, kept apart until the totals are formed: the engine's send-side snapshot
    /// and the policy's own book. Neither half is reachable from the other, which is what the
    /// disjointness contract (D18.1) asks for.
    /// </summary>
    private sealed class Lane<TBook>
        where TBook : new()
    {
        internal TBook Book { get; } = new();

        internal LaneCounts Counts { get; set; }
    }

    /// <summary>
    /// Per-lane containers: each lane writes only its own state and the totals are formed after the
    /// lanes join.
    /// </summary>
    private sealed class LaneStates
    {
        internal LaneStates(LatencyPlan plan)
        {
            // States exist before any lane starts, so a lane that never runs still appears in the
            // per-lane arrays instead of shrinking them.
            for (var lane = 0; plan.UseTcp && lane < plan.Lanes; lane++)
            {
                Tcp.Add(new Lane<LatencyTcpState>());
            }
        }

        internal List<Lane<LatencyTcpState>> Tcp { get; } = [];

        internal LatencyTcpState Probe { get; } = new();

        internal Lane<UdpLatencyState> Udp { get; } = new();
    }

    /// <summary>
    /// Whether the record measured what it offered: lanes run, schedule shortfall, and the ceiling
    /// on directly measurable latency. Derived once so gates and notes cannot disagree.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly struct MeasurementValidity
    {
        internal MeasurementValidity(LatencyPlan plan, LaneStates lanes, LatencyTotals tcp, LatencyTotals udp, long elapsedTicks)
        {
            PlannedLanes = (plan.UseTcp ? plan.Lanes : 0) + (plan.UseUdp ? 1 : 0);
            StartedLanes = (plan.UseTcp ? tcp.Started : 0) + (plan.UseUdp ? udp.Started : 0);
            var idleLanes = (plan.UseTcp ? CountIdleLanes(lanes.Tcp) : 0) + (IsIdle(udp.Started, udp.Supplied) ? 1 : 0);
            LaneShortfall = PlannedLanes - StartedLanes + idleLanes;
            TcpCeilingMs = plan.UseTcp ? InFlightCeilingMs(plan.InFlightWindow, plan.Lanes, tcp.SentOk, elapsedTicks) : 0;
            UdpCeilingMs = plan.UseUdp ? InFlightCeilingMs(plan.InFlightWindow, 1, udp.SentOk, elapsedTicks) : 0;
            Truncated = tcp.Truncated || udp.Truncated || LaneShortfall > 0;
        }

        internal long PlannedLanes { get; }

        internal long StartedLanes { get; }

        internal long LaneShortfall { get; }

        internal double TcpCeilingMs { get; }

        internal double UdpCeilingMs { get; }

        internal bool Truncated { get; }

        private static bool IsIdle(long started, long supplied) => started > 0 && supplied == 0;

        private static long CountIdleLanes(List<Lane<LatencyTcpState>> lanes)
        {
            long idle = 0;
            foreach (var lane in lanes)
            {
                idle += IsIdle(lane.Book.Started, lane.Counts.Supplied) ? 1 : 0;
            }

            return idle;
        }
    }
}
